using CsvHelper;
using CsvHelper.Configuration;
using System.Globalization;

using WorkshopDashboard.Services.External.Jms;
using WorkshopDashboard.Services.Workshop;
namespace WorkshopDashboard.Services.Jobs;

public record TimesheetNote(DateTime? Date, string Text);

public record JobByIdRow(int JobId, string? OwnerName, string? Notes, int JobPriorityId);

/// <summary>
/// Small job/company identity lookups shared by the quote extractor pages (job -> owner
/// company id/name) and UploadDocument.razor (job info CSV). Kept as one cohesive class
/// rather than one per query since they're all trivial single-value/single-query lookups
/// against the same "which company/job is this" concern.
/// </summary>
public class JobLookupService
{
    private readonly JmsReadSession _jmsRead;

    public JobLookupService(JmsReadSession jmsRead) => _jmsRead = jmsRead;

    public async Task<int?> GetOwnerCompanyIdAsync(int jobId)
    {
        var jms = await _jmsRead.GetClientAsync();
        if (jms == null) return null;

        var csv = await jms.PostQueryForCsvAsync(JmsQueries.OwnerCompanyIdQuery(jobId));
        var lines = csv.Content.Trim().Split('\n');
        if (lines.Length < 2 || !int.TryParse(lines[1].Trim(), out var companyId))
            return null;

        return companyId;
    }

    public async Task<int?> GetJobStatusIdAsync(int jobId)
    {
        var jms = await _jmsRead.GetClientAsync();
        if (jms == null) return null;

        var csv = await jms.PostQueryForCsvAsync(JmsQueries.JobStatusIdQuery(jobId));
        var lines = csv.Content.Trim().Split('\n');
        if (lines.Length < 2 || !int.TryParse(lines[1].Trim(), out var statusId))
            return null;

        return statusId;
    }

    public async Task<string?> GetCompanyNameAsync(int companyId)
    {
        var jms = await _jmsRead.GetClientAsync();
        if (jms == null) return null;

        var csv = await jms.PostQueryForCsvAsync(JmsQueries.CompanyNameQuery(companyId));
        var lines = csv.Content.Trim().Split('\n');
        return lines.Length >= 2 ? lines[1].Trim() : null;
    }

    // Returns the raw CSV content for UploadDocument.razor's own ParseJobInfoCsv, which
    // aggregates several joined status-history/equipment rows into one JobInfo — kept in
    // the page since it's page-specific display shaping, not a reusable data lookup.
    public async Task<string?> GetJobInfoCsvAsync(long jobId)
    {
        var jms = await _jmsRead.GetClientAsync();
        if (jms == null) return null;

        var csv = await jms.PostQueryForCsvAsync(JmsQueries.JobInfoQuery(jobId));
        return csv.Success ? csv.Content : null;
    }

    // Used by InternationalShipping.razor's "AU/Jobs" tab — job + owner + notes/priority,
    // no equipment join (see JobEquipmentLookup for the equipment-joined variants).
    public async Task<List<JobByIdRow>> GetJobsByIdsAsync(IEnumerable<int> jobIds)
    {
        var ids = jobIds.ToList();
        if (ids.Count == 0) return new();

        var jms = await _jmsRead.GetClientAsync();
        if (jms == null) return new();

        var res = await jms.PostQueryForCsvAsync(JmsQueries.JobsByIdsQuery(ids));
        if (!res.Success) return new();

        var config = WaitingQuoteReport.MakeCsvConfig();
        using var reader = new StringReader(res.Content);
        using var csv = new CsvReader(reader, config);

        return csv.GetRecords<JobByIdCsvRow>()
            .Select(r => new JobByIdRow(r.job_id, r.owner_name, r.notes, r.job_priority_id))
            .ToList();
    }

    /// <summary>The newest timesheet entry with text on the job, by anyone, or null if none.
    /// labour.start_date is stored ~10 hours behind local time (see
    /// JmsQueries.LabourHoursForTodayQuery), so it's shifted forward for display.</summary>
    public async Task<TimesheetNote?> GetLatestTimesheetNoteAsync(int jobId)
    {
        var jms = await _jmsRead.GetClientAsync();
        if (jms == null) return null;

        var res = await jms.PostQueryForCsvAsync(JmsQueries.LatestTimesheetNoteForJobQuery(jobId));
        if (!res.Success) throw new Exception($"Timesheet lookup failed: {res.StatusCode} {res.ReasonPhrase}");
        if (string.IsNullOrWhiteSpace(res.Content)) return null;

        using var reader = new StringReader(res.Content);
        using var csv = new CsvReader(reader, WaitingQuoteReport.MakeCsvConfig());
        var row = csv.GetRecords<TimesheetNoteCsvRow>().FirstOrDefault();
        return row == null ? null : new TimesheetNote(row.start_date?.AddHours(10), row.description ?? "");
    }

    private class TimesheetNoteCsvRow
    {
        public int labour_id { get; set; }
        public DateTime? start_date { get; set; }
        public string? description { get; set; }
    }
    private class JobByIdCsvRow
    {
        public int job_id { get; set; }
        public string? notes { get; set; }
        public int job_priority_id { get; set; }
        public string? owner_name { get; set; }
    }
}
