using System.Globalization;
using WorkshopDashboard.Abstract;
using WorkshopDashboard.Models;
using WorkshopDashboard.Services.External.Jms;

namespace WorkshopDashboard.Services.Jobs;

// ═══════════════════════════════════════════════════════════════════════════
//  TimeSheetService
//
//  The actual JMS call — takes essentially the same fields as
//  AddJobTimesheet's own form (see JmsClient.AddJobTimesheetAsync,
//  which this wraps) and posts one real timesheet entry. Deliberately thin
//  and unopinionated: no default labour type, no default charge rate, no
//  "who's logged in right now" resolution — that policy belongs one layer up,
//  in TimesheetLoggingService, which is what pages should actually call for
//  automation time-saved. This class exists so that layer (and anything else
//  that needs raw control over a submission) isn't duplicating JMS's form
//  shape or its job-status-lookup plumbing.
//
//  Two conveniences are kept here because they're really JMS requirements,
//  not policy:
//   - Job status: JMS requires a "new_job_status" on every submission, so if
//     the caller doesn't supply one, this fetches the job's current status
//     and resubmits it unchanged.
//   - Start time: JMS rejects overlapping labour rows for the same
//     technician/day (the same conflict check AddJobTimesheet's own JS
//     runs before letting you submit). If the caller doesn't supply a start
//     time, this queries the technician's last logged end time for today and
//     starts right after it — the same default the real page's date/time
//     pickers fill in on load.
// ═══════════════════════════════════════════════════════════════════════════

public class TimeSheetService
{
    private readonly JmsReadSession _jmsRead;
    private readonly JmsUserSession _jmsSession;
    private readonly JobLookupService _jobLookup;
    private readonly ILogger<TimeSheetService> _logger;

    public TimeSheetService(JmsReadSession jmsRead, JmsUserSession jmsSession, JobLookupService jobLookup,
        ILogger<TimeSheetService> logger)
    {
        _jmsRead = jmsRead;
        _jmsSession = jmsSession;
        _jobLookup = jobLookup;
        _logger = logger;
    }

    public async Task<bool> LogTimesheetAsync(
        int jobId,
        int technicianUserId,
        string technicianName,
        double durationHours,
        string description,
        string internalNotes = "",
        DateTime? startAt = null,
        JmsLabourType labourType = JmsLabourType.Admin,
        decimal chargeRate = 0m,
        int equipmentId = 0,
        int? jobStatusId = null,
        CancellationToken ct = default)
    {
        var jms = await _jmsSession.GetClientAsync();
        if (jms == null)
        {
            _logger.LogWarning("Timesheet for job {JobId} not logged: no JMS session", jobId);
            return false;
        }

        var statusId = jobStatusId ?? await _jobLookup.GetJobStatusIdAsync(jobId);
        if (statusId == null)
        {
            _logger.LogWarning("Timesheet for job {JobId} not logged: could not read the job's status", jobId);
            return false;
        }

        var effectiveStartAt = startAt ?? await GetNextAvailableStartTimeAsync(technicianUserId, DateTime.Today, ct);

        var request = new AddJobTimesheetRequest
        {
            JobId = jobId,
            TechnicianUserId = technicianUserId,
            TechnicianName = technicianName,
            JobStatusId = statusId.Value,
            StartAt = effectiveStartAt,
            DurationHours = durationHours,
            Description = description.Trim(),
            InternalNotes = internalNotes.Trim(),
            EquipmentId = equipmentId,
            LabourType = labourType,
            ChargeRate = chargeRate,
        };

        // A 200 response here doesn't prove anything got inserted — JMS's PHP can redisplay
        // the same form with a validation error and still return 200 (that page's "Create New
        // Timesheet" section is always present regardless of outcome, so text-sniffing the
        // response can't tell success from failure either). Comparing the job's raw labour
        // rows before/after is the only reliable signal — logged in full (not just a count) so
        // a broken filter (wrong user_id assumption, date format, etc.) is visible immediately
        // instead of silently defaulting to "nothing changed".
        var beforeRows = await GetLabourRowsAsync(jms, jobId);

        var res = await jms.AddJobTimesheetAsync(request, ct);

        if (!res.Success)
        {
            _logger.LogWarning("Timesheet POST for job {JobId} failed: {StatusCode} {ReasonPhrase}", jobId, res.StatusCode, res.ReasonPhrase);
            return false;
        }

        var afterRows = await GetLabourRowsAsync(jms, jobId);
        var newRows = afterRows.Where(a => !beforeRows.Any(b => b.LabourId == a.LabourId)).ToList();

        if (newRows.Count == 0)
        {
            var dumpPath = await DumpFailedResponseAsync(jobId, res.Content);
            _logger.LogWarning("Timesheet POST for job {JobId} returned {StatusCode} but no new labour row appeared "
                + "({Before} row(s) before, {After} after); response saved to {DumpPath}. Existing rows: {Rows}. "
                + "Request: technician {TechnicianId}, status {StatusId}, start {Start:yyyy-MM-dd HH:mm}, {Hours:0.###}h, "
                + "labour type {LabourType}, rate {ChargeRate}, equipment {EquipmentId}",
                jobId, res.StatusCode, beforeRows.Count, afterRows.Count, dumpPath,
                string.Join(" | ", afterRows.Select(r => $"#{r.LabourId} user={r.UserId} {r.StartDate}-{r.EndDate} \"{r.Description}\"")),
                technicianUserId, statusId, effectiveStartAt, durationHours, labourType, chargeRate, equipmentId);
            return false;
        }

        _logger.LogInformation("Timesheet logged on job {JobId}: {Rows}, technician {TechnicianId}, start {Start:yyyy-MM-dd HH:mm}, {Hours:0.###}h",
            jobId, string.Join(", ", newRows.Select(r => $"#{r.LabourId} user={r.UserId} {r.StartDate}-{r.EndDate}")),
            technicianUserId, effectiveStartAt, durationHours);
        return true;
    }

