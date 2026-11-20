using System.Buffers;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace SejilDuckDB.Storage;

/// <summary>
/// The log files of this server. One file per day of the event (local date): <c>log-yyyyMMdd.jsonl</c>, one JSON object per line.
/// <para>
/// A line: <c>{"i":id,"t":"utc","d":"local","a":"app","l":level,"m":"message","x":"exception","p":{"Name":"value"}}</c>
/// (<c>x</c> and <c>p</c> are left out when empty). <c>i</c> is unique and increases on this server, it breaks the ties between
/// events with the same <c>t</c>.
/// </para>
/// <para>
/// After a few days the day's file is compressed to <c>log-yyyyMMdd.&lt;stamp&gt;.jsonl.gz</c>. An event that arrives late for a day
/// that is already compressed goes into a new <c>log-yyyyMMdd.jsonl</c> beside it, so a day can hold several files.
/// </para>
/// </summary>
internal sealed class LogFileStore
{
    public const string UtcFormat = "yyyy-MM-dd'T'HH:mm:ss.ffffff";
    public const string LocalFormat = "yyyy-MM-dd'T'HH:mm:ss.fff";

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        // keeps the accents as they are instead of é: smaller files, and a text search finds them
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        SkipValidation = true, // several top-level objects, one per line
    };

    private readonly IdGenerator _ids = new();

    public LogFileStore(string directory)
    {
        Directory = directory;
        System.IO.Directory.CreateDirectory(directory);
    }

    public string Directory { get; }

    public static string DayKey(DateTime localDate) => localDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    /// <summary>
    /// Appends the batch to the files of its days. Returns false when it could not be written, so that the caller can have the
    /// sender retry. A batch that spans several days is written day by day: if the second write fails, the first stays and the
    /// retry repeats it (a duplicate, never a loss).
    /// </summary>
    public async Task<bool> TryAppendAsync(IReadOnlyList<LogEvent> events, string sourceApp, CancellationToken cancellationToken = default)
    {
        try
        {
            var byDay = new Dictionary<string, ArrayBufferWriter<byte>>();
            var writer = new Utf8JsonWriter(Stream.Null, WriterOptions);

            foreach (var e in events)
            {
                var local = e.Timestamp.ToLocalTime().DateTime;
                var key = DayKey(local);
                if (!byDay.TryGetValue(key, out var buffer))
                {
                    byDay[key] = buffer = new ArrayBufferWriter<byte>(4096);
                }

                writer.Reset(buffer);
                WriteLine(writer, e, local, sourceApp, _ids.Next());
                writer.Flush();
                buffer.GetSpan(1)[0] = (byte)'\n';
                buffer.Advance(1);
            }

            foreach (var (key, buffer) in byDay)
            {
                var path = Path.Combine(Directory, $"log-{key}.jsonl");

                // FileShare.ReadWrite: a search can read the file while it grows, and a second process of the site (IIS keeps the
                // old one alive while it recycles) can append too. Each batch is one write at the end of the file.
                await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 4096, useAsync: true);
                await stream.WriteAsync(buffer.WrittenMemory, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void WriteLine(Utf8JsonWriter w, LogEvent e, DateTime local, string sourceApp, long id)
    {
        w.WriteStartObject();
        w.WriteNumber("i", id);
        w.WriteString("t", e.Timestamp.UtcDateTime.ToString(UtcFormat, CultureInfo.InvariantCulture));
        w.WriteString("d", local.ToString(LocalFormat, CultureInfo.InvariantCulture));
        w.WriteString("a", sourceApp);
        w.WriteNumber("l", e.Level);

        if (e.Message is not null)
        {
            w.WriteString("m", e.Message);
        }

        if (e.Exception is not null)
        {
            w.WriteString("x", e.Exception);
        }

        if (e.Properties is { Count: > 0 })
        {
            w.WriteStartObject("p");
            foreach (var (name, value) in e.Properties)
            {
                if (value is null)
                {
                    w.WriteNull(name);
                }
                else
                {
                    w.WriteString(name, value);
                }
            }

            w.WriteEndObject();
        }

        w.WriteEndObject();
    }

    /// <summary>The days that have files, newest first, with their files (the compressed ones and the live one).</summary>
    public IReadOnlyList<LogDay> ListDays()
    {
        var days = new Dictionary<DateTime, List<string>>();

        foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "log-*"))
        {
            if (!TryParseDay(Path.GetFileName(file), out var date))
            {
                continue;
            }

            if (!days.TryGetValue(date, out var files))
            {
                days[date] = files = new List<string>();
            }

            files.Add(file);
        }

        return days
            .OrderByDescending(d => d.Key)
            .Select(d => new LogDay(d.Key, d.Value.Order(StringComparer.Ordinal).ToArray()))
            .ToList();
    }

    /// <summary>The day of a file name (<c>log-yyyyMMdd.jsonl</c> or <c>log-yyyyMMdd.&lt;stamp&gt;.jsonl.gz</c>); false for any other file, temporary ones included.</summary>
    public static bool TryParseDay(string fileName, out DateTime date)
    {
        date = default;

        if (fileName.Length < 13 || !fileName.StartsWith("log-", StringComparison.Ordinal) || fileName[12] != '.')
        {
            return false;
        }

        if (!fileName.EndsWith(".jsonl", StringComparison.Ordinal) && !fileName.EndsWith(".jsonl.gz", StringComparison.Ordinal))
        {
            return false;
        }

        return DateTime.TryParseExact(fileName.AsSpan(4, 8), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    /// <summary>Unique, increasing ids: microseconds since 1970 in the high bits, a per-process number in the low ones.</summary>
    private sealed class IdGenerator
    {
        private static readonly long EpochTicks = DateTime.UnixEpoch.Ticks;
        private readonly object _lock = new();
        private readonly long _processBits = Random.Shared.Next(0, 1024);
        private long _last;

        public long Next()
        {
            var micros = (DateTime.UtcNow.Ticks - EpochTicks) / 10;
            var candidate = (micros << 10) | _processBits;

            lock (_lock)
            {
                _last = candidate > _last ? candidate : _last + 1;
                return _last;
            }
        }
    }
}

/// <summary>The files of one day.</summary>
internal sealed record LogDay(DateTime Date, string[] Files);
