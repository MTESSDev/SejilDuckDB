using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SejilDuckDB.Storage;

namespace SejilDuckDB.Query;

/// <summary>A condition for the WHERE clause of a search, with its parameters (<c>$q0</c>, <c>$q1</c>...).</summary>
internal sealed class SqlFilter
{
    public string Where { get; init; } = "TRUE";
    public List<KeyValuePair<string, object>> Parameters { get; } = new();

    /// <summary>First day (local) that can hold a matching event, or null for no limit.</summary>
    public DateTime? FromDay { get; init; }

    /// <summary>Last day (local, included) that can hold a matching event, or null for no limit.</summary>
    public DateTime? ToDay { get; init; }
}

/// <summary>
/// Turns what the viewer sends into a DuckDB condition over the columns of a log file
/// (<c>i, t, d, a, l, m, x, p</c>). The query language is the one of SejilSQL: a word, <c>name = value</c>, <c>name != value</c>,
/// <c>name like '%value%'</c>, <c>name not like ...</c>, combined with <c>and</c>/<c>&amp;&amp;</c> and <c>or</c>/<c>||</c> and parentheses.
/// User text only ever reaches the SQL as a parameter.
/// </summary>
internal static class QueryTranslator
{
    // The columns a query can name besides the properties.
    private static readonly Dictionary<string, string> Columns = new(StringComparer.OrdinalIgnoreCase)
    {
        ["message"] = "m",
        ["exception"] = "x",
        ["sourceapp"] = "a",
        ["level"] = "l",
        ["timestamp"] = "CAST(d AS VARCHAR)",
    };

    public static TimeSpan? Period(string? dateFilter) => dateFilter switch
    {
        "5m" => TimeSpan.FromMinutes(5),
        "15m" => TimeSpan.FromMinutes(15),
        "1h" => TimeSpan.FromHours(1),
        "6h" => TimeSpan.FromHours(6),
        "12h" => TimeSpan.FromHours(12),
        "24h" => TimeSpan.FromHours(24),
        "2d" => TimeSpan.FromDays(2),
        "5d" => TimeSpan.FromDays(5),
        _ => null,
    };

    /// <param name="nowUtc">The current time, UTC.</param>
    /// <param name="nowLocal">The current time, local.</param>
    public static SqlFilter Translate(LogQueryFilter? filter, DateTime nowUtc, DateTime nowLocal)
    {
        filter ??= new LogQueryFilter();

        var conditions = new List<string>();
        var parameters = new List<KeyValuePair<string, object>>();
        DateTime? fromDay = null;
        DateTime? toDay = null;

        if (Period(filter.DateFilter) is { } span)
        {
            conditions.Add($"t >= {Add(parameters, (nowUtc - span))}");
            fromDay = (nowLocal - span).Date;
        }
        else if (filter.DateRangeFilter is { Count: >= 2 } range)
        {
            // [from, to): the viewer sends the day after the last one it wants
            conditions.Add($"(d >= {Add(parameters, range[0])} AND d < {Add(parameters, range[1])})");
            fromDay = range[0].Date;
            toDay = range[1].AddTicks(-1).Date;
        }

        if (!string.IsNullOrWhiteSpace(filter.LevelFilter))
        {
            if (!Levels.TryParse(filter.LevelFilter, out var level))
            {
                throw new FormatException($"Unknown level '{filter.LevelFilter}'.");
            }

            conditions.Add($"l = {Add(parameters, level)}");
        }

        if (filter.ExceptionsOnly)
        {
            conditions.Add("x IS NOT NULL");
        }

        if (!string.IsNullOrWhiteSpace(filter.QueryText))
        {
            var sql = new StringBuilder();
            BuildPredicate(filter.QueryText, sql, parameters);
            conditions.Add($"({sql})");
        }

        var result = new SqlFilter
        {
            Where = conditions.Count == 0 ? "TRUE" : string.Join(" AND ", conditions),
            FromDay = fromDay,
            ToDay = toDay,
        };
        result.Parameters.AddRange(parameters);
        return result;
    }

    private static string Add(List<KeyValuePair<string, object>> parameters, object value)
    {
        var name = $"q{parameters.Count}";
        parameters.Add(new KeyValuePair<string, object>(name, value));
        return "$" + name;
    }

