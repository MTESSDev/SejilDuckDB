using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using SejilDuckDB.Logging;
using SejilDuckDB.Query;
using SejilDuckDB.Storage;
using Serilog;
using Serilog.Events;
using Serilog.Parsing;
using Storage = SejilDuckDB.Storage;

namespace SejilDuckDB.Tests;

public class SerilogSinkTests
{
    private static readonly MessageTemplateParser Parser = new();

    private static Serilog.Events.LogEvent Event(string template, LogEventLevel level = LogEventLevel.Information, Exception? exception = null, params (string Name, object? Value)[] properties)
    {
        var props = properties.Select(p => new LogEventProperty(p.Name, p.Value is LogEventPropertyValue v ? v : new ScalarValue(p.Value)));
        return new Serilog.Events.LogEvent(new DateTimeOffset(2026, 10, 7, 18, 30, 0, TimeSpan.FromHours(-4)), level, exception, Parser.Parse(template), props);
    }

    // ---------- conversion: the same text as the /log route ----------

    [Fact]
    public void An_event_keeps_its_time_level_message_and_exception()
    {
        var converted = SerilogEventConverter.ToEvent(Event("Order {Id} failed", LogEventLevel.Error, new InvalidOperationException("boom"), ("Id", 42)));

        Assert.Equal(new DateTimeOffset(2026, 10, 7, 18, 30, 0, TimeSpan.FromHours(-4)), converted.Timestamp);
        Assert.Equal(4, converted.Level);
        Assert.Equal("Order 42 failed", converted.Message);
        Assert.Contains("InvalidOperationException: boom", converted.Exception);
    }

    [Fact]
    public void An_event_with_no_properties_and_no_exception_has_neither()
    {
        var converted = SerilogEventConverter.ToEvent(Event("hello"));

        Assert.Null(converted.Properties);
        Assert.Null(converted.Exception);
    }

    [Fact]
    public void Property_values_are_the_text_the_sender_wrote_whatever_the_culture_of_the_machine()
    {
        var before = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("fr-CA"); // decimal comma, "Vrai"
        try
        {
            var converted = SerilogEventConverter.ToEvent(Event("Amount {Amount}", properties: new (string, object?)[]
            {
                ("Amount", 12.5m), ("Ok", true), ("Name", "bob"), ("When", new DateTime(2026, 10, 7, 21, 48, 0, DateTimeKind.Utc)), ("Nothing", null),
            }));

            Assert.Equal("Amount 12.5", converted.Message);
            Assert.Equal("12.5", converted.Properties!["Amount"]);
            Assert.Equal("true", converted.Properties["Ok"]);
            Assert.Equal("bob", converted.Properties["Name"]);          // a string as is, no quotes
            Assert.Equal("2026-10-07T21:48:00.0000000Z", converted.Properties["When"]);
            Assert.Null(converted.Properties["Nothing"]);
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    [Fact]
    public void A_structure_is_stored_as_its_json_text()
    {
        var order = new StructureValue(new[] { new LogEventProperty("Id", new ScalarValue(5)), new LogEventProperty("Lines", new SequenceValue(new[] { new ScalarValue("a"), new ScalarValue("b") })) });

        var converted = SerilogEventConverter.ToEvent(Event("x", properties: ("Order", order)));

        Assert.Equal("{\"Id\":5,\"Lines\":[\"a\",\"b\"]}", converted.Properties!["Order"]);
    }

    // ---------- the real files ----------

    [Fact]
    public async Task The_sink_writes_to_the_files_and_the_viewer_finds_what_it_wrote()
    {
        using var w = new Workspace();
        var logger = new LoggerConfiguration()
            .WriteTo.SejilDuck(w.Directory, "Billing")
            .CreateLogger();

        logger.Information("Order {OrderId} created for {UserName}", 42, "bob");
        logger.Error(new InvalidOperationException("The card was refused."), "Payment failed for {OrderId}", 42);
        logger.Dispose(); // flushes

        var page = await w.SearchAsync();

        Assert.Equal(2, page.Events.Count);
        var error = page.Events.Single(e => e.Level == "Error");
        Assert.Equal("Billing", error.SourceApp);
        Assert.Equal("Payment failed for 42", error.Message);
        Assert.Contains("The card was refused.", error.Exception);
        Assert.Equal("42", error.Properties.Single(p => p.Name == "OrderId").Value);
        Assert.Equal(new[] { "Order 42 created for \"bob\"" }, page.Events.Where(e => e.Level == "Information").Select(e => e.Message)); // Serilog renders strings in quotes
        Assert.Equal(new[] { "Order 42 created for \"bob\"" }.Length, (await w.SearchAsync("UserName = bob")).Events.Count);
    }

    [Fact]
    public void The_folder_is_required()
    {
        Assert.Throws<ArgumentException>(() => new LoggerConfiguration().WriteTo.SejilDuck(" "));
    }

    [Fact]
    public void Without_AddSejilDuck_the_error_says_what_to_do()
    {
        var empty = new ServiceCollection().BuildServiceProvider();

        var e = Assert.Throws<InvalidOperationException>(() => new LoggerConfiguration().WriteTo.SejilDuck(empty));

        Assert.Contains("AddSejilDuck", e.Message);
    }

    [Fact]
    public async Task Two_writers_on_the_same_folder_never_hand_out_the_same_id()
    {
        using var w = new Workspace();
        var other = new LogFileStore(w.Directory); // a second store: a sink built from a folder, beside the site's
        var now = DateTimeOffset.Now;

        async Task Write(LogFileStore store, string prefix)
        {
            for (var batch = 0; batch < 10; batch++)
                Assert.True(await store.TryAppendAsync(Enumerable.Range(0, 50).Select(i => Workspace.Event(now.AddMilliseconds(-i), $"{prefix}{batch}-{i}")).ToArray(), prefix));
        }

        await Task.WhenAll(Write(w.Store, "a"), Write(other, "b"));

        var page = await w.Reader.QueryAsync(new LogQueryFilter(), null, 1000);
        Assert.Equal(1000, page.Events.Count);
        Assert.Equal(1000, page.Events.Select(e => e.Id).Distinct().Count());
    }

    // ---------- the same application: the site, the sink, and the HTTP route together ----------

    private sealed class Site : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "sejilduck-same-" + Guid.NewGuid().ToString("N"));
        public WebApplication App { get; private set; } = null!;
        public HttpClient Client { get; private set; } = null!;

        public static async Task<Site> StartAsync()
        {
            var site = new Site();
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.AddSejilDuck(o => { o.DataDirectory = site._directory; o.NodeName = "srv-a"; });
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
            try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
        }
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private static async Task<LogPage> Search(HttpClient client, string body = "{}")
    {
        var response = await client.PostAsync("/logs/events?pageSize=500", Json(body));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<LogPage>(new JsonSerializerOptions(JsonSerializerDefaults.Web)))!;
    }

