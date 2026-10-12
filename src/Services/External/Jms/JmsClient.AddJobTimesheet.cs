using System.Globalization;
using WorkshopDashboard.Models;

namespace WorkshopDashboard.Services.External.Jms;

/// <summary>Fields for AddJobTimesheet's "Create New Timesheet" form — JMS's own
/// per-job labour log, the same one technicians fill in by hand. This is a direct mirror of
/// the form itself; TimeSheetService is the thin service wrapping this, and
/// TimesheetLoggingService (further up) is what actually decides labour type/charge
/// rate/job status defaults for automation time-saved entries.</summary>
public class AddJobTimesheetRequest
{
    public required int JobId { get; init; }
    public required int TechnicianUserId { get; init; }
    public required string TechnicianName { get; init; }

    /// <summary>The job_status_id to submit with this entry — JMS requires a "new_job_status"
    /// on every timesheet submission. Pass the job's own current status (see
    /// JobLookupService.GetJobStatusIdAsync) to log time without moving the job's workflow
    /// state.</summary>
    public required int JobStatusId { get; init; }

    public required DateTime StartAt { get; init; }
    public required double DurationHours { get; init; }
    public string Description { get; init; } = "";
    public string InternalNotes { get; init; } = "";

    /// <summary>JMS equipment_id already on the job, or 0 for "No Equipment".</summary>
    public int EquipmentId { get; init; } = 0;

    public JmsLabourType LabourType { get; init; } = JmsLabourType.Admin;

    /// <summary>Per-hour charge rate. This app has no lookup for a technician's real JMS pay
    /// rate, so this is whatever the caller supplies — 0 for entries that only need to prove
    /// hours/time-saved, not bill for them.</summary>
    public decimal ChargeRate { get; init; } = 0m;
}

public sealed partial class JmsClient
{
    // The reminder <select> has two options sharing value 24 — "4 Days" and a "----------"
    // separator option. JMS doesn't distinguish them server-side (only the value is posted),
    // and picking the separator is the site's own way of not actually setting a follow-up
    // reminder on a status-unchanged submission.
    private const int ReminderNoTimer = 24;

    /// <summary>Logs a labour/timesheet entry against a job via AddJobTimesheet's own
    /// "Create New Timesheet" form — the exact POST the site's own submit button makes.</summary>
    public async Task<HtmlResponse> AddJobTimesheetAsync(AddJobTimesheetRequest request, CancellationToken ct = default)
    {
        var finishAt = request.StartAt.AddHours(request.DurationHours);
        var totalCost = request.ChargeRate * (decimal)request.DurationHours;

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["labour_id"] = "0",
            ["labour_basis"] = "0.00",
            ["travel_basis"] = "0.00",
            ["task_class"] = "",
            ["task_class_check"] = "Flat Rate",
            ["technician_prof_disable"] = "",
            ["technician_prof"] = "",
            ["technician_rate_disable"] = "",
            ["technician_rate"] = "0",
            ["technician_travel_rate_disable"] = "",
            ["technician_travel_rate"] = "0",
            ["technician_recovery_rate_disable"] = "",
            ["technician_recovery_rate"] = "0",
            ["labour_date"] = request.StartAt.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture),
            ["labour_start_time"] = request.StartAt.ToString("HH:mm", CultureInfo.InvariantCulture),
            ["labour_finish_time"] = finishAt.ToString("HH:mm", CultureInfo.InvariantCulture),
            ["equipment_id_add"] = request.EquipmentId > 0 ? request.EquipmentId.ToString() : "",
            ["technician"] = request.TechnicianName,
            ["user_id"] = request.TechnicianUserId.ToString(),
            ["description"] = request.Description,
            ["description_internal"] = request.InternalNotes,
            ["labour_type"] = ((int)request.LabourType).ToString(),
            ["duration2"] = FormatDurationDisplay(request.DurationHours),
            ["duration"] = FormatDurationForJms(request.DurationHours),
            ["travel"] = "0",
            ["charge_rate"] = request.ChargeRate.ToString("0.00", CultureInfo.InvariantCulture),
            ["is_km_allowance"] = "",
            ["normal_payable_rates"] = "0",
            ["payable_kilometer_rates"] = "0",
            ["typed_charge_rate"] = request.ChargeRate.ToString("0.00", CultureInfo.InvariantCulture),
            ["total_cost_shown"] = totalCost.ToString("0.00", CultureInfo.InvariantCulture),
            ["calculated_charge"] = totalCost.ToString("0.00", CultureInfo.InvariantCulture),
            ["new_job_status"] = request.JobStatusId.ToString(),
            ["reminder"] = ReminderNoTimer.ToString(),
            ["notes"] = "",
            ["submit"] = "Create"
        });

        return await SendAsync(HttpMethod.Post, $"{E.AddJobTimesheet}?id={request.JobId}", form, ct);
    }

    private static string FormatDurationDisplay(double hours)
    {
        var (wholeHours, minutes) = SplitHours(hours);
        return $"{wholeHours} hours  {minutes} minutes";
    }

    // Mirrors AddJobTimesheet's own get_duration_popup() JS, so the "duration" value we
    // post matches what a real submission through the page would send: minutes/60 as a
    // string, truncated (not rounded) to 2 digits after the decimal point — e.g. 10 minutes
    // becomes "1.16", not "1.17".
    private static string FormatDurationForJms(double hours)
    {
        var (wholeHours, minutes) = SplitHours(hours);
        if (minutes == 0) return $"{wholeHours}.0";

        var fraction = (minutes / 60.0).ToString("R", CultureInfo.InvariantCulture);
        var decimalPart = fraction.Contains('.') ? fraction.Split('.')[1] : "0";
        var truncated = decimalPart.Length > 2 ? decimalPart.Substring(0, 2) : decimalPart;
        return $"{wholeHours}.{truncated}";
    }

    private static (int WholeHours, int Minutes) SplitHours(double hours)
    {
        var wholeHours = (int)Math.Floor(hours);
        var minutes = (int)Math.Round((hours - wholeHours) * 60);
        if (minutes == 60)
        {
            wholeHours++;
            minutes = 0;
        }
        return (wholeHours, minutes);
    }
}
