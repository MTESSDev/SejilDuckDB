using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using DuckDB.NET.Data;
using Microsoft.Extensions.Options;
using SejilDuckDB.Storage;

namespace SejilDuckDB.Query;

/// <summary>
/// Runs a search over the log files with DuckDB, one day at a time, from the newest day to the oldest. A page is full as soon as
/// enough events are found, so the most recent events cost the same whatever the size of the history. A search that finds little
/// stops when its time budget is spent and says where it got to.
/// </summary>
internal sealed class LogReader
{
    private const string Columns = "{i:'BIGINT', t:'TIMESTAMP', d:'TIMESTAMP', a:'VARCHAR', l:'INTEGER', m:'VARCHAR', x:'VARCHAR', p:'JSON'}";
    private const int MaxPageSize = 1000;

    private readonly LogFileStore _store;
    private readonly SejilDuckOptions _options;

    public LogReader(LogFileStore store, IOptions<SejilDuckOptions> options)
    {
        _store = store;
        _options = options.Value;
    }

    /// <param name="filter">What to look for.</param>
    /// <param name="before">Only the events older than this point, or null to start from the newest.</param>
    /// <param name="pageSize">How many events at most.</param>
    public async Task<LogPage> QueryAsync(LogQueryFilter? filter, LogCursor? before, int pageSize, CancellationToken cancellationToken = default)
    {
        pageSize = pageSize <= 0 ? _options.PageSize : Math.Min(pageSize, MaxPageSize);

        var clock = Stopwatch.StartNew();
        var sqlFilter = QueryTranslator.Translate(filter, DateTime.UtcNow, DateTime.Now);
        var page = new LogPage { Node = _options.NodeName };

        DateTime? lastDay = null;
        if (before is not null)
        {
            // An id of 0 marks a bound the events are strictly older than (see BudgetCursor): nothing ties with it, so the day
            // that holds it only counts if it holds something before it.
            var upper = ParseUtc(before.Utc);
            if (before.Id == "0")
            {
                upper = upper.AddTicks(-10);
            }

            lastDay = DateTime.SpecifyKind(upper, DateTimeKind.Utc).ToLocalTime().Date;
        }

        var days = _store.ListDays()
            .Where(d => (sqlFilter.FromDay is null || d.Date >= sqlFilter.FromDay) &&
                        (sqlFilter.ToDay is null || d.Date <= sqlFilter.ToDay) &&
                        (lastDay is null || d.Date <= lastDay))
            .ToList();

        for (var index = 0; index < days.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var day = days[index];

            // Time is up: hand back what was found, from where the search will go on. At least one day is always read.
            if (index > 0 && clock.Elapsed >= _options.SearchBudget)
            {
                page.Partial = true;
                page.Resume = BudgetCursor(day.Date, before);
                return page;
            }

            var wanted = pageSize - page.Events.Count;
            var rows = await ReadDayAsync(day, sqlFilter, before, wanted + 1, cancellationToken);
            page.ScannedTo = day.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            page.Events.AddRange(rows.Take(wanted));

            if (rows.Count > wanted || page.Events.Count == pageSize)
            {
                // A full page. More may follow (certainly if this day had one more row; possibly in the days that remain).
                var last = page.Events[^1];
                page.Resume = new LogCursor(last.Utc, last.Id);
                page.Exhausted = rows.Count <= wanted && index == days.Count - 1;
                if (page.Exhausted)
                {
                    page.Resume = null;
                }

                return page;
            }
        }

        page.Exhausted = true;
        return page;
    }

    // "Older than the end of this day": the events of this day and before. Never newer than the cursor the search started from.
    private static LogCursor BudgetCursor(DateTime day, LogCursor? before)
    {
        var endOfDay = DateTime.SpecifyKind(day.Date.AddDays(1), DateTimeKind.Local).ToUniversalTime()
            .ToString(LogFileStore.UtcFormat, CultureInfo.InvariantCulture);

        return before is not null && string.CompareOrdinal(before.Utc, endOfDay) <= 0
            ? before
            : new LogCursor(endOfDay, "0");
    }

    private static DateTime ParseUtc(string utc)
        => DateTime.ParseExact(utc, LogFileStore.UtcFormat, CultureInfo.InvariantCulture, DateTimeStyles.None);

