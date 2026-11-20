using System.Text.Json;
using Microsoft.Extensions.Options;

namespace SejilDuckDB.Storage;

public sealed record SavedQuery(int Id, string Name, string Query);

/// <summary>
/// The small things the viewer keeps besides the logs: the saved queries and the shared minimum log level, in two JSON files
/// beside the log files. They belong to this server: with several servers the viewer writes them to each.
/// </summary>
internal sealed class MetaStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly object _lock = new();
    private readonly string _queriesPath;
    private readonly string _configPath;
    private readonly string _defaultLevel;

    public MetaStore(LogFileStore store, IOptions<SejilDuckOptions> options)
    {
        _queriesPath = Path.Combine(store.Directory, "queries.json");
        _configPath = Path.Combine(store.Directory, "config.json");
        _defaultLevel = Levels.TryParse(options.Value.MinimumLogLevel, out var level) ? Levels.Name(level) : "Warning";
    }

    public IReadOnlyList<SavedQuery> Queries()
    {
        lock (_lock)
        {
            return Load<List<SavedQuery>>(_queriesPath) ?? new List<SavedQuery>();
        }
    }

    /// <summary>False when a query of that name exists.</summary>
    public bool SaveQuery(string name, string query)
    {
        lock (_lock)
        {
            var queries = Load<List<SavedQuery>>(_queriesPath) ?? new List<SavedQuery>();
            if (queries.Any(q => q.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            queries.Add(new SavedQuery(queries.Count == 0 ? 1 : queries.Max(q => q.Id) + 1, name, query));
            Save(_queriesPath, queries);
            return true;
        }
    }

    public bool DeleteQuery(string name)
    {
        lock (_lock)
        {
            var queries = Load<List<SavedQuery>>(_queriesPath) ?? new List<SavedQuery>();
            if (queries.RemoveAll(q => q.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) == 0)
            {
                return false;
            }

            Save(_queriesPath, queries);
            return true;
        }
    }

    public string MinimumLevel()
    {
        lock (_lock)
        {
            var config = Load<Config>(_configPath);
            return config?.MinimumLogLevel is { } level && Levels.TryParse(level, out var parsed) ? Levels.Name(parsed) : _defaultLevel;
        }
    }

    public void SetMinimumLevel(int level)
    {
        lock (_lock)
        {
            Save(_configPath, new Config { MinimumLogLevel = Levels.Name(level) });
        }
    }

    private sealed class Config
    {
        public string? MinimumLogLevel { get; set; }
    }

    private static T? Load<T>(string path) where T : class
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) : null;
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            return null;
        }
    }

    // Written aside, then moved into place: a crash never leaves half a file.
    private static void Save<T>(string path, T value)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, Json));
        File.Move(temporary, path, overwrite: true);
    }
}
