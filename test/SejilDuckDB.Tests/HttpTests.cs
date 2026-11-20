using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using SejilDuckDB.Query;

namespace SejilDuckDB.Tests;

public sealed class HttpTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sejilduck-http-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSejilDuck(o =>
        {
            o.DataDirectory = _directory;
            o.NodeName = "srv-a";
            o.Title = "Test logs";
            o.Nodes.AddRange(new[] { "srv-a", "srv-b" });
        });

        _app = builder.Build();
        _app.MapSejilDuck();
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    // What Serilog.Sinks.Http sends (NormalRenderedTextFormatter).
    private static string Batch(params (string Message, string Level)[] events)
        => JsonSerializer.Serialize(events.Select(e => new Dictionary<string, object>
        {
            ["Timestamp"] = DateTimeOffset.Now.ToString("o"),
            ["Level"] = e.Level,
            ["MessageTemplate"] = e.Message,
            ["RenderedMessage"] = e.Message,
            ["Properties"] = new Dictionary<string, object> { ["UserName"] = "bob", ["Count"] = 3, ["Ok"] = true },
        }));

    private async Task<LogPage> SearchAsync(string body = "{}", string query = "")
    {
        var response = await _client.PostAsync("/logs/events" + query, Json(body));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<LogPage>(new JsonSerializerOptions(JsonSerializerDefaults.Web)))!;
    }

    [Fact]
    public async Task A_batch_posted_by_serilog_is_stored_and_found()
    {
        var post = await _client.PostAsync("/log?sourceApp=Billing", Json(Batch(("Order 42 created", "Information"), ("Payment failed", "Error"))));
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);

        var page = await SearchAsync();

        Assert.Equal(2, page.Events.Count);
        Assert.Equal("srv-a", page.Node);
        Assert.All(page.Events, e => Assert.Equal("srv-a", e.Node));
        Assert.All(page.Events, e => Assert.Equal("Billing", e.SourceApp));
        var failed = page.Events.Single(e => e.Message == "Payment failed");
        Assert.Equal("Error", failed.Level);
        // numbers and booleans are kept as the sender wrote them, whatever the culture of the server
        Assert.Equal("3", failed.Properties.Single(p => p.Name == "Count").Value);
        Assert.Equal("true", failed.Properties.Single(p => p.Name == "Ok").Value);
    }

    [Fact]
    public async Task The_viewer_searches_with_its_filters()
    {
        await _client.PostAsync("/log?sourceApp=A", Json(Batch(("hello world", "Information"), ("something broke", "Error"))));

        var byLevel = await SearchAsync("""{"levelFilter":"Error"}""");
        var byWord = await SearchAsync("""{"queryText":"hello"}""");
        var byProperty = await SearchAsync("""{"queryText":"UserName = bob and level = Error"}""");

        Assert.Equal(new[] { "something broke" }, byLevel.Events.Select(e => e.Message));
        Assert.Equal(new[] { "hello world" }, byWord.Events.Select(e => e.Message));
        Assert.Equal(new[] { "something broke" }, byProperty.Events.Select(e => e.Message));
    }

    [Fact]
    public async Task A_page_comes_with_the_cursor_of_the_next_one()
    {
        await _client.PostAsync("/log?sourceApp=A", Json(Batch(("one", "Information"), ("two", "Information"), ("three", "Information"))));

        var first = await SearchAsync(query: "?pageSize=2");
        Assert.Equal(2, first.Events.Count);
        Assert.False(first.Exhausted);
        Assert.NotNull(first.Resume);

        var second = await SearchAsync(query: $"?pageSize=2&before={Uri.EscapeDataString(first.Resume!.Format())}");
        Assert.Single(second.Events);
        Assert.True(second.Exhausted);
        Assert.Empty(first.Events.Select(e => e.Id).Intersect(second.Events.Select(e => e.Id)));
    }

    [Theory]
    [InlineData("/log")]                              // no sourceApp
    [InlineData("/log?sourceApp=")]
    public async Task A_batch_without_an_application_name_is_refused(string url)
    {
        var response = await _client.PostAsync(url, Json(Batch(("x", "Information"))));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"Timestamp\":\"2026-01-01T00:00:00Z\"}")]                          // not an array
    [InlineData("[{\"Level\":\"Error\"}]")]                                           // no timestamp
    [InlineData("[{\"Timestamp\":\"yesterday\"}]")]                                   // not a date
    [InlineData("[{\"Timestamp\":\"2026-01-01T00:00:00Z\",\"Level\":\"Loud\"}]")]    // unknown level
    [InlineData("[1]")]
    public async Task A_batch_that_cannot_be_read_is_refused_and_nothing_is_stored(string body)
    {
        var response = await _client.PostAsync("/log?sourceApp=A", Json(body));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty((await SearchAsync()).Events);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    public async Task An_empty_batch_is_accepted(string body)
    {
        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsync("/log?sourceApp=A", Json(body))).StatusCode);
    }

    [Fact]
    public async Task A_query_that_cannot_be_read_answers_400_with_the_reason()
    {
        var response = await _client.PostAsync("/logs/events", Json("""{"queryText":"hello UserName = bob"}"""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("hello UserName = bob", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_cursor_that_is_not_one_answers_400()
    {
        var response = await _client.PostAsync("/logs/events?before=garbage", Json("{}"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task The_viewer_is_served_where_it_is_mapped()
    {
        var response = await _client.GetAsync("/logs");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("text/html", response.Content.Headers.ContentType!.MediaType!);
    }

    [Fact]
    public async Task The_servers_to_ask_travel_with_the_page_itself()
    {
        var html = await _client.GetStringAsync("/logs");

        // not fetched afterwards: that call could reach the server that is down
        Assert.DoesNotContain("__SEJIL_CONFIG__", html);
        var start = html.IndexOf("id=\"sejil-config\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "the page has no config element");
        var json = html[(html.IndexOf('>', start) + 1)..html.IndexOf("</script>", start, StringComparison.Ordinal)];
        var config = JsonDocument.Parse(json).RootElement;
        Assert.Equal("x-nlb2", config.GetProperty("header").GetString());
        Assert.Equal(new[] { "srv-a", "srv-b" }, config.GetProperty("nodes").EnumerateArray().Select(n => n.GetString()));
        Assert.Equal("srv-a", config.GetProperty("node").GetString());
    }

    [Fact]
    public async Task The_viewer_learns_which_servers_to_ask_and_with_which_header()
    {
        var nodes = await _client.GetFromJsonAsync<JsonElement>("/logs/nodes");

        Assert.Equal("x-nlb2", nodes.GetProperty("header").GetString());
        Assert.Equal(new[] { "srv-a", "srv-b" }, nodes.GetProperty("nodes").EnumerateArray().Select(n => n.GetString()));
        Assert.Equal("srv-a", nodes.GetProperty("node").GetString());
        Assert.Equal("Test logs", (await _client.GetFromJsonAsync<JsonElement>("/logs/title")).GetProperty("title").GetString());
    }

    [Fact]
    public async Task Saved_queries_are_kept_and_can_be_deleted()
    {
        Assert.Equal(HttpStatusCode.Created, (await _client.PostAsync("/logs/log-query", Json("""{"name":"Errors","query":"level = Error"}"""))).StatusCode);
        // a second save of the same name (the viewer writes to every server) is not an error
        Assert.Equal(HttpStatusCode.Created, (await _client.PostAsync("/logs/log-query", Json("""{"name":"Errors","query":"level = Error"}"""))).StatusCode);

        var queries = await _client.GetFromJsonAsync<JsonElement>("/logs/log-queries");
        var only = Assert.Single(queries.EnumerateArray());
        Assert.Equal("Errors", only.GetProperty("name").GetString());
        Assert.Equal("level = Error", only.GetProperty("query").GetString());

        await _client.PostAsync("/logs/del-query", new StringContent("Errors"));
        Assert.Empty((await _client.GetFromJsonAsync<JsonElement>("/logs/log-queries")).EnumerateArray());
    }

    [Fact]
    public async Task The_minimum_level_is_shared_and_remembered()
    {
        Assert.Equal("Warning", (await _client.GetFromJsonAsync<JsonElement>("/logs/min-log-level")).GetProperty("minimumLogLevel").GetString());

        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsync("/logs/min-log-level", new StringContent("\"error\""))).StatusCode);

        Assert.Equal("Error", (await _client.GetFromJsonAsync<JsonElement>("/logs/min-log-level")).GetProperty("minimumLogLevel").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsync("/logs/min-log-level", new StringContent("loud"))).StatusCode);
    }

    [Fact]
    public async Task Accents_survive_the_round_trip()
    {
        await _client.PostAsync("/log?sourceApp=A", Json(Batch(("Échec de la connexion à l'état civil", "Error"))));

        var page = await SearchAsync("""{"queryText":"état civil"}""");

        Assert.Equal("Échec de la connexion à l'état civil", Assert.Single(page.Events).Message);
    }
}
