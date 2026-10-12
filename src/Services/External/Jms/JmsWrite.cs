using System.Globalization;
using System.Text.Json.Serialization;
using WorkshopDashboard.Abstract;
using WorkshopDashboard.Models;

namespace WorkshopDashboard.Services.External.Jms;

// ═══════════════════════════════════════════════════════════════════════════
//  JmsWrite
//
//  A JMS write described as data rather than a method call, so JmsWriteQueue
//  can save it and send it later if JMS can't take it right now. Only writes
//  whose caller doesn't need anything back from JMS belong here — service
//  lines and status changes. Anything that needs JMS's answer straight away
//  (a new job's id, an upload) stays a direct call and simply fails while JMS
//  is down.
//
//  AlreadyAppliedAsync is the duplicate guard: when an earlier send may have
//  reached JMS without us seeing the reply, the queue asks it before sending
//  again. Updates and deletes are safe to repeat, so they don't override it.
// ═══════════════════════════════════════════════════════════════════════════

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$kind")]
[JsonDerivedType(typeof(AddJobServiceWrite), "AddJobService")]
[JsonDerivedType(typeof(UpdateJobServiceWrite), "UpdateJobService")]
[JsonDerivedType(typeof(DeleteJobServiceWrite), "DeleteJobService")]
[JsonDerivedType(typeof(ChangeJobStatusWrite), "ChangeJobStatus")]
public abstract record JmsWrite(int JobId)
{
    public abstract Task<HtmlResponse> SendAsync(IJmsDataClient jms);

    /// <summary>Whether this write is already in JMS — asked only when an earlier send may
    /// have landed unseen. <paramref name="since"/> is how long ago it was first queued.</summary>
    public virtual Task<bool> AlreadyAppliedAsync(IJmsReader reader, TimeSpan since) => Task.FromResult(false);

    /// <summary>Plain-English summary for the pending-changes page.</summary>
    public abstract string Describe();

    // ── Builders matching JmsClient's typed Add*ServiceAsync methods ──

    public static JmsWrite AddQuoteLine(int jobId, string description, decimal costPrice, string equipmentId = "") =>
        new AddJobServiceWrite(jobId, JmsClient.BuildPricedRequest(JmsClient.ServiceTypeQuoted, description, costPrice, equipmentId));

    public static JmsWrite AddFreight(int jobId, string description, decimal costPrice, string equipmentId = "") =>
        new AddJobServiceWrite(jobId, JmsClient.BuildPricedRequest(JmsClient.ServiceTypeFreight, description, costPrice, equipmentId));

    public static JmsWrite AddNote(int jobId, string description, string equipmentId = "") =>
        new AddJobServiceWrite(jobId, JmsClient.BuildPricedRequest(JmsClient.ServiceTypeNote, description, 0m, equipmentId));

    protected static string Shorten(string text) =>
        text.Length <= 80 ? text : text[..77] + "...";
}

public sealed record AddJobServiceWrite(int JobId, JobServiceRequest Request) : JmsWrite(JobId)
{
    public override Task<HtmlResponse> SendAsync(IJmsDataClient jms) => jms.AddJobServiceAsync(JobId, Request);

    public override async Task<bool> AlreadyAppliedAsync(IJmsReader reader, TimeSpan since)
    {
        // Compared inside JMS's own database clock, so the app/JMS timezone offset doesn't matter.
        var withinMinutes = (int)Math.Ceiling(since.TotalMinutes) + 5;
        var csv = await reader.PostQueryForCsvAsync(
            JmsQueries.MatchingServiceCountQuery(JobId, Request.ServiceTypeId, Request.Description, withinMinutes));
        var lines = csv.Content.Trim().Split('\n');
        return csv.Success && lines.Length >= 2
            && int.TryParse(lines[1].Trim().Trim('"'), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0;
    }

    public override string Describe()
    {
        var kind = Request.ServiceTypeId switch
        {
            JmsClient.ServiceTypeQuoted => "quote line",
            JmsClient.ServiceTypeFreight => "freight line",
            JmsClient.ServiceTypeNote => "note",
            _ => "service line"
        };
        return $"Add {kind} to job {JobId}: {Shorten(Request.Description)}";
    }
}

public sealed record UpdateJobServiceWrite(int JobId, int ServiceId, JobServiceUpdateRequest Request) : JmsWrite(JobId)
{
    public override Task<HtmlResponse> SendAsync(IJmsDataClient jms) => jms.UpdateJobServiceAsync(JobId, ServiceId, Request);

    public override string Describe() => $"Update service line on job {JobId}: {Shorten(Request.Description)}";
}

public sealed record DeleteJobServiceWrite(int JobId, int ServiceId) : JmsWrite(JobId)
{
    public override Task<HtmlResponse> SendAsync(IJmsDataClient jms) => jms.DeleteJobServiceAsync(JobId, ServiceId);

    public override string Describe() => $"Delete service line {ServiceId} from job {JobId}";
}

public sealed record ChangeJobStatusWrite(int JobId, JobStatus Status, string Notes = "", int ReminderHours = 24) : JmsWrite(JobId)
{
    public override Task<HtmlResponse> SendAsync(IJmsDataClient jms) => jms.ChangeJobStatusAsync(JobId, Status, Notes, ReminderHours);

    public override async Task<bool> AlreadyAppliedAsync(IJmsReader reader, TimeSpan since)
    {
        var csv = await reader.PostQueryForCsvAsync(JmsQueries.JobStatusIdQuery(JobId));
        var lines = csv.Content.Trim().Split('\n');
        return csv.Success && lines.Length >= 2 && int.TryParse(lines[1].Trim().Trim('"'), out var current) && current == (int)Status;
    }

    public override string Describe() => $"Change job {JobId} status to {Status}";
}
