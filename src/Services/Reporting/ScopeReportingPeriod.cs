namespace WorkshopDashboard.Services.Reporting;

/// <summary>
/// One selectable time-period option for the Scope Reporting pages (relative
/// ranges + calendar years). Shared by ScopeReportingOverview/State/Customer
/// so the dropdown, default, and date-range math live in exactly one place.
///
/// Calendar years start at 2025 — earlier job data is inconsistent enough
/// that it isn't worth offering as a filter option.
/// </summary>
public readonly record struct ScopeReportingPeriod(string Key, string Label, DateTime Start, DateTime End)
{
    public const string DefaultKey = "last3m";
    private const int FirstCalendarYear = 2025;

    public static List<ScopeReportingPeriod> All()
    {
        var today = DateTime.Today;
        var list = new List<ScopeReportingPeriod>
        {
            new("last30", "Last 30 Days", today.AddDays(-30), today),
            new("last3m", "Last 3 Months", today.AddMonths(-3), today),
            new("last6m", "Last 6 Months", today.AddMonths(-6), today),
            new("lastyear", "Last Year", today.AddYears(-1), today),
        };

        for (var year = FirstCalendarYear; year <= today.Year; year++)
        {
            var start = new DateTime(year, 1, 1);
            var end = year == today.Year ? today : new DateTime(year, 12, 31);
            list.Add(new ScopeReportingPeriod(year.ToString(), year.ToString(), start, end));
        }

        return list;
    }

    /// <summary>Resolves a query-string key to a period, falling back to the default for a
    /// missing/unrecognised key (e.g. a stale/hand-edited URL).</summary>
    public static ScopeReportingPeriod Resolve(string? key)
    {
        var all = All();
        if (!string.IsNullOrWhiteSpace(key))
        {
            var match = all.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase));
            if (match.Key is not null) return match;
        }

        return all.First(p => p.Key == DefaultKey);
    }
}
