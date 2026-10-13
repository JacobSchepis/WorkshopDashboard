using System.Reflection;
using WorkshopDashboard.Demo;
using WorkshopDashboard.Services.External.Jms;
using WorkshopDashboard.Services.Stores;

namespace WorkshopDashboard.Tests.Demo;

// Every query the app sends JMS (written for JMS's MySQL) has to run on demo mode's SQLite
// database too — through MySqlToSqlite — or that page breaks in the demo. Each JmsQueries
// method is called with plausible arguments (taken from the seeded data) and its SQL executed.
public class JmsQueriesRunOnDemoTests : IClassFixture<JmsQueriesRunOnDemoTests.DemoDb>
{
    public sealed class DemoDb : IDisposable
    {
        public DemoJmsDatabase Database { get; } = new();
        public void Dispose() => Database.Dispose();
    }

    private readonly DemoJmsDatabase _db;

    public JmsQueriesRunOnDemoTests(DemoDb fixture) => _db = fixture.Database;

    public static IEnumerable<object[]> QueryMethods() =>
        typeof(JmsQueries).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.ReturnType == typeof(string) && m.Name != nameof(JmsQueries.ScopeJobClause))
            .Select(m => new object[] { m.Name });

    [Theory]
    [MemberData(nameof(QueryMethods))]
    public async Task Query_RunsOnTheDemoDatabase(string methodName)
    {
        foreach (var method in typeof(JmsQueries).GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.Name == methodName))
        {
            foreach (var exclusions in new[] { new ScopeJobExclusions(), Excluding() })
            {
                var sql = (string)method.Invoke(null, method.GetParameters().Select(p => ArgumentFor(p, exclusions)).ToArray())!;
                var ex = await Record.ExceptionAsync(() => _db.QueryCsvAsync(sql));
                Assert.True(ex is null, $"{methodName} failed on the demo database: {ex?.Message}\n{MySqlToSqlite.Translate(sql)}");
            }
        }
    }

    // The main pages should have something to show, not just run.
    private static readonly string[] MainPageQueries =
    [
        nameof(JmsQueries.StaleJobsQuery), nameof(JmsQueries.WaitingWorkshopAssignmentQuery),
        nameof(JmsQueries.WorkshopFinalisationCompleteQuery), nameof(JmsQueries.WaitingWorkshopProcessingQuery),
        nameof(JmsQueries.WaitingQuoteConfirmationQuery), nameof(JmsQueries.DashboardCountsQuery),
        nameof(JmsQueries.WorkshopDashboardCountsQuery), nameof(JmsQueries.OpenScopeJobsWithModelsQuery),
        nameof(JmsQueries.AllClosedScopeJobsWithModelsQuery), nameof(JmsQueries.BuildQuoteJobsQuery),
        nameof(JmsQueries.BuildScopeReportStatsQuery), nameof(JmsQueries.LoanEquipmentQuery),
        nameof(JmsQueries.CompanySearchQuery),
    ];

    public static IEnumerable<object[]> MainPageQueryNames() => MainPageQueries.Select(q => new object[] { q });

    [Theory]
    [MemberData(nameof(MainPageQueryNames))]
    public async Task MainPageQueries_FindDemoData(string methodName) =>
        Assert.True(await RowsAsync(_db, methodName) > 0, $"{methodName} returned no rows on the demo data.");

    // The seeded data is laid out relative to "now", so the demo (and CI, which runs in UTC)
    // must have something on every main page whatever the date and time of day.
    [Theory]
    [InlineData("2026-10-13 00:04")]
    [InlineData("2026-10-13 07:59")]
    [InlineData("2026-10-13 23:59")]
    [InlineData("2027-01-01 00:00")]
    [InlineData("2027-03-15 12:00")]
    [InlineData("2027-06-30 16:30")]
    public async Task MainPageQueries_FindDemoData_AtAnyTime(string now)
    {
        var clock = DateTime.Parse(now, System.Globalization.CultureInfo.InvariantCulture);
        using var db = new DemoJmsDatabase(clock: () => clock);

        var empty = new List<string>();
        foreach (var query in MainPageQueries)
            if (await RowsAsync(db, query) == 0) empty.Add(query);

        Assert.True(empty.Count == 0, $"No demo rows at {now} for: {string.Join(", ", empty)}");
    }

    private static async Task<int> RowsAsync(DemoJmsDatabase db, string methodName)
    {
        var method = typeof(JmsQueries).GetMethod(methodName)!;
        var sql = (string)method.Invoke(null, method.GetParameters().Select(p => ArgumentFor(p, new ScopeJobExclusions())).ToArray())!;
        return (await db.QueryCsvAsync(sql)).Trim().Split('\n').Length - 1;
    }

    // The demo reads MySQL's backslash escapes the way JMS does, so hostile search text has
    // to come back as a search for itself, never as SQL.
    [Theory]
    [InlineData(@"\' OR 1=1 -- ")]
    [InlineData(@"x\")]
    [InlineData("O'Brien")]
    [InlineData("%")]
    [InlineData("_")]
    public async Task CompanySearch_TreatsHostileTextAsText(string term)
    {
        var csv = await _db.QueryCsvAsync(JmsQueries.CompanySearchQuery(term));

        Assert.Equal(0, csv.Trim().Split('\n').Length - 1);
    }

    [Fact]
    public void Translate_RewritesTheMySqlOnlySyntax()
    {
        var sql = MySqlToSqlite.Translate(
            "SELECT GROUP_CONCAT(DISTINCT CONCAT(a, ', ', b) ORDER BY a SEPARATOR '; ') FROM t WHERE d < DATE_SUB(NOW(), INTERVAL 2 WEEK)");

        Assert.Equal("SELECT mysql_group_concat(CONCAT(a, ', ', b), '; ', 1) FROM t WHERE d < datetime(NOW(), '-14 day')", sql);
    }

    private static ScopeJobExclusions Excluding() => new()
    {
        JobIds = [DemoSeeder.FirstJobId], CompanyIds = [101], ModelIds = [501],
    };

    private static object? ArgumentFor(ParameterInfo p, ScopeJobExclusions exclusions)
    {
        var name = p.Name!.ToLowerInvariant();
        int Id() => name switch
        {
            _ when name.Contains("vuom") => 6001,
            _ when name.Contains("inspection") => 30001,
            _ when name.Contains("equipment") => 10001,
            _ when name.Contains("model") => 500,
            _ when name.Contains("contact") => 2001,
            _ when name.Contains("user") => DemoSeeder.DemoUserId,
            _ when name.Contains("status") => 10,
            _ when name.Contains("company") || name.Contains("owner") => 100,
            _ when name.Contains("po") => 5001,
            _ when name.Contains("job") => DemoSeeder.FirstJobId + 400,
            _ when name.Contains("type") => 11,
            _ => 30,
        };

        var t = Nullable.GetUnderlyingType(p.ParameterType) ?? p.ParameterType;
        if (t == typeof(ScopeJobExclusions)) return exclusions;
        if (t == typeof(int)) return Id();
        if (t == typeof(long)) return (long)Id();
        if (t == typeof(bool)) return true;
        if (t == typeof(DateTime)) return name.Contains("end") || name.Contains("to") ? DateTime.Today : DateTime.Today.AddMonths(-6);
        if (t == typeof(string)) return name.Contains("company") ? "Hospital" : "a";
        if (typeof(IEnumerable<int>).IsAssignableFrom(t))
            return name.Contains("company") ? Enumerable.Range(100, 25).ToArray() : new[] { Id(), Id() + 1 };
        if (typeof(IEnumerable<string>).IsAssignableFrom(t)) return new[] { "RC-0" };
        if (p.HasDefaultValue) return p.DefaultValue;
        throw new NotSupportedException($"No test argument for {p.Name} ({p.ParameterType.Name}).");
    }
}
