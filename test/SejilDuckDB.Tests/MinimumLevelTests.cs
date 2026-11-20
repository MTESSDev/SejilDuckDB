using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using SejilDuckDB.Query;
using SejilDuckDB.Storage;
using Serilog;

namespace SejilDuckDB.Tests;

/// <summary>
/// The minimum log level is not just something the viewer shows: the site applies it. Warning by default, so that Information and
/// Debug are not written unless somebody asks for them (in the viewer, or with <c>MinimumLogLevel</c>).
/// </summary>
public class MinimumLevelTests
{
    private sealed class Site : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "sejilduck-level-" + Guid.NewGuid().ToString("N"));
        public WebApplication App { get; private set; } = null!;
        public HttpClient Client { get; private set; } = null!;
        public string Directory => _directory;

        public static async Task<Site> StartAsync(Action<SejilDuckOptions>? configure = null)
        {
            var site = new Site();
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            // nothing about the level is set here unless a test says so: this is the default of the product
            builder.Services.AddSejilDuck(o => { o.DataDirectory = site._directory; configure?.Invoke(o); });
            site.App = builder.Build();
            site.App.MapSejilDuck();
            await site.App.StartAsync();
            site.Client = site.App.GetTestClient();
            return site;
        }

        public async ValueTask DisposeAsync()
        {
            await App.StopAsync();
            await App.DisposeAsync();
            WriteQueue.Forget(_directory);
            MinimumLevelStore.Forget(_directory);
            try { System.IO.Directory.Delete(_directory, recursive: true); } catch (IOException) { }
        }

        // One event per level, as Serilog.Sinks.Http would send them.
        public async Task<HttpStatusCode> PostEachLevelAsync(string app = "App")
        {
            var batch = JsonSerializer.Serialize(new[] { "Verbose", "Debug", "Information", "Warning", "Error", "Fatal" }.Select(level => new Dictionary<string, object>
            {
                ["Timestamp"] = DateTimeOffset.Now.ToString("o"), ["Level"] = level, ["RenderedMessage"] = level,
            }));
            return (await Client.PostAsync($"/log?sourceApp={app}", new StringContent(batch, Encoding.UTF8, "application/json"))).StatusCode;
        }

        public async Task<string[]> StoredLevelsAsync()
        {
            var response = await Client.PostAsync("/logs/events?pageSize=500", new StringContent("{}", Encoding.UTF8, "application/json"));
            var page = (await response.Content.ReadFromJsonAsync<LogPage>(new JsonSerializerOptions(JsonSerializerDefaults.Web)))!;
            return page.Events.Select(e => e.Level).Order().ToArray();
        }

        public async Task SetLevelAsync(string level)
            => Assert.Equal(HttpStatusCode.OK, (await Client.PostAsync("/logs/min-log-level", new StringContent(level))).StatusCode);

        public async Task<string?> LevelAsync()
            => (await Client.GetFromJsonAsync<JsonElement>("/logs/min-log-level")).GetProperty("minimumLogLevel").GetString();
    }

    [Fact]
    public async Task Without_a_config_file_the_level_is_Warning_and_that_is_what_the_site_says()
    {
        await using var site = await Site.StartAsync();

        Assert.False(File.Exists(Path.Combine(site.Directory, "config.json")));
        Assert.Equal("Warning", await site.LevelAsync());
    }

    [Fact]
    public async Task By_default_the_route_does_not_keep_what_is_below_Warning_but_answers_200()
    {
        await using var site = await Site.StartAsync();

        var status = await site.PostEachLevelAsync();

        Assert.Equal(HttpStatusCode.OK, status);                         // nothing was wrong with the batch: the sender must not send it again
        Assert.Equal(new[] { "Error", "Fatal", "Warning" }, await site.StoredLevelsAsync());   // no Verbose, Debug or Information
    }

    [Fact]
    public async Task A_batch_made_only_of_events_below_the_level_is_accepted_and_writes_nothing()
    {
        await using var site = await Site.StartAsync();
        var batch = JsonSerializer.Serialize(new[] { new Dictionary<string, object> { ["Timestamp"] = DateTimeOffset.Now.ToString("o"), ["Level"] = "Debug", ["RenderedMessage"] = "noise" } });

        var response = await site.Client.PostAsync("/log?sourceApp=App", new StringContent(batch, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await site.StoredLevelsAsync());
        Assert.Empty(Directory.GetFiles(site.Directory, "log-*"));
    }

    [Fact]
    public async Task By_default_the_local_sink_applies_the_same_level()
    {
        await using var site = await Site.StartAsync();
        var logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.SejilDuck(site.App.Services, "Local").CreateLogger();

        logger.Verbose("v"); logger.Debug("d"); logger.Information("i"); logger.Warning("w"); logger.Error("e"); logger.Fatal("f");
        logger.Dispose();

        Assert.Equal(new[] { "Error", "Fatal", "Warning" }, await site.StoredLevelsAsync());
    }

    [Fact]
    public async Task The_same_applies_to_a_sink_built_from_the_folder_without_the_site()
    {
        using var w = new Workspace();
        File.Delete(Path.Combine(w.Directory, "config.json")); // no config: the folder's own default
        MinimumLevelStore.Forget(w.Directory);
        WriteQueue.Forget(w.Directory);
        var logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.SejilDuck(w.Directory, "Local").CreateLogger();

        logger.Information("below"); logger.Warning("above");
        logger.Dispose();

        var store = new LogFileStore(w.Directory);
        Assert.Equal(new[] { "above" }, (await new LogReader(store, Microsoft.Extensions.Options.Options.Create(w.Options)).QueryAsync(new LogQueryFilter(), null, 10)).Events.Select(e => e.Message));
    }

    [Fact]
    public async Task Lowering_the_level_in_the_viewer_lets_Information_and_Debug_through_at_once()
    {
        await using var site = await Site.StartAsync();
        var logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.SejilDuck(site.App.Services, "Local").CreateLogger();

        await site.SetLevelAsync("Debug");
        await site.PostEachLevelAsync();
        logger.Debug("local debug"); logger.Verbose("local verbose");
        logger.Dispose();

        Assert.Equal("Debug", await site.LevelAsync());
        // Debug, Information, Warning, Error, Fatal from the route; the local Debug too; not the Verbose of either
        Assert.Equal(new[] { "Debug", "Debug", "Error", "Fatal", "Information", "Warning" }, await site.StoredLevelsAsync());
    }

    [Fact]
    public async Task Raising_the_level_stops_what_was_kept_before_at_once()
    {
        await using var site = await Site.StartAsync();
        await site.SetLevelAsync("Error");

        await site.PostEachLevelAsync();

        Assert.Equal(new[] { "Error", "Fatal" }, await site.StoredLevelsAsync());
    }

    [Fact]
    public async Task The_level_chosen_in_the_viewer_is_kept_in_the_file_and_survives_a_restart()
    {
        await using var site = await Site.StartAsync();
        await site.SetLevelAsync("Information");

        Assert.Contains("Information", File.ReadAllText(Path.Combine(site.Directory, "config.json")));

        // a restart: the process forgets what it held in memory
        MinimumLevelStore.Forget(site.Directory);
        Assert.Equal(2, MinimumLevelStore.For(site.Directory).Level);
    }

    [Fact]
    public async Task An_option_sets_the_level_that_applies_until_somebody_changes_it()
    {
        await using var site = await Site.StartAsync(o => o.MinimumLogLevel = "Information");

        await site.PostEachLevelAsync();

        Assert.Equal("Information", await site.LevelAsync());
        Assert.Equal(new[] { "Error", "Fatal", "Information", "Warning" }, await site.StoredLevelsAsync());
    }

    // ---------- the store of the level, without the site ----------

    private static string NewFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "sejilduck-levelstore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    [Fact]
    public void Without_a_file_the_default_given_by_the_first_user_of_the_folder_applies()
    {
        var folder = NewFolder();
        try
        {
            Assert.Equal(3, MinimumLevelStore.For(folder).Level);
            MinimumLevelStore.Forget(folder);
            Assert.Equal(0, MinimumLevelStore.For(folder, defaultLevel: 0).Level);
        }
        finally
        {
            MinimumLevelStore.Forget(folder);
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task A_change_made_by_another_process_or_by_hand_is_seen_within_the_refresh_time()
    {
        var folder = NewFolder();
        try
        {
            var level = MinimumLevelStore.For(folder, refreshEvery: TimeSpan.FromMilliseconds(40));
            Assert.Equal(3, level.Level);

            await File.WriteAllTextAsync(Path.Combine(folder, "config.json"), """{"minimumLogLevel":"Debug"}""");
            await Task.Delay(120);
            Assert.Equal(1, level.Level);

            File.Delete(Path.Combine(folder, "config.json"));   // deleted: back to the default
            await Task.Delay(120);
            Assert.Equal(3, level.Level);
        }
        finally
        {
            MinimumLevelStore.Forget(folder);
            Directory.Delete(folder, true);
        }
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("""{"minimumLogLevel":"Loud"}""")]
    [InlineData("""{"minimumLogLevel":42}""")]
    [InlineData("""[]""")]
    [InlineData("""{}""")]
    [InlineData("")]
    public void A_file_that_cannot_be_read_or_names_no_level_means_the_default(string content)
    {
        var folder = NewFolder();
        try
        {
            File.WriteAllText(Path.Combine(folder, "config.json"), content);

            Assert.Equal(3, MinimumLevelStore.For(folder).Level);
        }
        finally
        {
            MinimumLevelStore.Forget(folder);
            Directory.Delete(folder, true);
        }
    }

    [Theory]
    [InlineData("""{"minimumLogLevel":"Verbose"}""", 0)]
    [InlineData("""{"minimumLogLevel":"error"}""", 4)]
    [InlineData("""{"MinimumLogLevel":"Information"}""", 2)]   // the case of the name does not matter
    [InlineData("""{"minimumLogLevel":"Critical"}""", 5)]
    public void The_level_is_read_from_the_file(string content, int expected)
    {
        var folder = NewFolder();
        try
        {
            File.WriteAllText(Path.Combine(folder, "config.json"), content);

            Assert.Equal(expected, MinimumLevelStore.For(folder).Level);
        }
        finally
        {
            MinimumLevelStore.Forget(folder);
            Directory.Delete(folder, true);
        }
    }
}