    private async Task<List<LogEntry>> ReadDayAsync(LogDay day, SqlFilter filter, LogCursor? before, int limit, CancellationToken cancellationToken)
    {
        // A file can disappear or be locked while it is compressed: ask again, with the files of the day as they are now.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await QueryFilesAsync(day.Files, filter, before, limit, cancellationToken);
            }
            catch (DuckDBException) when (attempt < 3)
            {
                await Task.Delay(200 * attempt, cancellationToken);
                day = _store.ListDays().FirstOrDefault(d => d.Date == day.Date) ?? new LogDay(day.Date, Array.Empty<string>());
                if (day.Files.Length == 0)
                {
                    return new List<LogEntry>();
                }
            }
        }
    }

    private async Task<List<LogEntry>> QueryFilesAsync(string[] files, SqlFilter filter, LogCursor? before, int limit, CancellationToken cancellationToken)
    {
        var from = string.Join(" UNION ALL ", files.Select(f =>
            $"SELECT * FROM read_json('{Escape(f)}', format='newline_delimited', columns={Columns}, ignore_errors=true, " +
            $"compression='{(f.EndsWith(".gz", StringComparison.Ordinal) ? "gzip" : "uncompressed")}')"));

        // A line that is cut off (a write that did not finish) is read as a row of NULLs, not skipped: only whole lines count.
        var where = $"i IS NOT NULL AND t IS NOT NULL AND d IS NOT NULL AND l IS NOT NULL AND ({filter.Where})";
        var parameters = new List<KeyValuePair<string, object>>(filter.Parameters);

        if (before is not null)
        {
            parameters.Add(new("cursorT", ParseUtc(before.Utc)));
            parameters.Add(new("cursorI", long.Parse(before.Id, CultureInfo.InvariantCulture)));
            where += " AND (t < $cursorT OR (t = $cursorT AND i < $cursorI))";
        }

        var sql = $"SELECT i, t, d, a, l, m, x, CAST(p AS VARCHAR) FROM ({from}) AS L WHERE {where} ORDER BY t DESC, i DESC LIMIT {limit}";

        await using var connection = new DuckDBConnection("DataSource=:memory:");
        await connection.OpenAsync(cancellationToken);
        await ConfigureAsync(connection, cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.Add(new DuckDBParameter(name, value));
        }

        var entries = new List<LogEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            entries.Add(new LogEntry
            {
                Id = reader.GetInt64(0).ToString(CultureInfo.InvariantCulture),
                Node = _options.NodeName,
                Utc = reader.GetDateTime(1).ToString(LogFileStore.UtcFormat, CultureInfo.InvariantCulture),
                Timestamp = reader.GetDateTime(2).ToString(LogFileStore.LocalFormat, CultureInfo.InvariantCulture),
                SourceApp = reader.IsDBNull(3) ? "" : reader.GetString(3),
                Level = Levels.Name(reader.GetInt32(4)),
                Message = reader.IsDBNull(5) ? null : reader.GetString(5),
                Exception = reader.IsDBNull(6) ? null : reader.GetString(6),
                Properties = reader.IsDBNull(7) ? new List<LogEntryProperty>() : ReadProperties(reader.GetString(7)),
            });
        }

        return entries;
    }

    private async Task ConfigureAsync(DuckDBConnection connection, CancellationToken cancellationToken)
    {
        var settings = new List<string>();
        if (!string.IsNullOrWhiteSpace(_options.DuckDbMemoryLimit))
        {
            settings.Add($"SET memory_limit='{Escape(_options.DuckDbMemoryLimit)}'");
        }

        if (_options.DuckDbThreads is { } threads and > 0)
        {
            settings.Add($"SET threads={threads}");
        }

        foreach (var setting in settings)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = setting;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    // The properties are stored as one JSON object of texts; they are shown by name.
    private static List<LogEntryProperty> ReadProperties(string json)
    {
        var properties = new List<LogEntryProperty>();

        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in document.RootElement.EnumerateObject())
            {
                properties.Add(new LogEntryProperty
                {
                    Name = property.Name,
                    Value = property.Value.ValueKind == JsonValueKind.Null ? null : property.Value.GetString(),
                });
            }
        }

        properties.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return properties;
    }

    // File paths come from the folder listing, never from the user; a quote is doubled all the same.
    private static string Escape(string text) => text.Replace('\\', '/').Replace("'", "''");
}
