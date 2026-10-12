using WorkshopDashboard.Models;

namespace WorkshopDashboard.Services.External.Jms;

public sealed partial class JmsClient
{
    /// <summary>Changes a job's status via JobSummary's own "Change Status" form
    /// (new_job_status / reminder / notes / change_status=Submit) — the exact POST the
    /// site's own status-change button makes.</summary>
    public async Task<HtmlResponse> ChangeJobStatusAsync(int jobId, JobStatus newStatus, string notes = "", int reminderHours = 24, CancellationToken ct = default)
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            { "new_job_status", ((int)newStatus).ToString() },
            { "reminder", reminderHours.ToString() },
            { "notes", notes },
            { "change_status", "Submit" }
        });

        return await SendAsync(HttpMethod.Post, $"{E.JobSummary}?id={jobId}", form, ct);
    }
}
