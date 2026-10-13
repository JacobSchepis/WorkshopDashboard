using Microsoft.EntityFrameworkCore;
using WorkshopDashboard.Data;
using WorkshopDashboard.Models;
using WorkshopDashboard.Services.External.Jms;
using WorkshopDashboard.Tests.Fakes;

namespace WorkshopDashboard.Tests.Jms;

// Writes go straight to JMS when they can, wait in the JmsOutbox table when they can't, and
// are sent later in order, as the same user, never twice.
public class JmsWriteQueueTests
{
    private const int JobId = 1001;

    private static JmsWrite Note(string text) => JmsWrite.AddNote(JobId, text);

    private static Task<JmsWriteResult> SubmitAsync(JmsHarness h, JmsWrite write) =>
        h.Writes.SubmitAsync(JmsHarness.User, write);

    private static List<JmsOutboxItem> Outbox(JmsHarness h)
    {
        using var db = h.Db.CreateDbContext();
        return db.JmsOutbox.AsNoTracking().OrderBy(i => i.Id).ToList();
    }

    // Stands in for the backoff delay passing, so a rescheduled item is due again now.
    private static void MakeEverythingDue(JmsHarness h)
    {
        using var db = h.Db.CreateDbContext();
        db.JmsOutbox.ExecuteUpdate(s => s.SetProperty(i => i.NextAttemptAtUtc, DateTime.UtcNow.AddSeconds(-1)));
    }

    private static List<string> SentDescriptions(JmsHarness h) => h.Jms.ServiceLines.Select(l => l.Description).ToList();

    // ── Sending straight away ─────────────────────────────────────────────

    [Fact]
    public async Task WhenJmsIsUp_TheWriteIsSentStraightAway()
    {
        await using var h = await JmsHarness.StartAsync();
        await h.LoginAsync();

        var result = await SubmitAsync(h, Note("hello"));

        Assert.Equal(JmsWriteStatus.Done, result.Status);
        Assert.Equal(new[] { "hello" }, SentDescriptions(h));
        Assert.Empty(Outbox(h));
    }

    [Fact]
    public async Task Writes_AreSentAsTheUser_NotTheServiceAccount()
    {
        await using var h = await JmsHarness.StartAsync();
        await h.LoginAsync();

        await SubmitAsync(h, Note("hello"));

        Assert.Equal(JmsHarness.User, h.Jms.ServiceLines.Single().SentBy);
    }

    [Fact]
    public async Task StatusChange_IsSentStraightAway()
    {
        await using var h = await JmsHarness.StartAsync();
        await h.LoginAsync();

        var result = await SubmitAsync(h, new ChangeJobStatusWrite(JobId, JobStatus.LoanUnitProvided));

        Assert.Equal(JmsWriteStatus.Done, result.Status);
        Assert.Equal((int)JobStatus.LoanUnitProvided, h.Jms.JobStatuses[JobId]);
    }

    // ── Queueing ──────────────────────────────────────────────────────────

    [Fact]
    public async Task WhenJmsIsDown_TheWriteIsQueued_AndReportedAsAccepted()
    {
        await using var h = await JmsHarness.StartAsync();
        await h.LoginAsync();
        h.Status.Report(false, DateTime.Now);

        var result = await SubmitAsync(h, Note("hello"));

        Assert.Equal(JmsWriteStatus.Queued, result.Status);
        Assert.True(result.Accepted);
        Assert.Equal(202, result.ToResponse().StatusCode);
        Assert.Empty(h.Jms.ServiceLines);
        var item = Assert.Single(Outbox(h));
        Assert.Equal(JmsOutboxState.Pending, item.State);
        Assert.Equal(JmsHarness.User, item.Username);
        Assert.Contains("hello", item.Description);
    }

    [Fact]
    public async Task WhenJmsComesBack_QueuedWritesAreSentInOrder()
    {
        await using var h = await JmsHarness.StartAsync();
        await h.LoginAsync();
        h.Status.Report(false, DateTime.Now);
        await SubmitAsync(h, Note("first"));
        await SubmitAsync(h, new ChangeJobStatusWrite(JobId, JobStatus.LoanUnitProvided));
        await SubmitAsync(h, Note("third"));

        h.Status.Report(true, DateTime.Now);
        await h.Writes.ProcessDueAsync(CancellationToken.None);

        Assert.Equal(new[] { "first", "third" }, SentDescriptions(h));
        Assert.Equal((int)JobStatus.LoanUnitProvided, h.Jms.JobStatuses[JobId]);
        Assert.Empty(Outbox(h));
    }

