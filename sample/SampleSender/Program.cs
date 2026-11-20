using Serilog;

// An application that sends its logs to the log site (SampleApp, or any site that calls AddSejilDuck / MapSejilDuck).
//   dotnet run -- http://localhost:5000/log
// The site answers 503 when it cannot write a batch, and Serilog.Sinks.Http keeps the batch and sends it again.
var url = args.FirstOrDefault() ?? Environment.GetEnvironmentVariable("SEJIL_LOG_URL") ?? "http://localhost:5000/log";

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .Enrich.WithProperty("Environment", "demo")
    // the application name is a query parameter: it is what the viewer shows in the "app" column
    .WriteTo.Http(requestUri: $"{url}?sourceApp=SampleSender", queueLimitBytes: 1_048_576)
    .CreateLogger();

try
{
    Log.Information("Order {OrderId} created for {UserName}", 42, "bob");
    Log.Warning("Slow response from {Service}: {ElapsedMs} ms", "payments", 1840);

    try
    {
        throw new InvalidOperationException("The payment gateway refused the card.");
    }
    catch (Exception e)
    {
        Log.Error(e, "Payment failed for order {OrderId}", 42);
    }

    Log.Debug("Cache miss for key {Key}", "user:bob");
}
finally
{
    Log.CloseAndFlush(); // sends what is still waiting
}

Console.WriteLine($"Logs sent to {url}");
