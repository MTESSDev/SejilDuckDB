using Microsoft.Extensions.Options;
using SejilDuckDB;
using SejilDuckDB.Query;
using SejilDuckDB.Storage;

namespace SejilDuckDB.Tests;

/// <summary>A temporary folder of log files, with the store, the reader and the settings that go with it.</summary>
internal sealed class Workspace : IDisposable
{
    public Workspace(Action<SejilDuckOptions>? configure = null)
    {
        Directory = Path.Combine(Path.GetTempPath(), "sejilduck-tests-" + Guid.NewGuid().ToString("N"));
        Options = new SejilDuckOptions { DataDirectory = Directory, NodeName = "node-test" };
        configure?.Invoke(Options);

        Store = new LogFileStore(Directory);
        Reader = new LogReader(Store, Microsoft.Extensions.Options.Options.Create(Options));
    }

    public string Directory { get; }
    public SejilDuckOptions Options { get; }
    public LogFileStore Store { get; }
    public LogReader Reader { get; }

    public static LogEvent Event(DateTimeOffset at, string? message = "message", int level = 2, string? exception = null, params (string Name, string? Value)[] properties)
        => new()
        {
            Timestamp = at,
            Level = level,
            Message = message,
            Exception = exception,
            Properties = properties.Length == 0 ? null : properties.ToDictionary(p => p.Name, p => p.Value),
        };

    public async Task WriteAsync(string sourceApp, params LogEvent[] events)
        => Assert.True(await Store.TryAppendAsync(events, sourceApp));

    public Task WriteAsync(params LogEvent[] events) => WriteAsync("App", events);

    public Task<LogPage> SearchAsync(string? query = null, LogCursor? before = null, int pageSize = 100)
        => Reader.QueryAsync(new LogQueryFilter { QueryText = query }, before, pageSize);

    public string[] Files(string pattern = "log-*") => System.IO.Directory.GetFiles(Directory, pattern).Select(Path.GetFileName).Order().ToArray()!;

    public void Dispose()
    {
        WriteQueue.Forget(Directory); // stops the writer of the folder
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
            // a search still holds a file: the system cleans the temporary folder later
        }
    }
}
