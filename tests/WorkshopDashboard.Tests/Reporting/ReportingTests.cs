using WorkshopDashboard.Models;
using WorkshopDashboard.Services.External.Jms;
using WorkshopDashboard.Services.Reporting;

namespace WorkshopDashboard.Tests.Reporting;

// JMS uptime totals (work hours only), scope report periods, and scope report stat maths.
public class ReportingTests
{
    private static readonly DateTime Day = new(2026, 9, 28);

    private static JmsOutagePeriod Outage(DateTime start, DateTime? end) => new() { StartedAt = start, EndedAt = end };

    private static TimeSpan Downtime(DateTime? now = null, params JmsOutagePeriod[] outages) =>
        JmsUptimeReport.TotalDowntime(outages, Day.AddDays(-7), Day.AddDays(7), now);

    // ── JMS uptime ──

    [Fact]
    public void OutageDuringWorkHours_CountsInFull()
    {
        Assert.Equal(TimeSpan.FromMinutes(90), Downtime(null, Outage(Day.AddHours(10), Day.AddHours(11.5))));
    }

    [Fact]
    public void OutageBeforeEightAm_OnlyCountsFromEight()
    {
        Assert.Equal(TimeSpan.FromHours(1), Downtime(null, Outage(Day.AddHours(6), Day.AddHours(9))));
    }

    [Fact]
    public void OvernightOutage_IsSplitAcrossBothDays()
    {
        // 4pm to 10am next day: 1h on the first day (4–5pm), 2h on the second (8–10am).
        var daily = JmsUptimeReport.DailyDowntime(
            new[] { Outage(Day.AddHours(16), Day.AddDays(1).AddHours(10)) }, Day, Day.AddDays(2));

        Assert.Equal(TimeSpan.FromHours(1), daily[Day]);
        Assert.Equal(TimeSpan.FromHours(2), daily[Day.AddDays(1)]);
    }

    [Fact]
    public void OngoingOutage_CountsUpToNow()
    {
        Assert.Equal(TimeSpan.FromMinutes(90), Downtime(Day.AddHours(16.5), Outage(Day.AddHours(15), null)));
    }

    [Fact]
    public void OutageOutsideTheRange_CountsForNothing()
    {
        var total = JmsUptimeReport.TotalDowntime(
            new[] { Outage(Day.AddDays(-30).AddHours(9), Day.AddDays(-30).AddHours(12)) }, Day, Day.AddDays(1));

        Assert.Equal(TimeSpan.Zero, total);
    }

    // ── Scope report periods ──

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nonsense")]
    public void UnknownPeriod_FallsBackToTheDefault(string? key)
    {
        Assert.Equal(ScopeReportingPeriod.DefaultKey, ScopeReportingPeriod.Resolve(key).Key);
    }

    [Fact]
    public void PeriodKeys_AreCaseInsensitive()
    {
        Assert.Equal("last30", ScopeReportingPeriod.Resolve("LAST30").Key);
    }

    [Fact]
    public void CalendarYears_StartAt2025_AndTheCurrentYearEndsToday()
    {
        var all = ScopeReportingPeriod.All();

        Assert.Equal(new DateTime(2025, 1, 1), all.Single(p => p.Key == "2025").Start);
        Assert.Equal(DateTime.Today, all.Single(p => p.Key == DateTime.Today.Year.ToString()).End);
        Assert.DoesNotContain(all, p => p.Key == "2024");
    }

    // ── Scope report stats ──

    private const string StatsCsv =
        "owner_company_id,job_count,completed_count,open_count,closed_without_invoice_count,invoiced_total,paid_total,turnaround_days_sum,turnaround_job_count\n" +
        "10,5,3,2,1,1000.50,400,30,3\n" +
        "20,2,2,0,0,200,200,2,1\n";

    private const string ResponseCsv =
        "owner_company_id,response_days_sum,response_job_count\n" +
        "10,8,2\n" +
        "30,4,1\n";

    [Fact]
    public void Stats_AreParsedPerCompany()
    {
        var stats = ScopeReportStatsParser.Parse(StatsCsv);

        Assert.Equal(5, stats[10].JobCount);
        Assert.Equal(600.50m, stats[10].OutstandingTotal);
        Assert.Equal(10d, stats[10].AvgTurnaroundDays);
    }

    [Fact]
    public void ResponseTimes_MergeWithTheOtherStats_EvenWhereOnlyOneSideHasACompany()
    {
        var merged = ScopeReportStatsParser.Merge(
            ScopeReportStatsParser.Parse(StatsCsv), ScopeReportStatsParser.ParseResponseTimes(ResponseCsv));

        Assert.Equal(5, merged[10].JobCount);
        Assert.Equal(4d, merged[10].AvgResponseDays);
        Assert.Null(merged[20].AvgResponseDays);
        Assert.Equal(0, merged[30].JobCount);
        Assert.Equal(4d, merged[30].AvgResponseDays);
    }

    [Fact]
    public void Aggregate_SumsTheGivenCompanies_AndAveragesAcrossThemCorrectly()
    {
        var stats = ScopeReportStatsParser.Parse(StatsCsv);

        var total = ScopeReportStatsParser.Aggregate(stats, new[] { 10, 20, 999 });

        Assert.Equal(7, total.JobCount);
        Assert.Equal(1200.50m, total.InvoicedTotal);
        Assert.Equal(8d, total.AvgTurnaroundDays); // (30 + 2) / (3 + 1) — not the mean of the two averages (10 and 2 → 6)
    }

    [Fact]
    public void Averages_AreBlank_WhenThereAreNoJobs()
    {
        var empty = new ScopeReportStats();

        Assert.Null(empty.AvgTurnaroundDays);
        Assert.Null(empty.AvgResponseDays);
    }
}
