using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace WorkshopDashboard.Demo;

// ═══════════════════════════════════════════════════════════════════════════
//  MySqlToSqlite
//
//  Lets the app's real JMS queries (JmsQueries — written for JMS's MySQL) run
//  unchanged against the demo's SQLite database. Two halves:
//
//  - Rewrite: the bits of MySQL syntax SQLite can't parse — INTERVAL date
//    arithmetic and GROUP_CONCAT's SEPARATOR / DISTINCT / ORDER BY clauses.
//  - Register: MySQL functions SQLite lacks or treats differently (CONCAT,
//    NOW, CURDATE, DATEDIFF), added to each demo connection.
//
//  Only what JmsQueries actually uses is covered; JmsQueriesRunOnDemoTests runs
//  every query through here so a new MySQL-ism shows up as a failing test.
// ═══════════════════════════════════════════════════════════════════════════

public static class MySqlToSqlite
{
    private const string DateTimeFormat = "yyyy-MM-dd HH:mm:ss";

    // DATE_SUB(x, INTERVAL n UNIT) / DATE_ADD(x, INTERVAL n UNIT)
    private static readonly Regex DateAddSub = new(
        @"DATE_(SUB|ADD)\s*\(\s*(?<x>[^,()]+(?:\([^()]*\))?)\s*,\s*INTERVAL\s+(?<n>-?\d+)\s+(?<unit>SECOND|MINUTE|HOUR|DAY|WEEK|MONTH|YEAR)\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // x - INTERVAL n UNIT / x + INTERVAL n UNIT, where x is NOW() / CURDATE() or a column.
    private static readonly Regex IntervalArithmetic = new(
        @"(?<x>NOW\(\)|CURDATE\(\)|[A-Za-z_][A-Za-z0-9_.]*)\s*(?<op>[-+])\s*INTERVAL\s+(?<n>-?\d+)\s+(?<unit>SECOND|MINUTE|HOUR|DAY|WEEK|MONTH|YEAR)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // LIKE followed by a single-quoted pattern ('' is an escaped quote inside it).
    private static readonly Regex LikeLiteral = new(@"\bLIKE\s+'(?:[^']|'')*'", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string Translate(string sql)
    {
        sql = StringEscapes(sql);
        // MySQL's LIKE treats backslash as its escape character by default; SQLite's has none.
        sql = LikeLiteral.Replace(sql, m => m.Value + @" ESCAPE '\'");
        sql = DateAddSub.Replace(sql, m => Shift(m.Groups["x"].Value.Trim(),
            m.Groups[1].Value.Equals("SUB", StringComparison.OrdinalIgnoreCase) ? "-" : "+",
            m.Groups["n"].Value, m.Groups["unit"].Value));
        sql = IntervalArithmetic.Replace(sql, m => Shift(m.Groups["x"].Value,
            m.Groups["op"].Value, m.Groups["n"].Value, m.Groups["unit"].Value));
        return RewriteGroupConcat(sql);
    }

    // MySQL reads backslash escapes inside string literals; SQLite takes backslashes literally.
    // \\ and \' become their characters. \% and \_ are kept as MySQL keeps them, for LIKE to
    // unescape. Only what JmsSql produces is handled.
    private static string StringEscapes(string sql)
    {
        var sb = new StringBuilder(sql.Length);
        var inString = false;
        for (var k = 0; k < sql.Length; k++)
        {
            var c = sql[k];
            if (inString && c == '\\' && k + 1 < sql.Length)
            {
                var next = sql[++k];
                sb.Append(next switch { '\\' => "\\", '\'' => "''", _ => $"\\{next}" });
                continue;
            }
            if (c == '\'') inString = !inString;
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static string Shift(string x, string op, string n, string unit)
    {
        var amount = int.Parse(n, CultureInfo.InvariantCulture);
        unit = unit.ToUpperInvariant();
        if (unit == "WEEK") { amount *= 7; unit = "DAY"; }
        if (op == "-") amount = -amount;
        return $"datetime({x}, '{amount:+0;-0} {unit.ToLowerInvariant()}')";
    }

    // GROUP_CONCAT([DISTINCT] expr [ORDER BY ...] [SEPARATOR 'sep']) → mysql_group_concat(expr, 'sep', distinct).
    // Parsed by hand rather than by regex because expr routinely holds nested calls
    // (CONCAT(...)) and quoted strings with commas or parentheses in them.
    private static string RewriteGroupConcat(string sql)
    {
        var sb = new StringBuilder();
        var i = 0;
        while (true)
        {
            var at = sql.IndexOf("GROUP_CONCAT", i, StringComparison.OrdinalIgnoreCase);
            if (at < 0) { sb.Append(sql, i, sql.Length - i); return sb.ToString(); }

            var open = sql.IndexOf('(', at);
            var close = MatchingParen(sql, open);
            var inner = sql.Substring(open + 1, close - open - 1).Trim();

            var distinct = inner.StartsWith("DISTINCT ", StringComparison.OrdinalIgnoreCase);
            if (distinct) inner = inner[9..].Trim();

            var separator = "','";
            var sepAt = TopLevelKeyword(inner, "SEPARATOR");
            if (sepAt >= 0)
            {
                separator = inner[(sepAt + 9)..].Trim();
                inner = inner[..sepAt].Trim();
            }
            var orderAt = TopLevelKeyword(inner, "ORDER BY");
            if (orderAt >= 0) inner = inner[..orderAt].Trim(); // ordering inside the list isn't kept

            sb.Append(sql, i, at - i);
            sb.Append($"mysql_group_concat({RewriteGroupConcat(inner)}, {separator}, {(distinct ? 1 : 0)})");
            i = close + 1;
        }
    }

    private static int MatchingParen(string s, int open)
    {
        var depth = 0;
        var inString = false;
        for (var k = open; k < s.Length; k++)
        {
            var c = s[k];
            if (c == '\'') inString = !inString;
            else if (inString) continue;
            else if (c == '(') depth++;
            else if (c == ')' && --depth == 0) return k;
        }
        throw new FormatException("Unbalanced parentheses in GROUP_CONCAT.");
    }

    // Index of a keyword at paren depth 0 and outside string literals, or -1.
    private static int TopLevelKeyword(string s, string keyword)
    {
        var depth = 0;
        var inString = false;
        for (var k = 0; k < s.Length; k++)
        {
            var c = s[k];
            if (c == '\'') { inString = !inString; continue; }
            if (inString) continue;
            if (c == '(') depth++;
            else if (c == ')') depth--;
            else if (depth == 0 && string.Compare(s, k, keyword, 0, keyword.Length, StringComparison.OrdinalIgnoreCase) == 0
                && (k == 0 || char.IsWhiteSpace(s[k - 1])))
                return k;
        }
        return -1;
    }

    /// <summary>Adds the MySQL functions the queries rely on to a demo connection.</summary>
    public static void RegisterFunctions(SqliteConnection connection, Func<DateTime> now)
    {
        // MySQL's CONCAT is NULL if any argument is NULL (SQLite's own concat() skips NULLs).
        connection.CreateFunction("CONCAT", (object?[] args) =>
            args.Any(a => a is null or DBNull) ? null : string.Concat(args.Select(a => Convert.ToString(a, CultureInfo.InvariantCulture))),
            isDeterministic: true);
        connection.CreateFunction("NOW", () => now().ToString(DateTimeFormat, CultureInfo.InvariantCulture));
        connection.CreateFunction("CURDATE", () => now().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        connection.CreateFunction("DATEDIFF", (string? a, string? b) =>
            ParseDate(a) is { } x && ParseDate(b) is { } y ? (long?)(x.Date - y.Date).TotalDays : null,
            isDeterministic: true);

        // The accumulator must be immutable: SQLite hands every group the same seed value.
        connection.CreateAggregate("mysql_group_concat",
            (Values: ImmutableList<string>.Empty, Separator: ","),
            ((ImmutableList<string> Values, string Separator) acc, string? value, string separator, long distinct) =>
                value is null || (distinct != 0 && acc.Values.Contains(value))
                    ? acc with { Separator = separator }
                    : (acc.Values.Add(value), separator),
            acc => acc.Values.IsEmpty ? null : string.Join(acc.Separator, acc.Values));
    }

    private static DateTime? ParseDate(string? s) =>
        DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
}
