using Serilog;
using SejilDuckDB;

var builder = WebApplication.CreateBuilder(args);

// Everything is read from the "SejilDuck" section of the configuration (appsettings.json, environment, command line).
builder.Services.AddSejilDuck(options => builder.Configuration.GetSection("SejilDuck").Bind(options));

// This site also writes its own logs, straight to the files, with no HTTP: they go into the same queue as the logs that other
// applications POST to /log, and the viewer shows both.
builder.Host.UseSerilog((context, services, logger) => logger
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
    .WriteTo.Console()
    .WriteTo.SejilDuck(services, sourceApp: "SampleApp"));

var app = builder.Build();

app.MapSejilDuck();                          // the viewer on /logs, its API, and POST /log for Serilog.Sinks.Http
app.MapGet("/", () => Results.Redirect("/logs"));

app.Logger.LogInformation("SampleApp started on {Urls}", string.Join(", ", app.Urls));
app.Run();
