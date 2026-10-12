namespace WorkshopDashboard.Services.External.Jms;

// ═══════════════════════════════════════════════════════════════════════════
//  JmsSql
//
//  The JMS query endpoint takes raw SQL text, not parameters, so any string that
//  goes into a JmsQueries literal goes through here first. JMS runs MySQL with
//  backslash escapes on: doubling the single quote alone isn't enough, because a
//  trailing backslash in the input would escape the closing quote.
// ═══════════════════════════════════════════════════════════════════════════

public static class JmsSql
{
    /// <summary>Escapes text for use inside a single-quoted MySQL string literal.</summary>
    public static string Escape(string value) =>
        value.Replace("\\", "\\\\").Replace("'", "''");

    /// <summary>
    /// Escapes text for use inside a single-quoted LIKE pattern, so % and _ in the
    /// input match themselves rather than acting as wildcards.
    /// </summary>
    public static string EscapeLike(string value) =>
        Escape(value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_"));
}
