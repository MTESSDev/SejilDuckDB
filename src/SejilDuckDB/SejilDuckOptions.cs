namespace SejilDuckDB;

/// <summary>Settings of the log site. Bindable from configuration (<c>services.AddSejilDuck(config.GetSection("SejilDuck").Bind)</c>).</summary>
public sealed class SejilDuckOptions
{
    /// <summary>
    /// Folder that holds the log files of this server. A relative path is taken from the content root of the application.
    /// Every server writes its own folder: nothing is shared between servers.
    /// </summary>
    public string DataDirectory { get; set; } = "logs-data";

    /// <summary>Route of the viewer and of its API. <c>/logs</c> by default.</summary>
    public string Url { get; set; } = "/logs";

    /// <summary>
    /// Route that receives the batches posted by <c>Serilog.Sinks.Http</c> (a JSON array of events, <c>?sourceApp=</c> required).
    /// Anonymous: the senders have no identity to present. Null or empty turns it off.
    /// </summary>
    public string? IngestPath { get; set; } = "/log";

    /// <summary>Title shown in the viewer.</summary>
    public string Title { get; set; } = "Sejil";

    /// <summary>Authentication scheme that protects the viewer and its API. Empty: no authentication. The ingest route is never protected.</summary>
    public string? AuthenticationScheme { get; set; }

    /// <summary>Name of this server, returned with every event so that the viewer can tell the servers apart. The machine name by default.</summary>
    public string NodeName { get; set; } = Environment.MachineName;

    /// <summary>
    /// Header the load balancer reads to send a request to one given server (<c>x-nlb2</c>). The viewer sets it, with one of the
    /// <see cref="Nodes"/> as the value, on each of its calls.
    /// </summary>
    public string NodeHeader { get; set; } = "x-nlb2";

    /// <summary>
    /// The values of <see cref="NodeHeader"/> that reach each server (this one included), for example <c>["srv-a", "srv-b"]</c>.
    /// The viewer then asks every server and merges the answers. Empty: a single server, no header.
    /// Every server must be given the same list.
    /// </summary>
    public List<string> Nodes { get; set; } = new();

    /// <summary>The files of a day older than this many days are compressed to <c>.jsonl.gz</c>. Null: never. 7 by default.</summary>
    public int? CompressAfterDays { get; set; } = 7;

    /// <summary>The files of a day older than this many days are deleted. Null: never. 90 by default.</summary>
    public int? RetentionDays { get; set; } = 90;

    /// <summary>Events per page when the viewer does not say. 100 by default.</summary>
    public int PageSize { get; set; } = 100;

    /// <summary>
    /// A search stops after this long and hands back what it found, with the point it reached: the viewer asks again from there.
    /// It keeps a rare search from holding a request for minutes. 8 seconds by default.
    /// </summary>
    public TimeSpan SearchBudget { get; set; } = TimeSpan.FromSeconds(8);

    /// <summary>Minimum log level shared by the applications (<c>GET {Url}/min-log-level</c>). It is kept on disk. Warning until someone changes it.</summary>
    public string MinimumLogLevel { get; set; } = "Warning";

    /// <summary>Extra route (GET and POST) for the minimum log level, for when <see cref="Url"/> is behind authentication but the applications must read it anonymously.</summary>
    public string? LevelPath { get; set; }

    /// <summary>DuckDB memory limit for one search, for example <c>"1GB"</c>. Null: DuckDB's own default (80 % of the RAM).</summary>
    public string? DuckDbMemoryLimit { get; set; }

    /// <summary>DuckDB threads for one search. Null: one per core.</summary>
    public int? DuckDbThreads { get; set; }

    /// <summary>How often the files are compressed and purged. 1 hour by default.</summary>
    public TimeSpan MaintenanceEvery { get; set; } = TimeSpan.FromHours(1);
}
