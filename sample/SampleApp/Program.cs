using SejilDuckDB;

var builder = WebApplication.CreateBuilder(args);

// Everything is read from the "SejilDuck" section of the configuration (appsettings.json, environment, command line).
builder.Services.AddSejilDuck(options => builder.Configuration.GetSection("SejilDuck").Bind(options));

var app = builder.Build();

app.MapSejilDuck();                          // the viewer on /logs, its API, and POST /log for Serilog.Sinks.Http
app.MapGet("/", () => Results.Redirect("/logs"));

app.Run();
