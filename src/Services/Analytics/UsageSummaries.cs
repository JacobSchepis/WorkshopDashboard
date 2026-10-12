using WorkshopDashboard.Data;
using WorkshopDashboard.Models;

namespace WorkshopDashboard.Services.Analytics;

/// <summary>A page the app knows by name — NavCatalog's tools, handed in by UsageAnalytics.razor.</summary>
public record KnownPage(string Href, string Title, string Section);

public record UserCount(string User, int Count, double? MinutesSaved = null);
public record NameCount(string Name, int Count);

/// <summary>One page on the Page views tab. Key groups a page's drill-downs (waiting-quote/Alpha/12)
/// under "waiting-quote/*".</summary>
public record PageViewRow(string Key, string Title, string? Section, int Views, int Users, DateTime LastSeen, List<UserCount> ByUser);

/// <summary>One tracked action on the Actions tab. Items is the sum of the events' Quantity, when they
/// carry one; MinutesSaved is null when no saving is configured for it.</summary>
public record ActionRow(string Name, int Uses, int? Items, int Users, double? MinutesSaved, DateTime LastSeen, List<UserCount> ByUser);

/// <summary>One person on the Users tab.</summary>
public record UserRow(string User, int ActiveDays, int PageViews, int Actions, double MinutesSaved, DateTime LastSeen,
    List<NameCount> TopPages, List<NameCount> TopActions);

/// <summary>
/// Turns the raw analytics log into UsageAnalytics.razor's three tabs — page views, people and
/// actions — kept apart, since a page view and a button press aren't comparable counts.
/// </summary>
public static class UsageSummaries
{
    public const string PageView = "PageView";
    public const string Action = "Click";

    /// <summary>The Username on events the app records by itself (the 1st-of-month report run) —
    /// they count towards actions and time saved, but aren't a person on the Users tab.</summary>
    public const string AutomaticUser = "(automatic)";

    /// <summary>Signed-out page views — the workshop TV screens, the login page.</summary>
    public static bool IsSignedOut(AnalyticsEvent e) => string.IsNullOrWhiteSpace(e.Username);

    public static bool IsPerson(string? user) => !string.IsNullOrWhiteSpace(user) && user != AutomaticUser;

    /// <summary>One event's saving: the configured minutes, times its Quantity when it's per item.</summary>
    public static double? MinutesSaved(AnalyticsEvent e, ActionTimeSaving? saving) =>
        saving == null ? null : saving.PerItem ? saving.Minutes * (e.Quantity ?? 1) : saving.Minutes;

    /// <summary>Which page a viewed path belongs to: the known page it is, or — for a drill-down
    /// under one (waiting-quote/Alpha/12) — that page's "/*". Unknown paths stay as they are.</summary>
    public static (string Key, string Title, string? Section) ResolvePage(string path, IReadOnlyList<KnownPage> pages)
    {
        var p = path.Trim('/');
        if (p is "" or "(home)") return ("(home)", "Home", null);

        var exact = pages.FirstOrDefault(k => string.Equals(k.Href, p, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return (exact.Href, exact.Title, exact.Section);

        var parent = pages.Where(k => p.StartsWith(k.Href + "/", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(k => k.Href.Length).FirstOrDefault();
        if (parent != null) return (parent.Href + "/*", parent.Title + " (detail)", parent.Section);

        return (p, p, null);
    }

    public static List<PageViewRow> PageViews(IEnumerable<AnalyticsEvent> events, IReadOnlyList<KnownPage> pages, bool includeSignedOut)
    {
        return events
            .Where(e => e.EventType == PageView && (includeSignedOut || !IsSignedOut(e)))
            .Select(e => (Event: e, Page: ResolvePage(e.Name, pages)))
            .GroupBy(x => x.Page.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new PageViewRow(
                g.Key, g.First().Page.Title, g.First().Page.Section,
                g.Count(),
                g.Select(x => x.Event.Username).Where(IsPerson).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                g.Max(x => x.Event.Timestamp),
                ByUser(g.Select(x => x.Event), _ => null)))
            .OrderByDescending(r => r.Views).ThenBy(r => r.Title)
            .ToList();
    }

    public static List<ActionRow> Actions(IEnumerable<AnalyticsEvent> events, IReadOnlyDictionary<string, ActionTimeSaving> savings)
    {
        return events
            .Where(e => e.EventType == Action)
            .GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var saving = savings.GetValueOrDefault(g.Key);
                return new ActionRow(
                    g.First().Name,
                    g.Count(),
                    g.Any(e => e.Quantity.HasValue) ? g.Sum(e => e.Quantity ?? 0) : null,
                    g.Select(e => e.Username).Where(IsPerson).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    saving == null ? null : g.Sum(e => MinutesSaved(e, saving) ?? 0),
                    g.Max(e => e.Timestamp),
                    ByUser(g, e => MinutesSaved(e, saving)));
            })
            .OrderByDescending(r => r.MinutesSaved ?? -1).ThenByDescending(r => r.Uses)
            .ToList();
    }

    public static List<UserRow> Users(IEnumerable<AnalyticsEvent> events, IReadOnlyList<KnownPage> pages, IReadOnlyDictionary<string, ActionTimeSaving> savings)
    {
        return events
            .Where(e => IsPerson(e.Username))
            .GroupBy(e => e.Username!, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var views = g.Where(e => e.EventType == PageView).ToList();
                var actions = g.Where(e => e.EventType == Action).ToList();
                return new UserRow(
                    g.Key,
                    g.Select(e => e.Timestamp.ToLocalTime().Date).Distinct().Count(),
                    views.Count,
                    actions.Count,
                    actions.Sum(e => MinutesSaved(e, savings.GetValueOrDefault(e.Name)) ?? 0),
                    g.Max(e => e.Timestamp),
                    Top(views.Select(e => ResolvePage(e.Name, pages).Title)),
                    Top(actions.Select(e => e.Name)));
            })
            .OrderByDescending(r => r.ActiveDays).ThenByDescending(r => r.PageViews + r.Actions)
            .ToList();
    }

    /// <summary>"2 h 15 m", "45 m", "under a minute".</summary>
    public static string FormatMinutes(double minutes)
    {
        if (minutes <= 0) return "—";
        if (minutes < 1) return "under a minute";
        var total = (int)Math.Round(minutes);
        var (days, hours, mins) = (total / (60 * 24), total / 60 % 24, total % 60);
        if (days > 0) return hours > 0 ? $"{days} d {hours} h" : $"{days} d";
        if (hours > 0) return mins > 0 ? $"{hours} h {mins} m" : $"{hours} h";
        return $"{mins} m";
    }

    private static List<UserCount> ByUser(IEnumerable<AnalyticsEvent> events, Func<AnalyticsEvent, double?> minutes) =>
        events.GroupBy(e => IsPerson(e.Username) ? e.Username! : e.Username == AutomaticUser ? AutomaticUser : "(signed out)",
                StringComparer.OrdinalIgnoreCase)
            .Select(g => new UserCount(g.Key, g.Count(), g.Any(e => minutes(e) != null) ? g.Sum(e => minutes(e) ?? 0) : null))
            .OrderByDescending(u => u.Count)
            .ToList();

    private static List<NameCount> Top(IEnumerable<string> names) =>
        names.GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Select(g => new NameCount(g.Key, g.Count()))
            .OrderByDescending(n => n.Count).Take(5).ToList();
}
