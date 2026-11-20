using System.IO.Compression;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SejilDuckDB.Storage;

/// <summary>Compresses the files of the days past <see cref="SejilDuckOptions.CompressAfterDays"/> and deletes those past <see cref="SejilDuckOptions.RetentionDays"/>.</summary>
internal sealed class LogMaintenance
{
    private readonly LogFileStore _store;
    private readonly int? _compressAfterDays;
    private readonly int? _retentionDays;

    public LogMaintenance(LogFileStore store, int? compressAfterDays, int? retentionDays)
    {
        _store = store;
        _compressAfterDays = compressAfterDays;
        _retentionDays = retentionDays;
    }

    /// <param name="today">The current local date.</param>
    public MaintenanceResult Run(DateTime today)
    {
        var compressed = 0;
        var deleted = 0;

        foreach (var day in _store.ListDays())
        {
            var age = (today.Date - day.Date).Days;

            if (_retentionDays is { } retention && age > retention)
            {
                foreach (var file in day.Files)
                {
                    deleted += TryDelete(file) ? 1 : 0;
                }

                continue;
            }

            if (_compressAfterDays is { } after && age > after)
            {
                foreach (var file in day.Files.Where(f => f.EndsWith(".jsonl", StringComparison.Ordinal)))
                {
                    compressed += TryCompress(file, day.Date) ? 1 : 0;
                }
            }
        }

        DeleteStaleTemporaryFiles();
        return new MaintenanceResult(compressed, deleted);
    }

    // Writes the .gz beside the file under a temporary name, renames it once it is complete, and only then deletes the original:
    // a crash in the middle leaves a complete file or a temporary one that is ignored, never a half-written .gz.
    // The original is opened with no sharing: an append that arrives meanwhile fails, answers 503, and the sender retries into a new file.
    private bool TryCompress(string file, DateTime day)
    {
        var final = Path.Combine(_store.Directory, $"log-{LogFileStore.DayKey(day)}.{DateTime.UtcNow:yyyyMMddHHmmssfff}.jsonl.gz");
        var temporary = final + ".tmp";

        try
        {
            using (var source = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
            using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var gzip = new GZipStream(target, CompressionLevel.Optimal))
            {
                source.CopyTo(gzip);
            }

            File.Move(temporary, final);
            File.Delete(file);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            TryDelete(temporary); // in use (a search or an append): the next run tries again
            return false;
        }
    }

    private void DeleteStaleTemporaryFiles()
    {
        foreach (var file in Directory.EnumerateFiles(_store.Directory, "log-*.tmp"))
        {
            if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-1))
            {
                TryDelete(file);
            }
        }
    }

    private static bool TryDelete(string file)
    {
        try
        {
            if (File.Exists(file))
            {
                File.Delete(file);
                return true;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // in use: the next run tries again
        }

        return false;
    }
}

internal readonly record struct MaintenanceResult(int Compressed, int Deleted);

/// <summary>Runs <see cref="LogMaintenance"/> now and then.</summary>
internal sealed class MaintenanceService : BackgroundService
{
    private readonly LogMaintenance _maintenance;
    private readonly SejilDuckOptions _options;
    private readonly ILogger<MaintenanceService> _logger;

    public MaintenanceService(LogFileStore store, IOptions<SejilDuckOptions> options, ILogger<MaintenanceService> logger)
    {
        _options = options.Value;
        _maintenance = new LogMaintenance(store, _options.CompressAfterDays, _options.RetentionDays);
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); // not while the site is starting

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var result = _maintenance.Run(DateTime.Now);
                    if (result.Compressed > 0 || result.Deleted > 0)
                    {
                        _logger.LogInformation("SejilDuckDB: {Compressed} file(s) compressed, {Deleted} deleted.", result.Compressed, result.Deleted);
                    }
                }
                catch (Exception e)
                {
                    _logger.LogWarning(e, "SejilDuckDB: the maintenance of the log files failed; it will run again.");
                }

                await Task.Delay(_options.MaintenanceEvery, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
    }
}
