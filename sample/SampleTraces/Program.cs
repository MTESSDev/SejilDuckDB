using Serilog;
using Serilog.Events;

// An application that reports its slow requests and its thread pool starvation as log events, sent to the log site.
//   dotnet run -- --urls http://localhost:5401
// then try:  /fast  /slow  /fail  /block (call /block 80 times at once to starve the thread pool)
var logSite = Environment.GetEnvironmentVariable("SEJIL_LOG_URL") ?? "http://127.0.0.1:5200/log";

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, services, configuration) => configuration
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)   // the framework's own request logs would drown ours
    .MinimumLevel.Override("System.Net.Http", LogEventLevel.Warning)
    .WriteTo.Http(requestUri: $"{logSite}?sourceApp=SampleTraces", queueLimitBytes: 1_048_576));

builder.Services.AddSejilSlowTraces(options =>
{
    options.ThreadPoolSamplePeriod = TimeSpan.FromSeconds(2);   // 5 s by default; shorter here to see it quickly

    // The logs are sent by HttpClient: without this, a slow POST to the log site would itself be logged, and sent, and so on.
    options.Filter = span => !(span.GetTagItem("url.full") is string url && url.Contains("/log"));
});

var app = builder.Build();

app.MapGet("/fast", () => "fast");

// HTTP A -> HTTP B: this request calls two other services in turn (here, two routes of the same app, over HTTP). When the whole
// thing takes more than a second, ONE event is logged with the tree: each call and the time it took.
var http = new HttpClient();
app.MapGet("/chain", async (HttpContext context) =>
{
    var self = $"{context.Request.Scheme}://{context.Request.Host}";
    var slow = context.Request.Query.ContainsKey("slow") ? "&slow=1" : "";
    await http.GetStringAsync($"{self}/inventory?sku=42{slow}");
    await http.GetStringAsync($"{self}/payments");
    return "chain done";
});
// with ?slow=1 this service is slow itself: it then logs its own side of the request, with the same TraceId as the caller
app.MapGet("/inventory", async (HttpContext context) => { await Task.Delay(context.Request.Query.ContainsKey("slow") ? 1200 : 700); return "stock"; });
app.MapGet("/payments", async () => { await Task.Delay(600); return "paid"; });

// Slow, but without holding a thread: a database or a remote service that takes its time.
app.MapGet("/slow", async () =>
{
    await Task.Delay(1300);
    return "slow";
});

app.MapGet("/fail", () => Results.Problem("The payment gateway refused the card.", statusCode: 500));

// Holds a thread of the pool for 8 seconds, like blocking code (.Result, .Wait(), a lock, a synchronous call). Enough of them at
// once and no thread is left for anything else: that is thread pool starvation.
app.MapGet("/block", () =>
{
    Thread.Sleep(TimeSpan.FromSeconds(8));
    return "blocked";
});

app.Run();
