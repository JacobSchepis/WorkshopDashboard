using WorkshopDashboard.Services.Stores;

namespace WorkshopDashboard.Services.Jobs;

// One job's share of a logged entry, whether it actually posted, and whether it was skipped
// because taskKey was already logged today (see TimesheetLoggingService).
public record TimeSheetLogResult(int JobId, double EstimatedHours, bool Success, bool AlreadyLoggedToday = false);

// ═══════════════════════════════════════════════════════════════════════════
//  TimesheetLoggingService
//
//  What pages should actually inject to log automated work. Sits on top of
//  TimeSheetService (the thin, unopinionated form-fields wrapper) and
//  supplies the policy:
//   - Always ADMIN labour type, always $0 charge rate — this isn't about
//     billing, it's about proving how much time a task should have taken
//     done manually (so a day's worth of logged automation can add up to
//     more than a literal 8 hours, on top of whatever's actually worked).
//   - Always attributed to a fixed technician (TechnicianUserId/Name below),
//     not whoever happens to be logged into this app — this app's automation
//     is being credited to that one person's output regardless of who
//     triggers a given page.
//   - Every entry's free text goes entirely into JMS's internal notes field
//     (description_internal) — the customer-facing `description` field is
//     left blank, since job summaries can be printed for customers and
//     shouldn't mention internal tooling.
//   - Every entry's notes are stamped with the real wall-clock time the
//     entry was actually logged, since the entry's duration is itself a
//     synthetic "this should have taken X" estimate, not a literal
//     recording of elapsed time.
//   - Each job's active equipment (job_equipment.active_status = 't') is
//     looked up and attached to its entry automatically — callers never
//     need to know or pass an equipment id themselves.
//
//  Three ways to log, matching how the numbers are actually meant to be used:
//   - LogForJobAsync / LogForJobsAsync — work done against one or more real
//     JMS jobs (e.g. a repair-request booking that touched several jobs at
//     once). Splits one estimated-hours total evenly across them.
//   - LogVariableForJobsAsync — same idea, but each job gets its own hours
//     and notes (e.g. a quote-sent email where the logged time per job
//     depends on what was actually included for that job).
//   - LogAdminTaskAsync — general work not tied to any one job (e.g.
//     "checked all DHL shipments"). Posts a single entry against the fixed
//     admin job configured in TimesheetSettingsStore.
//
//  Either way, entries for the same technician/day are chained back-to-back
//  starting from TimeSheetService.GetNextAvailableStartTimeAsync (8am if
//  nothing's logged yet) rather than a bare "now", so a day's entries fill
//  the day out in order and JMS's same-technician overlap check never
//  rejects one.
//
//  Pass a stable taskKey (e.g. "waiting-workshop-tracking-check") to any of
//  the three methods for actions that should only ever land one entry per
//  technician per day no matter how many times the triggering page/button is
//  hit — checked against TimesheetTaskLogStore before anything is posted to
//  JMS, so a repeat click that day is a no-op rather than a duplicate entry.
//  Only marked as logged once every entry in the call succeeds, so a partial
//  failure can still be retried later that day.
// ═══════════════════════════════════════════════════════════════════════════

public class TimesheetLoggingService
{
    // Off switch while the logged times/notes are being reworked. False = every Log* call
    // posts nothing to JMS and returns no results (callers already ignore the results, apart
    // from WaitingQuoteDetail, which checks this flag for its status message). Flip back to
    // true to turn logging on again everywhere, including the Dev Tools Timesheet Test page.
    public static bool Enabled => false;

    // Every entry this service logs is attributed to this one technician, regardless of who's
    // actually logged into WorkshopDashboard — this app's automation is credited to this
    // person's output specifically. Same JMS user id already used elsewhere in this app (see
    // JmsClient.Services.cs's DefaultServiceUserId).
    private const int TechnicianUserId = 189;
    private const string TechnicianName = "Workshop Automation";

    private readonly TimeSheetService _timeSheet;
    private readonly TimesheetSettingsStore _settings;
    private readonly TimesheetTaskLogStore _taskLog;
    private readonly JobEquipmentLookup _jobEquipment;
    private readonly ILogger<TimesheetLoggingService> _logger;

    public TimesheetLoggingService(
        TimeSheetService timeSheet,
        TimesheetSettingsStore settings,
        TimesheetTaskLogStore taskLog,
        JobEquipmentLookup jobEquipment,
        ILogger<TimesheetLoggingService> logger)
    {
        _timeSheet = timeSheet;
        _settings = settings;
        _taskLog = taskLog;
        _jobEquipment = jobEquipment;
        _logger = logger;
    }

    /// <summary>Total hours logged in JMS today, straight from the labour table (see
    /// TimeSheetService.GetHoursLoggedOnDateAsync / JmsQueries.LabourHoursForTodayQuery for the
    /// fixed +10h offset applied instead of proper timezone conversion). For the on-page "how
    /// much have I logged today" counter (see TimesheetTest.razor).</summary>
    public Task<double> GetHoursLoggedTodayAsync(CancellationToken ct = default) =>
        _timeSheet.GetHoursLoggedOnDateAsync(TechnicianUserId, DateTime.Today, ct);

