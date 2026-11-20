using System.Globalization;
using System.Text.Json;
using Serilog.Events;
using Serilog.Formatting.Json;
using SejilDuckDB.Storage;

namespace SejilDuckDB.Logging;

/// <summary>
/// Turns a Serilog event into what the <c>/log</c> route stores for the same event sent through Serilog.Sinks.Http, so that both
/// ways of writing leave identical lines: property values as the text the sender wrote (a string as is, anything else as its JSON text,
/// null as null), whatever the culture of the machine.
/// </summary>
internal static class SerilogEventConverter
{
    private static readonly JsonValueFormatter Json = new(typeTagName: "$type");

    public static Storage.LogEvent ToEvent(Serilog.Events.LogEvent logEvent)
    {
        Dictionary<string, string?>? properties = null;
        if (logEvent.Properties.Count > 0)
        {
            properties = new Dictionary<string, string?>(logEvent.Properties.Count, StringComparer.Ordinal);
            foreach (var property in logEvent.Properties)
            {
                properties[property.Key] = ToText(property.Value);
            }
        }

        return new Storage.LogEvent
        {
            Timestamp = logEvent.Timestamp,
            Level = (int)logEvent.Level,
            Message = logEvent.RenderMessage(CultureInfo.InvariantCulture),
            Exception = logEvent.Exception?.ToString(),
            Properties = properties,
        };
    }

    public static string? ToText(LogEventPropertyValue value)
    {
        var writer = new StringWriter(CultureInfo.InvariantCulture);
        Json.Format(value, writer);
        var json = writer.ToString();

        if (json == "null")
        {
            return null;
        }

        return json.Length > 0 && json[0] == '"' ? JsonSerializer.Deserialize<string>(json) : json;
    }
}
