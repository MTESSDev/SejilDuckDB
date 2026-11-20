using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SejilDuckDB.Query;
using SejilDuckDB.Storage;

namespace SejilDuckDB;

public static class SejilDuckExtensions
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Lazy<string> IndexHtml = new(() =>
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("SejilDuckDB.index.html")
            ?? throw new InvalidOperationException("The viewer (index.html) is not embedded in the assembly.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    /// <summary>
    /// Registers the log files, the search, and the service that compresses and purges the old files.
    /// <code>builder.Services.AddSejilDuck(o => o.DataDirectory = @"D:\logs");</code>
    /// </summary>
    public static IServiceCollection AddSejilDuck(this IServiceCollection services, Action<SejilDuckOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = services.AddOptions<SejilDuckOptions>();
        if (configure is not null)
        {
            options.Configure(configure);
        }

        services.TryAddSingleton(provider =>
        {
            var settings = provider.GetRequiredService<IOptions<SejilDuckOptions>>().Value;
            var directory = Path.IsPathRooted(settings.DataDirectory)
                ? settings.DataDirectory
                : Path.Combine(provider.GetRequiredService<IHostEnvironment>().ContentRootPath, settings.DataDirectory);
            return new LogFileStore(directory, settings.QueueLimit,
                Levels.TryParse(settings.MinimumLogLevel, out var minimum) ? minimum : MinimumLevelStore.DefaultLevel);
        });
        services.TryAddSingleton<LogReader>();
        services.TryAddSingleton<MetaStore>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, MaintenanceService>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, WriteQueueFlushService>());

        return services;
    }

    /// <summary>
    /// Maps the viewer (<c>Url</c>), its API and the ingest route (<c>IngestPath</c>).
    /// <code>app.MapSejilDuck();</code>
    /// </summary>
    public static IEndpointRouteBuilder MapSejilDuck(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var options = endpoints.ServiceProvider.GetRequiredService<IOptions<SejilDuckOptions>>().Value;
        var url = "/" + options.Url.Trim('/');

        // The viewer and its API: behind the authentication scheme when there is one.
        RequestDelegate Protected(RequestDelegate handler) => async context =>
        {
            if (!string.IsNullOrWhiteSpace(options.AuthenticationScheme) && context.User.Identity?.IsAuthenticated != true)
            {
                await context.ChallengeAsync(options.AuthenticationScheme);
                return;
            }

            await handler(context);
        };

        endpoints.MapGet(url, Protected(async context =>
        {
            // The servers to ask travel with the page, which comes from a server that answers: asking for them afterwards could
            // reach the one that is down, and the viewer would believe it is alone.
            var config = JsonSerializer.Serialize(new { header = options.NodeHeader, nodes = options.Nodes, node = options.NodeName }, Json)
                .Replace("<", "\\u003c"); // nothing in the value can close the <script> element

            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.WriteAsync(IndexHtml.Value.Replace("__SEJIL_CONFIG__", config));
        }));

        endpoints.MapPost(url + "/events", Protected(async context =>
        {
            var reader = context.RequestServices.GetRequiredService<LogReader>();

            LogQueryFilter? filter;
            try
            {
                filter = await JsonSerializer.DeserializeAsync<LogQueryFilter>(context.Request.Body, Json, context.RequestAborted);
            }
            catch (JsonException e)
            {
                await Text(context, StatusCodes.Status400BadRequest, $"Invalid search: {e.Message}");
                return;
            }

            _ = int.TryParse(context.Request.Query["pageSize"].FirstOrDefault(), out var pageSize);

            LogCursor? before = null;
            var beforeText = context.Request.Query["before"].FirstOrDefault();
            if (!string.IsNullOrEmpty(beforeText) && !LogCursor.TryParse(beforeText, out before))
            {
                await Text(context, StatusCodes.Status400BadRequest, "The 'before' parameter is not a cursor.");
                return;
            }

            try
            {
                var page = await reader.QueryAsync(filter, before, pageSize, context.RequestAborted);
                await Write(context, page);
            }
            catch (FormatException e) // a query that cannot be read
            {
                await Text(context, StatusCodes.Status400BadRequest, e.Message);
            }
        }));

        // The servers the viewer has to ask, and the header that reaches each of them.
        endpoints.MapGet(url + "/nodes", Protected(context => Write(context, new
        {
            header = options.NodeHeader,
            nodes = options.Nodes,
            node = options.NodeName,
        })));

        endpoints.MapPost(url + "/log-query", Protected(async context =>
        {
            var query = await JsonSerializer.DeserializeAsync<SavedQuery>(context.Request.Body, Json, context.RequestAborted);
            if (query is null || string.IsNullOrWhiteSpace(query.Name))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            // 201 also when it already exists here: with several servers a query saved on both is not an error on the second.
            context.RequestServices.GetRequiredService<MetaStore>().SaveQuery(query.Name, query.Query ?? "");
            context.Response.StatusCode = StatusCodes.Status201Created;
        }));

        endpoints.MapGet(url + "/log-queries", Protected(context
            => Write(context, context.RequestServices.GetRequiredService<MetaStore>().Queries())));

        endpoints.MapPost(url + "/del-query", Protected(async context =>
        {
            var name = (await new StreamReader(context.Request.Body).ReadToEndAsync()).Trim().Trim('"');
            context.RequestServices.GetRequiredService<MetaStore>().DeleteQuery(name);
            context.Response.StatusCode = StatusCodes.Status200OK;
        }));

        endpoints.MapGet(url + "/title", Protected(context => Write(context, new { title = options.Title })));

        endpoints.MapGet(url + "/user-name", Protected(context => Write(context, new
        {
            userName = !string.IsNullOrWhiteSpace(options.AuthenticationScheme) && context.User.Identity?.IsAuthenticated == true
                ? context.User.Identity.Name ?? ""
                : "",
        })));

        // The minimum level: anonymous, the applications that send their logs read it.
        RequestDelegate getLevel = context => Write(context, new { minimumLogLevel = context.RequestServices.GetRequiredService<MetaStore>().MinimumLevel() });
        RequestDelegate setLevel = async context =>
        {
            var text = (await new StreamReader(context.Request.Body).ReadToEndAsync()).Trim().Trim('"');
            if (!Levels.TryParse(text, out var level))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            context.RequestServices.GetRequiredService<MetaStore>().SetMinimumLevel(level);
            context.Response.StatusCode = StatusCodes.Status200OK;
        };

        endpoints.MapGet(url + "/min-log-level", getLevel);
        endpoints.MapPost(url + "/min-log-level", setLevel);

        if (!string.IsNullOrWhiteSpace(options.LevelPath))
        {
            var levelUrl = "/" + options.LevelPath.Trim('/');
            endpoints.MapGet(levelUrl, getLevel);
            endpoints.MapPost(levelUrl, setLevel);
        }

        if (!string.IsNullOrWhiteSpace(options.IngestPath))
        {
            endpoints.MapPost("/" + options.IngestPath.Trim('/'), Ingest);
        }

        return endpoints;
    }

    // A batch posted by Serilog.Sinks.Http: a JSON array of events, ?sourceApp= required.
    private static async Task Ingest(HttpContext context)
    {
        string? sourceApp = context.Request.Query["sourceApp"];
        if (string.IsNullOrWhiteSpace(sourceApp))
        {
            await Text(context, StatusCodes.Status400BadRequest, "The sourceApp query parameter is required.");
            return;
        }

        List<LogEvent> events;
        try
        {
            using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            var root = document.RootElement;

            if (root.ValueKind == JsonValueKind.Null)
            {
                context.Response.StatusCode = StatusCodes.Status200OK;
                return;
            }

            if (root.ValueKind != JsonValueKind.Array)
            {
                await Text(context, StatusCodes.Status400BadRequest, "A JSON array of events is expected.");
                return;
            }

            events = LogEventReader.Read(root);
        }
        catch (Exception e) when (e is JsonException or FormatException)
        {
            await Text(context, StatusCodes.Status400BadRequest, $"Invalid events: {e.Message}");
            return;
        }

        if (events.Count == 0)
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            return;
        }

        var store = context.RequestServices.GetRequiredService<LogFileStore>();

        // 503 when the batch could not be written: the sender keeps it and retries.
        context.Response.StatusCode = await store.TryAppendAsync(events, sourceApp, context.RequestAborted)
            ? StatusCodes.Status200OK
            : StatusCodes.Status503ServiceUnavailable;
    }

    private static Task Write<T>(HttpContext context, T value)
    {
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsync(JsonSerializer.Serialize(value, Json));
    }

    private static Task Text(HttpContext context, int status, string message)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "text/plain; charset=utf-8";
        return context.Response.WriteAsync(message);
    }
}