    /// <summary>Logs one task's estimated manual-equivalent time against a single job.</summary>
    public async Task<TimeSheetLogResult> LogForJobAsync(int jobId, double estimatedHours, string notes, string? taskKey = null, CancellationToken ct = default)
    {
        var results = await LogForJobsAsync(new[] { jobId }, estimatedHours, notes, taskKey, ct);
        return results.Count > 0 ? results[0] : new TimeSheetLogResult(jobId, 0, false);
    }

    /// <summary>Splits estimatedHours evenly across jobIds and logs one entry per job, all with
    /// the same notes text — e.g. a repair-request booking that created/touched several jobs at
    /// once. Split is computed in whole minutes, with any minute left over from rounding handed
    /// one-each to the first few jobs, so the logged total always equals estimatedHours. For
    /// jobs that need their own distinct hours/notes (e.g. per-job bonuses), use
    /// LogVariableForJobsAsync instead.</summary>
    public Task<List<TimeSheetLogResult>> LogForJobsAsync(
        IReadOnlyList<int> jobIds,
        double estimatedHours,
        string notes,
        string? taskKey = null,
        CancellationToken ct = default)
    {
        if (jobIds.Count == 0) return Task.FromResult(new List<TimeSheetLogResult>());

        var splitHours = SplitHoursEvenly(estimatedHours, jobIds.Count);
        var entries = jobIds.Select((id, i) => (JobId: id, EstimatedHours: splitHours[i], Notes: notes)).ToList();
        return LogVariableForJobsAsync(entries, taskKey, ct);
    }

    /// <summary>Logs one entry per job with its own hours and notes — e.g. a quote-sent email
    /// where each job's logged time depends on what was actually included for that job (PDF
    /// attached, PO approved, etc). Entries are posted in list order, each chained
    /// (non-overlapping) straight after the previous one's duration.</summary>
    public async Task<List<TimeSheetLogResult>> LogVariableForJobsAsync(
        IReadOnlyList<(int JobId, double EstimatedHours, string Notes)> entries,
        string? taskKey = null,
        CancellationToken ct = default)
    {
        if (entries.Count == 0 || !Enabled) return new();

        if (taskKey != null && await _taskLog.HasLoggedTodayAsync(TechnicianUserId, taskKey))
            return entries.Select(e => new TimeSheetLogResult(e.JobId, 0, true, AlreadyLoggedToday: true)).ToList();

        var results = new List<TimeSheetLogResult>(entries.Count);

        // JMS rejects overlapping labour rows for the same technician/day, so every entry
        // needs its own non-overlapping slot — start after whatever's already logged today
        // (8am if nothing has), then chain each subsequent entry straight after the previous
        // one's duration.
        var cursor = await _timeSheet.GetNextAvailableStartTimeAsync(TechnicianUserId, DateTime.Today, ct);

        foreach (var (jobId, hours, notes) in entries)
        {
            var equipmentId = await ResolveActiveEquipmentIdAsync(jobId);

            var success = await _timeSheet.LogTimesheetAsync(
                jobId,
                TechnicianUserId,
                TechnicianName,
                hours,
                description: "",
                internalNotes: WithLoggedTimestamp(notes),
                startAt: cursor,
                equipmentId: equipmentId,
                ct: ct);

            results.Add(new TimeSheetLogResult(jobId, hours, success));
            cursor = cursor.AddHours(hours);
        }

        if (taskKey != null && results.All(r => r.Success))
            await _taskLog.MarkLoggedTodayAsync(TechnicianUserId, taskKey);

        return results;
    }

    /// <summary>Logs a general task not tied to any specific job — e.g. "checked all DHL
    /// shipments" — against the fixed admin job configured in TimesheetSettingsStore.</summary>
    public async Task<TimeSheetLogResult> LogAdminTaskAsync(double estimatedHours, string notes, string? taskKey = null, CancellationToken ct = default)
    {
        if (!Enabled) return new TimeSheetLogResult(0, 0, false);

        var adminJobId = await _settings.GetAdminJobIdAsync();
        if (adminJobId == null)
        {
            _logger.LogWarning("No admin job id configured — set one via TimesheetSettingsStore before logging admin tasks");
            return new TimeSheetLogResult(0, 0, false);
        }

        return await LogForJobAsync(adminJobId.Value, estimatedHours, notes, taskKey, ct);
    }

    // A job can have several active equipment rows in theory; this app's real usage is one
    // active unit per job, so the first is good enough — matches "if there is one, use it".
    private async Task<int> ResolveActiveEquipmentIdAsync(int jobId)
    {
        var equipment = await _jobEquipment.LoadAsync(jobId);
        var first = equipment.FirstOrDefault();
        return first != null && int.TryParse(first.EquipmentId, out var id) ? id : 0;
    }

    private static string WithLoggedTimestamp(string notes) =>
        $"{notes.Trim()}\n\n(Logged {DateTime.Now:ddd d MMM yyyy, h:mm tt})";

    // Whole-minute split with the remainder from rounding given one-each to the first few
    // jobs (largest-remainder style) rather than dumped entirely on the last one.
    private static List<double> SplitHoursEvenly(double totalHours, int count)
    {
        var totalMinutes = (int)Math.Round(totalHours * 60);
        var baseMinutes = totalMinutes / count;
        var remainder = totalMinutes - baseMinutes * count;

        var minutes = Enumerable.Repeat(baseMinutes, count).ToList();
        for (var i = 0; i < remainder; i++)
            minutes[i]++;

        return minutes.Select(m => m / 60.0).ToList();
    }
}
