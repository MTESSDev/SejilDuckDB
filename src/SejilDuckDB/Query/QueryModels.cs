using System.Globalization;

namespace SejilDuckDB.Query;

/// <summary>What the viewer sends with a search (camelCase JSON).</summary>
public sealed class LogQueryFilter
{
    public string? QueryText { get; set; }
    public string? DateFilter { get; set; }
    public List<DateTime>? DateRangeFilter { get; set; }
    public string? LevelFilter { get; set; }
    public bool ExceptionsOnly { get; set; }
}

/// <summary>
/// A point in a node's order: the events <b>older</b> than it are the next ones. <c>Utc</c> is the event time
/// (<c>yyyy-MM-ddTHH:mm:ss.ffffff</c>), <c>Id</c> breaks the ties.
/// </summary>
public sealed record LogCursor(string Utc, string Id)
{
    public string Format() => $"{Utc}|{Id}";

    public static bool TryParse(string? text, out LogCursor? cursor)
    {
        cursor = null;
        var parts = text?.Split('|');
        if (parts is not { Length: 2 } ||
            !DateTime.TryParseExact(parts[0], "yyyy-MM-dd'T'HH:mm:ss.ffffff", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ||
            !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            return false;
        }

        cursor = new LogCursor(parts[0], parts[1]);
        return true;
    }
}

public sealed class LogEntryProperty
{
    public string Name { get; set; } = "";
    public string? Value { get; set; }
}

/// <summary>An event, as the viewer receives it.</summary>
public sealed class LogEntry
{
    /// <summary>Unique on its server (a string: it can be larger than what JavaScript counts exactly).</summary>
    public string Id { get; set; } = "";

    /// <summary>The server that holds the event.</summary>
    public string Node { get; set; } = "";

    public string? Message { get; set; }
    public string SourceApp { get; set; } = "";
    public string Level { get; set; } = "";

    /// <summary>Local time of the server, without zone: <c>2026-10-07T19:21:41.083</c>.</summary>
    public string Timestamp { get; set; } = "";

    /// <summary>The same instant in UTC, to the microsecond. What the servers are merged on.</summary>
    public string Utc { get; set; } = "";

    public string? Exception { get; set; }
    public List<LogEntryProperty> Properties { get; set; } = new();
}

/// <summary>One answer to a search.</summary>
public sealed class LogPage
{
    /// <summary>The server that answered.</summary>
    public string Node { get; set; } = "";

    public List<LogEntry> Events { get; set; } = new();

    /// <summary>Where to ask from to get the following events. Null when there are none.</summary>
    public LogCursor? Resume { get; set; }

    /// <summary>Nothing is left to find.</summary>
    public bool Exhausted { get; set; }

    /// <summary>The page is short because the time allowed to the search ran out, not because the events ran out: ask again from <see cref="Resume"/>.</summary>
    public bool Partial { get; set; }

    /// <summary>The oldest day that was read, <c>yyyy-MM-dd</c>.</summary>
    public string? ScannedTo { get; set; }
}