    // The same cascade of splits as SejilSQL, so that a query means the same thing in both:
    // (...) op (...), then ... op (...), then (...) op ..., then ... op ..., then name op value, then a bare word.
    private static void BuildPredicate(string query, StringBuilder sql, List<KeyValuePair<string, object>> parameters)
    {
        // and, or and like are whole words (a name such as "formulaire" contains "or"), whatever their case.
        var split = Split(query, @"(\(.+\))\s*(\|\||&&|\band\b|\bor\b)\s*(\(.+\))");
        if (split.Length != 3)
        {
            split = Split(query, @"(.+)\s*(\|\||&&|\band\b|\bor\b)\s*(\(.+\))");
            if (split.Length != 3)
            {
                split = Split(query, @"(\(.+\))\s*(\|\||&&|\band\b|\bor\b)\s*(.+)");
                if (split.Length != 3)
                {
                    split = Split(query, @"(.+)\s*(\|\||&&|\band\b|\bor\b)\s*(.+)");
                    if (split.Length != 3)
                    {
                        Leaf(query, split: Split(query, @"(\w+)\s*(=|!=|\blike\b|\bnot like\b)\s*(.+)"), sql, parameters);
                    }
                    else
                    {
                        BuildPredicate(split[0], sql, parameters);
                        sql.Append(LogicalOperator(split[1]));
                        BuildPredicate(split[2], sql, parameters);
                    }
                }
                else
                {
                    sql.Append('(');
                    BuildPredicate(Unwrap(split[0]), sql, parameters);
                    sql.Append(')');
                    sql.Append(LogicalOperator(split[1]));
                    BuildPredicate(split[2], sql, parameters);
                }
            }
            else
            {
                BuildPredicate(split[0], sql, parameters);
                sql.Append(LogicalOperator(split[1]));
                sql.Append('(');
                BuildPredicate(Unwrap(split[2]), sql, parameters);
                sql.Append(')');
            }
        }
        else
        {
            sql.Append('(');
            BuildPredicate(Unwrap(split[0]), sql, parameters);
            sql.Append(')');
            sql.Append(LogicalOperator(split[1]));
            sql.Append('(');
            BuildPredicate(Unwrap(split[2]), sql, parameters);
            sql.Append(')');
        }
    }

    private static string[] Split(string query, string pattern)
        => Regex.Split(query, pattern, RegexOptions.IgnoreCase).Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();

    private static string Unwrap(string text) => text.Substring(1, text.Length - 2);

    private static string LogicalOperator(string op)
        => op.Trim().ToLowerInvariant() is "&&" or "and" ? " AND "
         : op.Trim().ToLowerInvariant() is "||" or "or" ? " OR "
         : throw new FormatException("Invalid logical operator.");

    private static void Leaf(string query, string[] split, StringBuilder sql, List<KeyValuePair<string, object>> parameters)
    {
        if (split.Length == 3)
        {
            var name = split[0];
            var op = split[1].Trim().ToLowerInvariant();
            var value = StripQuotes(split[2].Trim());

            if (Columns.TryGetValue(name, out var column))
            {
                sql.Append(ColumnCondition(name, column, op, value, parameters));
            }
            else
            {
                sql.Append(PropertyCondition(name, op, value, parameters));
            }
        }
        else if (split.Length == 1)
        {
            // Just a word: the message, the exception and every property are searched. The properties are searched as text, so a
            // name that contains the word matches too.
            var word = Add(parameters, $"%{split[0].Trim()}%");
            sql.Append($"(m ILIKE {word} OR x ILIKE {word} OR CAST(p AS VARCHAR) ILIKE {word})");
        }
        else
        {
            throw new FormatException($"Cannot read the query '{query}'.");
        }
    }

    // message, exception, sourceApp, timestamp and level. A missing exception or message never matches (like SQL NULL).
    private static string ColumnCondition(string name, string column, string op, string value, List<KeyValuePair<string, object>> parameters)
    {
        if (name.Equals("level", StringComparison.OrdinalIgnoreCase) && op is "=" or "!=" && Levels.TryParse(value, out var level))
        {
            return $"l {(op == "=" ? "=" : "<>")} {Add(parameters, level)}";
        }

        // a level compared as text ("level like 'Err%'")
        var text = name.Equals("level", StringComparison.OrdinalIgnoreCase) ? "CAST(l AS VARCHAR)" : column;
        var parameter = Add(parameters, value);

        return op switch
        {
            "=" => $"lower({text}) = lower({parameter})",
            "!=" => $"lower({text}) <> lower({parameter})",
            "like" => $"{text} ILIKE {parameter}",
            "not like" => $"{text} NOT ILIKE {parameter}",
            _ => throw new FormatException("Invalid operator."),
        };
    }

    // A property. As in SejilSQL, "!=" and "not like" also match the events that do not have the property at all.
    // The name is a \w+ and the path is a parameter: nothing typed by the user is part of the SQL.
    private static string PropertyCondition(string name, string op, string value, List<KeyValuePair<string, object>> parameters)
    {
        var path = Add(parameters, $"$.\"{name}\"");
        var parameter = Add(parameters, value);
        var property = $"json_extract_string(p, {path})";

        return op switch
        {
            "=" => $"COALESCE(lower({property}) = lower({parameter}), false)",
            "!=" => $"NOT COALESCE(lower({property}) = lower({parameter}), false)",
            "like" => $"COALESCE({property} ILIKE {parameter}, false)",
            "not like" => $"NOT COALESCE({property} ILIKE {parameter}, false)",
            _ => throw new FormatException("Invalid operator."),
        };
    }

    // "..."  -->  ...      '...'  -->  ...      ...  -->  ...
    private static string StripQuotes(string value)
        => value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0]
            ? value.Substring(1, value.Length - 2)
            : value;
}
