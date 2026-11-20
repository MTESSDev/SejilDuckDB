using System.Text.Json;

namespace SejilDuckDB.Storage;

/// <summary>One log event, as sent by Serilog.Sinks.Http.</summary>
internal sealed class LogEvent
{
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>The Serilog level, 0 (Verbose) to 5 (Fatal).</summary>
    public int Level { get; set; }

    public string? Message { get; set; }

    public string? Exception { get; set; }

    /// <summary>
    /// Property values as text, exactly as the sender wrote them: a JSON string as is, anything else (number, boolean, object,
    /// array) as its JSON text. A JSON null stays null.
    /// </summary>
    public Dictionary<string, string?>? Properties { get; set; }
}

/// <summary>Reads the JSON array sent by Serilog.Sinks.Http. Property names are matched without regard to case.</summary>
internal static class LogEventReader
{
    /// <exception cref="FormatException">An event is not shaped as expected.</exception>
    public static List<LogEvent> Read(JsonElement array)
    {
        var events = new List<LogEvent>(array.GetArrayLength());

        foreach (var element in array.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException("Each event must be a JSON object.");
            }

            var logEvent = new LogEvent();
            var hasTimestamp = false;

            foreach (var member in element.EnumerateObject())
            {
                if (Is(member.Name, "Timestamp"))
                {
                    logEvent.Timestamp = ReadTimestamp(member.Value);
                    hasTimestamp = true;
                }
                else if (Is(member.Name, "Level"))
                {
                    logEvent.Level = ReadLevel(member.Value);
                }
                else if (Is(member.Name, "RenderedMessage"))
                {
                    logEvent.Message = ReadText(member.Value);
                }
                else if (Is(member.Name, "Exception"))
                {
                    logEvent.Exception = ReadText(member.Value);
                }
                else if (Is(member.Name, "Properties"))
                {
                    logEvent.Properties = ReadProperties(member.Value);
                }
            }

            if (!hasTimestamp)
            {
                throw new FormatException("Each event needs a Timestamp.");
            }

            events.Add(logEvent);
        }

        return events;
    }

    private static bool Is(string name, string expected) => name.Equals(expected, StringComparison.OrdinalIgnoreCase);

    private static DateTimeOffset ReadTimestamp(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String && value.TryGetDateTimeOffset(out var timestamp))
        {
            return timestamp;
        }

        throw new FormatException("Timestamp must be an ISO 8601 date and time with an offset.");
    }

    private static int ReadLevel(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String && Levels.TryParse(value.GetString(), out var byName))
        {
            return byName;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number >= 0 && number < Levels.Names.Length)
        {
            return number;
        }

        throw new FormatException("Level must be a Serilog level name (Verbose, Debug, Information, Warning, Error, Fatal).");
    }

    private static string? ReadText(JsonElement value)
        => value.ValueKind == JsonValueKind.String ? value.GetString()
         : value.ValueKind == JsonValueKind.Null ? null
         : value.GetRawText();

    private static Dictionary<string, string?>? ReadProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("Properties must be a JSON object.");
        }

        var properties = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            properties[property.Name] = ReadText(property.Value);
        }

        return properties;
    }
}
