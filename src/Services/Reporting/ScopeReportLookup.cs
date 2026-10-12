using WorkshopDashboard.Services.External.Jms;
using WorkshopDashboard.Services.Jobs;
using WorkshopDashboard.Services.Workshop;
using WorkshopDashboard.Services.Admin;
using WorkshopDashboard.Services.Stores;

namespace WorkshopDashboard.Services.Reporting;

/// <summary>
/// Runs BuildScopeReportStatsQuery + BuildScopeResponseTimeQuery and merges them into one
/// per-company map — the exact orchestration ScopeReportingOverview/State/Customer.razor
/// each duplicated (only the customer subset passed in differed). Row parsing itself stays
/// in ScopeReportStatsParser, which callers unrelated to a single "run both and merge" flow
/// can still use directly.
/// </summary>
public class ScopeReportLookup
{
    private readonly JmsReadSession _jmsRead;

    public ScopeReportLookup(JmsReadSession jmsRead) => _jmsRead = jmsRead;

    public async Task<Dictionary<int, ScopeReportStats>> GetStatsByCompanyAsync(
        IEnumerable<int> companyIds, ScopeJobExclusions exclusions, DateTime start, DateTime end)
    {
        var jms = await _jmsRead.GetClientAsync();
        if (jms == null) return new();

        var ids = companyIds.ToList();
        var statsQuery = JmsQueries.BuildScopeReportStatsQuery(ids, exclusions, start, end);
        var responseQuery = JmsQueries.BuildScopeResponseTimeQuery(ids, exclusions, start, end);

        var statsTask = jms.PostQueryForCsvAsync(statsQuery);
        var responseTask = jms.PostQueryForCsvAsync(responseQuery);
        await Task.WhenAll(statsTask, responseTask);

        var stats = statsTask.Result.Success
            ? ScopeReportStatsParser.Parse(statsTask.Result.Content)
            : new();
        var responseTimes = responseTask.Result.Success
            ? ScopeReportStatsParser.ParseResponseTimes(responseTask.Result.Content)
            : new();

        return ScopeReportStatsParser.Merge(stats, responseTimes);
    }
}
