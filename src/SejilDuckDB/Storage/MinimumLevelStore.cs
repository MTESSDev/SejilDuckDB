using System.Collections.Concurrent;
using System.Text.Json;

namespace SejilDuckDB.Storage;

/// <summary>
/// The minimum log level of a folder: what the viewer shows and changes, what the applications can read
/// (<c>GET /logs/min-log-level</c>), and what the site <b>applies</b>: an event below it is not written, whether it comes in on
/// <c>/log</c> or from the local sink. It is kept in <c>config.json</c> beside the log files, and <see cref="DefaultLevel"/> (Warning)
/// is the level while that file does not exist.
/// <para>
/// The level is held in memory and looked at again every few seconds (the file's date): a change made through this process is seen at
/// once, one made by another process on the same folder, or by hand, within <c>refreshEvery</c>.
/// </para>
/// </summary>
internal sealed class MinimumLevelStore
{
    public const int DefaultLevel = 3; // Warning

    private static readonly ConcurrentDictionary<string, MinimumLevelStore> Stores = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _path;
    private readonly int _default;
    private readonly TimeSpan _refreshEvery;
    private readonly object _lock = new();
    private int _level;
    private DateTime _fileStamp = DateTime.MinValue;
    private long _nextCheckTicks;

    private MinimumLevelStore(string directory, int defaultLevel, TimeSpan refreshEvery)
    {
        _path = Path.Combine(directory, "config.json");
        _default = defaultLevel;
        _refreshEvery = refreshEvery;
        _level = defaultLevel;
        Refresh();
    }

    /// <summary>The store of a folder, shared by everything in this process that writes to it. The first caller sets the default.</summary>
    public static MinimumLevelStore For(string directory, int defaultLevel = DefaultLevel, TimeSpan? refreshEvery = null)
        => Stores.GetOrAdd(Path.GetFullPath(directory), full => new MinimumLevelStore(full, defaultLevel, refreshEvery ?? TimeSpan.FromSeconds(2)));

    /// <summary>Forgets the store of a folder (tests).</summary>
    public static void Forget(string directory) => Stores.TryRemove(Path.GetFullPath(directory), out _);

    /// <summary>The level now: 0 (Verbose) to 5 (Fatal).</summary>
    public int Level
    {
        get
        {
            if (DateTime.UtcNow.Ticks >= Interlocked.Read(ref _nextCheckTicks))
            {
                Refresh();
            }

            return Volatile.Read(ref _level);
        }
    }

    /// <summary>Changes the level: it takes effect at once in this process and is kept in the file for the others.</summary>
    public void Set(int level)
    {
        lock (_lock)
        {
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new { minimumLogLevel = Levels.Name(level) }, Json));
            File.Move(temporary, _path, overwrite: true);

            _fileStamp = File.GetLastWriteTimeUtc(_path);
            Volatile.Write(ref _level, level);
        }
    }

    private void Refresh()
    {
        lock (_lock)
        {
            Interlocked.Exchange(ref _nextCheckTicks, (DateTime.UtcNow + _refreshEvery).Ticks);

            try
            {
                // GetLastWriteTimeUtc says 1601 for a file that does not exist
                var stamp = File.Exists(_path) ? File.GetLastWriteTimeUtc(_path) : DateTime.MinValue;
                if (stamp == _fileStamp)
                {
                    return;
                }

                _fileStamp = stamp;
                Volatile.Write(ref _level, stamp == DateTime.MinValue ? _default : ReadFile() ?? _default);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // being written by someone else: the next look will see it
            }
        }
    }

    // A file that cannot be read, or says something that is not a level, is as if it were not there: the default applies.
    private int? ReadFile()
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(_path));
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.NameEquals("minimumLogLevel") || property.Name.Equals("minimumLogLevel", StringComparison.OrdinalIgnoreCase))
                {
                    return Levels.TryParse(property.Value.GetString(), out var level) ? level : null;
                }
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            // not JSON, or not an object
        }

        return null;
    }
}
