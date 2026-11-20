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

    // One generator for the whole process: the site and a sink on the same folder must not hand out the same id.
    private static readonly IdGenerator Ids = new();

    /// <param name="directory">The folder of the log files.</param>
    /// <param name="defaultMinimumLevel">The minimum level while the folder has no <c>config.json</c> (3: Warning), when this store is the first on the folder.</param>
    /// <param name="queueLimit">Most events waiting to be written (see <see cref="WriteQueue"/>), when this store is the first on the folder.</param>
    public LogFileStore(string directory, int queueLimit = WriteQueue.DefaultLimit, int defaultMinimumLevel = MinimumLevelStore.DefaultLevel)
    {
        Directory = directory;
        System.IO.Directory.CreateDirectory(directory);

        // The queue of the folder, shared by every store on it: the route /log and a local sink write through the same one.
        var full = Path.GetFullPath(directory);
        MinimumLevel = MinimumLevelStore.For(full, defaultMinimumLevel);
        Queue = WriteQueue.For(full, () => new WriteQueue(
            batch => WriteBatchAsync(full, batch),
            queueLimit,
            window: TimeSpan.FromMilliseconds(10),
            retryDelay: TimeSpan.FromSeconds(10)));
    }

    public string Directory { get; }

    /// <summary>The one queue through which this folder is written.</summary>
    public WriteQueue Queue { get; }

    public static string DayKey(DateTime localDate) => localDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    /// <summary>
    /// Writes the events and returns when they are written (they wait in the shared queue, so that many callers at once cost a few
    /// appends). False when they could not be written, so that the caller can have the sender retry. The events of a batch that
    /// spans several days are written day by day: if the second write fails, the first stays and the retry repeats it (a duplicate,
    /// never a loss).
    /// </summary>
    public Task<bool> TryAppendAsync(IReadOnlyList<LogEvent> events, string sourceApp, CancellationToken cancellationToken = default)
    {
        var kept = AboveMinimumLevel(events);
        return kept.Count == 0 ? Task.FromResult(true) : Queue.SubmitAsync(kept, sourceApp);   // nothing left to write is a success
    }

    /// <summary>
    /// Puts the events in the queue and returns at once (the local sink). False: the queue was full and they were dropped.
    /// The events below the minimum level are not kept, and that is not a failure.
    /// </summary>
    public bool TryEnqueue(IReadOnlyList<LogEvent> events, string sourceApp)
    {
        var kept = AboveMinimumLevel(events);
        return kept.Count == 0 || Queue.TryEnqueue(kept, sourceApp);
    }

    /// <summary>
    /// The minimum level of the folder, applied where the route and the sink meet: an event below it is not written, whichever way it
    /// came. It is what the viewer shows and changes, and what the applications can read.
    /// </summary>
    public MinimumLevelStore MinimumLevel { get; }

    private IReadOnlyList<LogEvent> AboveMinimumLevel(IReadOnlyList<LogEvent> events)
    {
        var minimum = MinimumLevel.Level;
        return events.All(e => e.Level >= minimum) ? events : events.Where(e => e.Level >= minimum).ToList();
    }

    // What the writer of the queue does with what has gathered: one append per day-file for the whole batch.
    private static async Task<bool> WriteBatchAsync(string directory, IReadOnlyList<(LogEvent Event, string App)> batch)
    {
        try
        {
            var byDay = new Dictionary<string, ArrayBufferWriter<byte>>();
            var writer = new Utf8JsonWriter(Stream.Null, WriterOptions);

            foreach (var (e, app) in batch)
            {
                var local = e.Timestamp.ToLocalTime().DateTime;
                var key = DayKey(local);
                if (!byDay.TryGetValue(key, out var buffer))
                {
                    byDay[key] = buffer = new ArrayBufferWriter<byte>(4096);
                }

                writer.Reset(buffer);
                WriteLine(writer, e, local, app, Ids.Next());
                writer.Flush();
                buffer.GetSpan(1)[0] = (byte)'\n';
                buffer.Advance(1);
            }

            foreach (var (key, buffer) in byDay)
            {
                await AppendAsync(Path.Combine(directory, $"log-{key}.jsonl"), buffer.WrittenMemory);
            }

            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // FileMode.Append is NOT an atomic append in .NET: the position of the end of the file is read when the file is opened and each write
    // goes there, so two writers on the file at once overwrite each other (events were lost that way: 475 of 8000 with two processes).
    // In this process the queue has a single writer. Between processes (IIS keeps the old one alive while it recycles, or a sink in
    // another process) FileShare.Read makes writers take turns: a second one cannot open the file until the first has closed it, and
    // waits. Searches can still read the file while it grows.
    private const int OpenAttempts = 100;                       // 100 x 30 ms: a few seconds, for a write that takes milliseconds
    private static readonly TimeSpan OpenRetryDelay = TimeSpan.FromMilliseconds(30);

    private static async Task AppendAsync(string path, ReadOnlyMemory<byte> bytes)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, useAsync: true);
                await stream.WriteAsync(bytes);
                await stream.FlushAsync();
                return;
            }
            catch (IOException) when (attempt < OpenAttempts)
            {
                // another handle has the file (another process, a search that has it open, the compression): wait for its turn
                await Task.Delay(OpenRetryDelay);
            }
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

    /// <summary>
    /// Ids that increase in a process and tell the events apart: microseconds since 1970 in the high bits, a number that the process
    /// draws at random (10 bits) in the low ones. Two processes on the same folder can only collide if they hand out an id in the very
    /// same microsecond with the same random number: about once in a thousand coincidences, and a coincidence needs two events in the
    /// same microsecond, which at the volume this is made for (a few events a second) does not happen. An id only breaks the ties between
    /// events with the same time, and names an event in the viewer; it is not a clock.
    /// </summary>
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