    [Fact]
    public async Task NewWrite_WhileEarlierOnesAreWaiting_QueuesBehindThem()
    {
        await using var h = await JmsHarness.StartAsync();
        await h.LoginAsync();
        h.Status.Report(false, DateTime.Now);
        await SubmitAsync(h, Note("first"));

        h.Status.Report(true, DateTime.Now);
        var second = await SubmitAsync(h, Note("second"));

        // JMS is back, but sending "second" now would put it ahead of "first".
        Assert.Equal(JmsWriteStatus.Queued, second.Status);
        Assert.Empty(h.Jms.ServiceLines);

        await h.Writes.ProcessDueAsync(CancellationToken.None);
        Assert.Equal(new[] { "first", "second" }, SentDescriptions(h));
    }

    [Fact]
    public async Task WhileJmsIsStillDown_ProcessingSendsNothing()
    {
        await using var h = await JmsHarness.StartAsync();
        await h.LoginAsync();
        await h.TakeJmsDownAsync();
        await SubmitAsync(h, Note("waiting"));

        await h.Writes.ProcessDueAsync(CancellationToken.None);

        Assert.Single(Outbox(h));
    }

    [Fact]
    public async Task QueuedWrite_ForAUserWhosePasswordChanged_WaitsWithAClearReason()
    {
        await using var h = await JmsHarness.StartAsync();
        await h.LoginAsync();
        h.Status.Report(false, DateTime.Now);
        await SubmitAsync(h, Note("waiting"));

        h.Jms.SetPassword(JmsHarness.User, "new-pw");
        h.Jms.ExpireAllSessions();
        h.Status.Report(true, DateTime.Now);
        await h.Sessions.GetAsync(JmsHarness.User, forceCheck: true); // notice the expired session
        await h.Writes.ProcessDueAsync(CancellationToken.None);

        var item = Assert.Single(Outbox(h));
        Assert.Equal(JmsOutboxState.Pending, item.State);
        Assert.Contains("jms-reconnect", item.LastError);
        Assert.Empty(h.Jms.ServiceLines);
    }

    // ── Never applying twice ──────────────────────────────────────────────

    [Fact]
    public async Task WriteThatTimesOut_ButLandedInJms_IsNotSentAgain()
    {
        await using var h = await JmsHarness.StartAsync(requestTimeoutSeconds: 1);
        await h.LoginAsync();

        // JMS saves the note but only replies after the client has given up (1s timeout).
        h.Jms.WriteReplyDelay = TimeSpan.FromSeconds(3);
        var result = await SubmitAsync(h, Note("slow reply"));
        h.Jms.WriteReplyDelay = TimeSpan.Zero;

        Assert.Equal(JmsWriteStatus.Queued, result.Status);
        Assert.True(Assert.Single(Outbox(h)).NeedsCheck);

        await h.Writes.ProcessDueAsync(CancellationToken.None);

        Assert.Single(SentDescriptions(h), "slow reply");
        Assert.Empty(Outbox(h));
    }

    [Fact]
    public async Task WriteThatFailedWithAServerError_IsRetriedLater()
    {
        await using var h = await JmsHarness.StartAsync();
        await h.LoginAsync();

        h.Jms.WriteFailureStatus = 503;
        var result = await SubmitAsync(h, Note("retry me"));
        Assert.Equal(JmsWriteStatus.Queued, result.Status);

        // Still failing: rescheduled, not given up on.
        await h.Writes.ProcessDueAsync(CancellationToken.None);
        var item = Assert.Single(Outbox(h));
        Assert.Equal(1, item.Attempts);
        Assert.True(item.NextAttemptAtUtc > DateTime.UtcNow);

        // Recovered: sent once the backoff has passed.
        h.Jms.WriteFailureStatus = null;
        MakeEverythingDue(h);
        await h.Writes.ProcessDueAsync(CancellationToken.None);

        Assert.Equal(new[] { "retry me" }, SentDescriptions(h));
        Assert.Empty(Outbox(h));
    }

