using WorkshopDashboard.Data;
using WorkshopDashboard.Models;
using WorkshopDashboard.Services.Analytics;
using WorkshopDashboard.Services.Stores;
using WorkshopDashboard.Tests.Fakes;

namespace WorkshopDashboard.Tests.Analytics;

// Usage Analytics' three tabs: page views by page, people, and actions with the time they save.
public class UsageSummariesTests
{
    private static readonly DateTime Day1 = new(2026, 10, 6, 0, 0, 0, DateTimeKind.Utc);

    private static readonly IReadOnlyList<KnownPage> Pages =
    [
        new("waiting-quote", "Quotes Pending", "Quoting"),
        new("test-equipment", "Test Equipment", "Repairs"),
        new("test-equipment/setup", "Test Equipment Setup", "Repairs"),
    ];

    private static AnalyticsEvent View(string path, string? user, int day = 0) =>
        new() { EventType = UsageSummaries.PageView, Name = path, Username = user, Timestamp = Day1.AddDays(day).AddHours(1) };

    private static AnalyticsEvent Act(string name, string? user, int? qty = null, int day = 0) =>
        new() { EventType = UsageSummaries.Action, Name = name, Username = user, Quantity = qty, Timestamp = Day1.AddDays(day).AddHours(2) };

    [Theory]
    [InlineData("waiting-quote", "waiting-quote", "Quotes Pending")]
    [InlineData("waiting-quote/Alpha%20Private/1,2", "waiting-quote/*", "Quotes Pending (detail)")]
    [InlineData("test-equipment/setup", "test-equipment/setup", "Test Equipment Setup")]    // its own page, not a detail
    [InlineData("(home)", "(home)", "Home")]
    [InlineData("somewhere-new", "somewhere-new", "somewhere-new")]
    public void Pages_AreNamedFromTheCatalog(string path, string key, string title)
    {
        var (k, t, _) = UsageSummaries.ResolvePage(path, Pages);
        Assert.Equal((key, title), (k, t));
    }

    [Fact]
    public void PageViews_GroupByPage_LeaveOutActions_AndHideSignedOutUnlessAsked()
    {
        var events = new[]
        {
            View("waiting-quote", "amy"), View("waiting-quote", "amy"), View("waiting-quote/A/1", "bob"),
            View("waiting-quote", null),                       // TV screen
            Act("Quote services created — Apex", "amy"),
        };

        var rows = UsageSummaries.PageViews(events, Pages, includeSignedOut: false);
        Assert.Equal(new[] { ("waiting-quote", 2, 1), ("waiting-quote/*", 1, 1) }, rows.Select(r => (r.Key, r.Views, r.Users)));

        Assert.Equal(3, UsageSummaries.PageViews(events, Pages, includeSignedOut: true).First().Views);
    }

    [Fact]
    public void Actions_TotalTheirTimeSaved_PerUseOrPerItem()
    {
        var events = new[]
        {
            Act("Bulk Create Jobs", "amy", qty: 4), Act("Bulk Create Jobs", "bob", qty: 2),
            Act("Waiting quote email sent", "amy"), Act("Waiting quote email sent", "amy"),
            Act("Service report looked up", "bob"),
            View("waiting-quote", "amy"),
        };
        var savings = new Dictionary<string, ActionTimeSaving>(StringComparer.OrdinalIgnoreCase)
        {
            ["bulk create jobs"] = new() { ActionName = "Bulk Create Jobs", Minutes = 5, PerItem = true },
            ["Waiting quote email sent"] = new() { ActionName = "Waiting quote email sent", Minutes = 10 },
        };

        var rows = UsageSummaries.Actions(events, savings).ToDictionary(r => r.Name);

        Assert.Equal((2, (int?)6, 30.0), (rows["Bulk Create Jobs"].Uses, rows["Bulk Create Jobs"].Items, rows["Bulk Create Jobs"].MinutesSaved!.Value));
        Assert.Equal(20.0, rows["Waiting quote email sent"].MinutesSaved);
        Assert.Null(rows["Service report looked up"].MinutesSaved);   // nothing set
        Assert.Equal(20.0, rows["Bulk Create Jobs"].ByUser.Single(u => u.User == "amy").MinutesSaved);
        Assert.Equal(3, rows.Count);                                    // the page view isn't an action
    }

    [Fact]
    public void Users_ArePeopleOnly_WithTheirDaysActiveAndTimeSaved()
    {
        var events = new[]
        {
            View("waiting-quote", "amy", day: 0), View("test-equipment", "amy", day: 0), View("waiting-quote", "amy", day: 2),
            Act("Waiting quote email sent", "amy", day: 2),
            View("waiting-quote", null),
            Act("Monthly report pack built", UsageSummaries.AutomaticUser),
        };
        var savings = new Dictionary<string, ActionTimeSaving>(StringComparer.OrdinalIgnoreCase)
        {
            ["Waiting quote email sent"] = new() { ActionName = "Waiting quote email sent", Minutes = 10 },
        };

        var amy = Assert.Single(UsageSummaries.Users(events, Pages, savings));
        Assert.Equal(("amy", 2, 3, 1, 10.0), (amy.User, amy.ActiveDays, amy.PageViews, amy.Actions, amy.MinutesSaved));
        Assert.Equal(new NameCount("Quotes Pending", 2), amy.TopPages[0]);
    }

    [Theory]
    [InlineData(0, "—")]
    [InlineData(0.4, "under a minute")]
    [InlineData(45, "45 m")]
    [InlineData(60, "1 h")]
    [InlineData(135, "2 h 15 m")]
    [InlineData(60 * 24 * 2 + 180, "2 d 3 h")]
    public void FormatMinutes_ReadsNaturally(double minutes, string expected)
    {
        Assert.Equal(expected, UsageSummaries.FormatMinutes(minutes));
    }

    [Fact]
    public async Task TimeSaving_CanBeSetChanged_AndClearedWithZero()
    {
        using var db = new TestDb();
        var store = new ActionTimeSavingStore(db);

        await store.SetAsync("Bulk Create Jobs", 5, perItem: true);
        await store.SetAsync("bulk create jobs", 7.5, perItem: false);    // same action, any case
        var saving = Assert.Single(await store.LoadAsync()).Value;
        Assert.Equal((7.5, false), (saving.Minutes, saving.PerItem));

        await store.SetAsync("Bulk Create Jobs", 0, perItem: false);
        Assert.Empty(await store.LoadAsync());
    }
}
