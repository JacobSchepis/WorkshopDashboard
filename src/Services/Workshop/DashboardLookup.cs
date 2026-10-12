using CsvHelper;
using WorkshopDashboard.Abstract;

using WorkshopDashboard.Services.External.Jms;
using WorkshopDashboard.Services.Stores;
namespace WorkshopDashboard.Services.Workshop;

public record DashboardStatusCount(string StatusName, int Total, int Overdue);

public record HighlightedJobRowInfo(int JobId, string? JobNotes, string? JobStatus, string? Owner, string? Equipment);

public record WorkshopDashboardCounts(
    int WOpen,
    int ScopeOpen,
    int WaitingArrival,
    int UnitDeparted,
    int CompletedLast30Days,
    int CompletedToday);

/// <summary>
/// Small aggregate-count queries backing dashboard tiles — Home.razor's per-status counts
/// and highlighted-job details, and WorkshopStatus.razor's kiosk summary tiles. Grouped as
/// one class since they're all "a handful of numbers for a dashboard card," not because
/// the two pages share anything else.
///
/// WorkshopStatus.razor is a kiosk page (KioskPairingComponentBase — anonymous, paired via
/// device code rather than a login cookie) that gets its IJmsReader from the kiosk base
/// class rather than DI-injecting this service; GetWorkshopCountsAsync's static overload takes that client directly so
/// the query/parse logic still lives in one place instead of being duplicated in the page.
/// </summary>
public class DashboardLookup
{
    private readonly JmsReadSession _jmsRead;

    public DashboardLookup(JmsReadSession jmsRead) => _jmsRead = jmsRead;

    public async Task<List<DashboardStatusCount>> GetStatusCountsAsync(int overdueAfterDays, ScopeJobExclusions exclusions)
    {
        var jms = await _jmsRead.GetClientAsync();
        return jms == null ? new() : await GetStatusCountsAsync(jms, overdueAfterDays, exclusions);
    }

    // Static overload for callers that already hold their own IJmsReader — kiosk pages
    // (see class doc comment) and RuleCheckService, which runs from WorkshopNotifications.razor.
    public static async Task<List<DashboardStatusCount>> GetStatusCountsAsync(IJmsReader jms, int overdueAfterDays, ScopeJobExclusions exclusions)
    {
        var query = JmsQueries.DashboardCountsQuery(overdueAfterDays, exclusions);
        var res = await jms.PostQueryForCsvAsync(query);
        if (!res.Success) return new();

        using var reader = new StringReader(res.Content);
        using var csv = new CsvReader(reader, WaitingQuoteReport.MakeCsvConfig());
        return csv.GetRecords<CountRow>()
            .Select(r => new DashboardStatusCount(
                string.IsNullOrWhiteSpace(r.job_status) ? "(no status)" : r.job_status!,
                r.total,
                r.overdue))
            .ToList();
    }

    public async Task<List<HighlightedJobRowInfo>> GetHighlightedJobDetailsAsync(IEnumerable<int> ids)
    {
        var jms = await _jmsRead.GetClientAsync();
        return jms == null ? new() : await GetHighlightedJobDetailsAsync(jms, ids);
    }

    // Static overload for the kiosk WorkshopNotifications page, which has its own IJmsReader.
    public static async Task<List<HighlightedJobRowInfo>> GetHighlightedJobDetailsAsync(IJmsReader jms, IEnumerable<int> ids)
    {
        var idList = ids.ToList();
        if (idList.Count == 0) return new();

        var query = JmsQueries.HighlightedJobsQuery(idList);
        var res = await jms.PostQueryForCsvAsync(query);
        if (!res.Success) return new();

        using var reader = new StringReader(res.Content);
        using var csv = new CsvReader(reader, WaitingQuoteReport.MakeCsvConfig());
        return csv.GetRecords<HighlightedJobRowCsv>()
            .Select(r => new HighlightedJobRowInfo(r.job_id, r.job_notes, r.job_status, r.owner, r.equipment))
            .ToList();
    }

    public async Task<WorkshopDashboardCounts?> GetWorkshopCountsAsync(ScopeJobExclusions exclusions)
    {
        var jms = await _jmsRead.GetClientAsync();
        return jms == null ? null : await GetWorkshopCountsAsync(jms, exclusions);
    }

    // "Open scope jobs" (scope_open) and "open workshop jobs" (the complement of scope_open
    // within w_open) both come from one query — see JmsQueries.WorkshopDashboardCountsQuery's
    // comment for the full definition.
    public static async Task<WorkshopDashboardCounts?> GetWorkshopCountsAsync(IJmsReader jms, ScopeJobExclusions exclusions)
    {
        var res = await jms.PostQueryForCsvAsync(JmsQueries.WorkshopDashboardCountsQuery(exclusions));
        if (!res.Success) return null;

        using var reader = new StringReader(res.Content);
        using var csv = new CsvReader(reader, WaitingQuoteReport.MakeCsvConfig());
        var row = csv.GetRecords<WorkshopCountsRow>().FirstOrDefault();
        return row == null
            ? null
            : new WorkshopDashboardCounts(row.w_open, row.scope_open, row.waiting_arrival, row.unit_departed, row.completed_last_30_days, row.completed_today);
    }

    private class CountRow
    {
        public int? job_status_id { get; set; }
        public string? job_status { get; set; }
        public int total { get; set; }
        public int overdue { get; set; }
    }

    private class HighlightedJobRowCsv
    {
        public int job_id { get; set; }
        public string? job_notes { get; set; }
        public string? job_status { get; set; }
        public string? owner { get; set; }
        public string? equipment { get; set; }
    }

    private class WorkshopCountsRow
    {
        public int w_open { get; set; }
        public int scope_open { get; set; }
        public int waiting_arrival { get; set; }
        public int unit_departed { get; set; }
        public int completed_last_30_days { get; set; }
        public int completed_today { get; set; }
    }
}