    private class LabourCsvRow
    {
        public int labour_id { get; set; }
        public int user_id { get; set; }
        public string? start_date { get; set; }
        public string? end_date { get; set; }
        public string? description { get; set; }
    }

    private record LabourRow(int LabourId, int UserId, string StartDate, string EndDate, string Description);

    private async Task<List<LabourRow>> GetLabourRowsAsync(IJmsReader jms, int jobId)
    {
        var csv = await jms.PostQueryForCsvAsync(JmsQueries.LabourRowsForJobQuery(jobId));
        if (!csv.Success) return new();

        try
        {
            using var reader = new StringReader(csv.Content);
            using var csvReader = new CsvHelper.CsvReader(reader, new CsvHelper.Configuration.CsvConfiguration(CultureInfo.InvariantCulture)
            {
                HeaderValidated = null,
                MissingFieldFound = null,
            });

            return csvReader.GetRecords<LabourCsvRow>()
                .Select(r => new LabourRow(r.labour_id, r.user_id, r.start_date ?? "", r.end_date ?? "", r.description ?? ""))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not parse the labour rows for job {JobId}: {Error}", jobId, ex.Message);
            return new();
        }
    }

    // Saves the raw HTML AddJobTimesheet actually returned when a POST didn't insert
    // anything, so the real rejection reason (a validation message, a redirect to login, etc.)
    // can be read directly off disk instead of guessing from the outside.
    private static async Task<string> DumpFailedResponseAsync(int jobId, string html)
    {
        try
        {
            var dir = Path.Combine("App_Data", "debug");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"timesheet-fail-job{jobId}-{DateTime.Now:yyyyMMdd-HHmmss}.html");
            await File.WriteAllTextAsync(path, html);
            return path;
        }
        catch (Exception ex)
        {
            return $"(could not save debug dump: {ex.Message})";
        }
    }

    /// <summary>The next default labour start time for the given day, or 8am if nothing's
    /// logged yet (the workday's own start time — see TimesheetLoggingService). Uses JMS's own
    /// LabourConflictCheck "get_last_time" endpoint (see JmsClient.GetLastLabourEndTimeAsync)
    /// rather than reading labour.start_date/end_date directly — those columns come back from
    /// the Query endpoint in a different timezone than the site displays (a 10-hour offset was
    /// observed), which silently produced start times that collided with existing entries.
    /// Exposed so TimesheetLoggingService can resolve one starting point and then chain several
    /// split entries after it locally, instead of every entry colliding on the same "now".
    /// technicianUserId isn't used by JMS's endpoint (it's scoped to the session, not a
    /// parameter) — kept in the signature for API stability / in case that ever changes.</summary>
    public async Task<DateTime> GetNextAvailableStartTimeAsync(int technicianUserId, DateTime date, CancellationToken ct = default)
    {
        var fallback = date.Date.AddHours(8);

        var jms = await _jmsSession.GetClientAsync();
        if (jms == null) return fallback;

        var time = await jms.GetLastLabourEndTimeAsync(date, ct);
        if (string.IsNullOrWhiteSpace(time)) return fallback;

        return TimeSpan.TryParse(time, CultureInfo.InvariantCulture, out var t) ? date.Date + t : fallback;
    }

    /// <summary>Total timesheet_duration logged by a technician on a given local day, straight
    /// from JMS (see JmsQueries.LabourHoursForTodayQuery for the fixed +10h offset this
    /// applies instead of proper timezone conversion — deliberately approximate, fine for a
    /// normal working day).</summary>
    public async Task<double> GetHoursLoggedOnDateAsync(int technicianUserId, DateTime date, CancellationToken ct = default)
    {
        var jms = await _jmsRead.GetClientAsync();
        if (jms == null) return 0;

        var csv = await jms.PostQueryForCsvAsync(JmsQueries.LabourHoursForTodayQuery(technicianUserId, date));
        var lines = csv.Content.Trim().Split('\n');
        if (lines.Length < 2) return 0;

        var raw = lines[1].Trim().Trim('"');
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var hours) ? hours : 0;
    }
}
