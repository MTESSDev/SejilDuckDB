namespace SejilDuckDB.Storage;

/// <summary>The Serilog levels, by number (as stored) and by name (as shown).</summary>
internal static class Levels
{
    public static readonly string[] Names = { "Verbose", "Debug", "Information", "Warning", "Error", "Fatal" };

    public static string Name(int level) => level >= 0 && level < Names.Length ? Names[level] : level.ToString();

    /// <summary>A level by name (also <c>Trace</c> and <c>Critical</c>) or by number.</summary>
    public static bool TryParse(string? text, out int level)
    {
        level = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        text = text.Trim();

        switch (text.ToLowerInvariant())
        {
            case "trace": level = 0; return true;
            case "critical": level = 5; return true;
        }

        for (var i = 0; i < Names.Length; i++)
        {
            if (Names[i].Equals(text, StringComparison.OrdinalIgnoreCase))
            {
                level = i;
                return true;
            }
        }

        return int.TryParse(text, out level) && level >= 0 && level < Names.Length;
    }
}