    [Fact]
    public async Task The_site_can_write_its_own_logs_through_the_sink_while_it_still_receives_the_logs_of_others_over_http()
    {
        await using var site = await Site.StartAsync();

        // the application's own logger: Serilog -> sink -> the site's files, no HTTP
        var logger = new LoggerConfiguration().WriteTo.SejilDuck(site.App.Services, "TheSite").CreateLogger();
        logger.Information("The site started on {Machine}", "srv-a");
        logger.Warning("Disk at {Percent} percent", 91);

        // another application: Serilog.Sinks.Http -> POST /log
        var batch = JsonSerializer.Serialize(new[]
        {
            new Dictionary<string, object> { ["Timestamp"] = DateTimeOffset.Now.ToString("o"), ["Level"] = "Error", ["RenderedMessage"] = "Payment failed", ["Properties"] = new Dictionary<string, object> { ["OrderId"] = 42 } },
        });
        Assert.True((await site.Client.PostAsync("/log?sourceApp=Billing", Json(batch))).IsSuccessStatusCode);

        logger.Dispose(); // flushes the sink

        var page = await Search(site.Client);

        Assert.Equal(3, page.Events.Count);
        Assert.Equal(new[] { "Billing", "TheSite", "TheSite" }.Order(), page.Events.Select(e => e.SourceApp).Order());
        Assert.Equal(3, page.Events.Select(e => e.Id).Distinct().Count());
        Assert.All(page.Events, e => Assert.Equal("srv-a", e.Node));

        // and the viewer can tell them apart
        Assert.Equal(new[] { "Disk at 91 percent" }, (await Search(site.Client, """{"queryText":"sourceApp = TheSite and level = Warning"}""")).Events.Select(e => e.Message));
        Assert.Equal(new[] { "Payment failed" }, (await Search(site.Client, """{"queryText":"sourceApp = Billing"}""")).Events.Select(e => e.Message));
    }

    [Fact]
    public async Task Both_ways_leave_lines_that_look_the_same()
    {
        await using var site = await Site.StartAsync();
        var logger = new LoggerConfiguration().WriteTo.SejilDuck(site.App.Services, "Local").CreateLogger();
        logger.Information("Amount {Amount} for {Name} ok={Ok}", 12.5m, "bob", true);
        logger.Dispose();

        var batch = JsonSerializer.Serialize(new[]
        {
            new Dictionary<string, object>
            {
                ["Timestamp"] = DateTimeOffset.Now.ToString("o"), ["Level"] = "Information", ["RenderedMessage"] = "Amount 12.5 for \"bob\" ok=True",
                ["Properties"] = new Dictionary<string, object> { ["Amount"] = 12.5m, ["Name"] = "bob", ["Ok"] = true },
            },
        });
        await site.Client.PostAsync("/log?sourceApp=Remote", Json(batch));

        var page = await Search(site.Client);
        var local = page.Events.Single(e => e.SourceApp == "Local");
        var remote = page.Events.Single(e => e.SourceApp == "Remote");

        Assert.Equal(local.Message, remote.Message);
        Assert.Equal(
            local.Properties.Select(p => $"{p.Name}={p.Value}"),
            remote.Properties.Select(p => $"{p.Name}={p.Value}"));   // 12.5, bob, true: the same text either way
    }
}