    [Fact]
    public async Task StatusChange_AlreadyApplied_IsNotSentAgain()
    {
        await using var h = await JmsHarness.StartAsync(requestTimeoutSeconds: 1);
        await h.LoginAsync();

        // JMS applies the status change but replies after the client has given up.
        h.Jms.WriteReplyDelay = TimeSpan.FromSeconds(3);
        await SubmitAsync(h, new ChangeJobStatusWrite(JobId, JobStatus.LoanUnitProvided));
        h.Jms.WriteReplyDelay = TimeSpan.Zero;
        Assert.Single(Outbox(h));

        await h.Writes.ProcessDueAsync(CancellationToken.None);

        // The job already has that status, so it's dropped rather than posted a second time.
        Assert.Equal(1, h.Jms.StatusChanges);
        Assert.Empty(Outbox(h));
    }

    // ── Refusals ──────────────────────────────────────────────────────────

    [Fact]
    public async Task WriteJmsRefuses_FailsStraightAway_AndIsNotQueued()
    {
        await using var h = await JmsHarness.StartAsync();
        await h.LoginAsync();

        var result = await SubmitAsync(h, Note("REJECT this"));

        Assert.Equal(JmsWriteStatus.Failed, result.Status);
        Assert.False(result.Accepted);
        Assert.Empty(Outbox(h));
    }

    [Fact]
    public async Task QueuedWriteJmsRefuses_IsMarkedFailed_AndDoesNotHoldUpLaterWrites()
    {
        await using var h = await JmsHarness.StartAsync();
        await h.LoginAsync();
        h.Status.Report(false, DateTime.Now);
        await SubmitAsync(h, Note("REJECT this"));
        await SubmitAsync(h, Note("after it"));

        h.Status.Report(true, DateTime.Now);
        await h.Writes.ProcessDueAsync(CancellationToken.None);

        Assert.Equal(new[] { "after it" }, SentDescriptions(h));
        var failed = Assert.Single(Outbox(h));
        Assert.Equal(JmsOutboxState.Failed, failed.State);
        Assert.Contains("400", failed.LastError);
    }

    // ── Managing the queue (/jms-pending) ─────────────────────────────────

    [Fact]
    public async Task FailedWrite_CanBeRetried()
    {
        await using var h = await JmsHarness.StartAsync();
        await h.LoginAsync();
        h.Status.Report(false, DateTime.Now);
        await SubmitAsync(h, Note("try again"));
        h.Status.Report(true, DateTime.Now);
        h.Jms.WriteFailureStatus = 400;
        await h.Writes.ProcessDueAsync(CancellationToken.None);
        var failed = Assert.Single(Outbox(h));
        Assert.Equal(JmsOutboxState.Failed, failed.State);

        h.Jms.WriteFailureStatus = null;
        await h.Writes.RetryAsync(JmsHarness.User, failed.Id);
        await h.Writes.ProcessDueAsync(CancellationToken.None);

        Assert.Equal(new[] { "try again" }, SentDescriptions(h));
        Assert.Empty(Outbox(h));
    }

    [Fact]
    public async Task QueuedWrite_CanBeDiscarded_AndIsNeverSent()
    {
        await using var h = await JmsHarness.StartAsync();
        await h.LoginAsync();
        h.Status.Report(false, DateTime.Now);
        await SubmitAsync(h, Note("never mind"));

        await h.Writes.DiscardAsync(JmsHarness.User, Outbox(h).Single().Id);
        h.Status.Report(true, DateTime.Now);
        await h.Writes.ProcessDueAsync(CancellationToken.None);

        Assert.Empty(h.Jms.ServiceLines);
        Assert.Empty(Outbox(h));
    }

    [Fact]
    public async Task UsersCanOnlyManageTheirOwnQueuedWrites()
    {
        await using var h = await JmsHarness.StartAsync();
        await h.LoginAsync();
        h.Status.Report(false, DateTime.Now);
        await SubmitAsync(h, Note("alice's"));

        await h.Writes.DiscardAsync("mallory", Outbox(h).Single().Id);

        Assert.Single(Outbox(h));
        Assert.Empty(await h.Writes.ListAsync("mallory"));
        Assert.Equal((1, 0), await h.Writes.CountAsync(JmsHarness.User));
    }

    [Fact]
    public async Task ChangedEvent_FiresForTheUserWhoseQueueChanged()
    {
        await using var h = await JmsHarness.StartAsync();
        await h.LoginAsync();
        var changed = new List<string>();
        h.Writes.Changed += changed.Add;

        h.Status.Report(false, DateTime.Now);
        await SubmitAsync(h, Note("hello"));

        Assert.Equal(new[] { JmsHarness.User }, changed);
    }
}
