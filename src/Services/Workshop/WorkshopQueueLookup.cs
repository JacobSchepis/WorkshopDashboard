using CsvHelper;
using CsvHelper.Configuration;
using System.Globalization;
using WorkshopDashboard.Abstract;
using WorkshopDashboard.Services.Shipping;
using WorkshopDashboard.Services.External.Shipping;
using WorkshopDashboard.Services.Workshop;

using WorkshopDashboard.Services.External.Jms;
using WorkshopDashboard.Services.Stores;
namespace WorkshopDashboard.Services.Workshop;

public record StaleJobRecord(
    string JobId,
    string Owner,
    string Equipment,
    string Fault,
    string Status,
    DateTime LastStatusChange);

public record WorkshopAssignmentJob(
    string JobId,
    string? TrackingRaw,
    string Owner,
    string Equipment,
    string Fault,
    DateTime LastStatusChange,
    int? TrackingServiceId = null);

/// <summary>
/// Read-only job-queue lookups for the simpler StaleJobs-family pages (no per-job
/// writes, unlike WaitingQuoteConfirmation/WaitingWorkshopProcessing which get their
/// own dedicated services alongside their save/toggle actions).
/// </summary>
public class WorkshopQueueLookup
{
    private readonly JmsReadSession _jmsRead;

    public WorkshopQueueLookup(JmsReadSession jmsRead) => _jmsRead = jmsRead;

    public async Task<List<StaleJobRecord>> GetStaleJobsAsync()
    {
        var jms = await _jmsRead.GetClientAsync();
        return jms == null ? new() : await RunStaleJobsQueryAsync(jms, JmsQueries.StaleJobsQuery());
    }

    public async Task<List<StaleJobRecord>> GetStaleJobsFilteredAsync(ScopeJobExclusions exclusions)
    {
        var jms = await _jmsRead.GetClientAsync();
        return jms == null ? new() : await GetStaleJobsFilteredAsync(jms, exclusions);
    }

    // Static overload for the kiosk WorkshopNotifications page, which has its own IJmsReader.
    public static Task<List<StaleJobRecord>> GetStaleJobsFilteredAsync(IJmsReader jms, ScopeJobExclusions exclusions) =>
        RunStaleJobsQueryAsync(jms, JmsQueries.StaleJobsFilteredQuery(exclusions));

    private static async Task<List<StaleJobRecord>> RunStaleJobsQueryAsync(IJmsReader jms, string query)
    {
        var res = await jms.PostQueryForCsvAsync(query);
        if (!res.Success) return new();

        using var reader = new StringReader(res.Content);
        using var csv = new CsvReader(reader, CsvConfig());

        return csv.GetRecords<StaleJobCsvRow>()
            .Select(r => new StaleJobRecord(
                r.job_id.ToString(),
                r.owner ?? "",
                r.equipment ?? "",
                r.fault ?? "",
                r.job_status ?? "",
                DateTime.TryParse(r.last_status_change, out var d) ? d : DateTime.MinValue))
            .ToList();
    }

    public async Task<List<WorkshopAssignmentJob>> GetWaitingWorkshopAssignmentJobsAsync(ScopeJobExclusions exclusions)
    {
        var jms = await _jmsRead.GetClientAsync();
        return jms == null ? new() : await GetWaitingWorkshopAssignmentJobsAsync(jms, exclusions);
    }

    public static Task<List<WorkshopAssignmentJob>> GetWaitingWorkshopAssignmentJobsAsync(IJmsReader jms, ScopeJobExclusions exclusions) =>
        RunTrackingJobsQueryAsync(jms, JmsQueries.WaitingWorkshopAssignmentQuery(exclusions));

    // Repairer -> workshop leg (the unit's finished repair is shipping back). Same row shape as
    // waiting-workshop-assignment (repairer-bound tracking), just a different query/status.
    public async Task<List<WorkshopAssignmentJob>> GetWorkshopFinalisationCompleteJobsAsync(ScopeJobExclusions exclusions)
    {
        var jms = await _jmsRead.GetClientAsync();
        return jms == null ? new() : await GetWorkshopFinalisationCompleteJobsAsync(jms, exclusions);
    }

    // Static overload for callers that already hold their own IJmsReader — the kiosk
    // WorkshopNotifications page (via HeldShipmentLookup), same reasoning as
    // DashboardLookup's static overloads.
    public static Task<List<WorkshopAssignmentJob>> GetWorkshopFinalisationCompleteJobsAsync(IJmsReader jms, ScopeJobExclusions exclusions) =>
        RunTrackingJobsQueryAsync(jms, JmsQueries.WorkshopFinalisationCompleteQuery(exclusions));

    private static async Task<List<WorkshopAssignmentJob>> RunTrackingJobsQueryAsync(IJmsReader jms, string query)
    {
        var res = await jms.PostQueryForCsvAsync(query);
        if (!res.Success) return new();

        using var reader = new StringReader(res.Content);
        using var csv = new CsvReader(reader, CsvConfig());

        return csv.GetRecords<AssignmentCsvRow>()
            .Select(r => new WorkshopAssignmentJob(
                r.job_id.ToString(),
                r.tracking_raw,
                r.owner ?? "",
                r.equipment ?? "",
                r.fault ?? "",
                DateTime.TryParse(r.last_status_change, out var d) ? d : DateTime.MinValue,
                r.tracking_service_id))
            .ToList();
    }

    private static CsvConfiguration CsvConfig() => WaitingQuoteReport.MakeCsvConfig();

    private class StaleJobCsvRow
    {
        public int job_id { get; set; }
        public string? owner { get; set; }
        public string? fault { get; set; }
        public string? job_status { get; set; }
        public string? equipment { get; set; }
        public string? last_status_change { get; set; }
    }

    private class AssignmentCsvRow
    {
        public int job_id { get; set; }
        public string? tracking_raw { get; set; }
        // Only populated by WorkshopFinalisationCompleteQuery — WaitingWorkshopAssignmentQuery
        // has no such column, and MakeCsvConfig's MissingFieldFound:null leaves this null
        // rather than erroring for that query.
        public int? tracking_service_id { get; set; }
        public string? owner { get; set; }
        public string? fault { get; set; }
        public string? job_status { get; set; }
        public string? equipment { get; set; }
        public string? last_status_change { get; set; }
    }
}
