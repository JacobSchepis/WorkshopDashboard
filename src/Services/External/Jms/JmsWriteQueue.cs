using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using WorkshopDashboard.Data;
using WorkshopDashboard.Models;

namespace WorkshopDashboard.Services.External.Jms;

public enum JmsWriteStatus
{
    /// <summary>JMS accepted it just now.</summary>
    Done,
    /// <summary>Saved to send as soon as JMS can take it — see /jms-pending.</summary>
    Queued,
    /// <summary>JMS refused it (not an outage) — nothing was saved to retry.</summary>
    Failed
}

public readonly record struct JmsWriteResult(JmsWriteStatus Status, HtmlResponse? Response)
{
    /// <summary>Sent or safely queued — what callers that used to check res.Success want.</summary>
    public bool Accepted => Status != JmsWriteStatus.Failed;

    /// <summary>For callers that hand an HtmlResponse on: a queued write reads as 202 Accepted.</summary>
    public HtmlResponse ToResponse() => Response ?? new HtmlResponse(
        Accepted, Accepted ? 202 : 0, Accepted ? "Queued — will be sent to JMS when it's back." : "JMS unavailable.",
        "", null, null, null, new Dictionary<string, string>());
}

// ═══════════════════════════════════════════════════════════════════════════
//  JmsWriteQueue
//
//  Every queueable JMS write (see JmsWrite) goes through SubmitAsync, which
//  sends it straight away when it can — so normally nothing changes — and
//  otherwise saves it to the JmsOutbox table:
//    - JMS is down, or this user can't get a session right now;
//    - the send failed in a way JMS might not have seen (timeout, 5xx) —
//      saved with NeedsCheck so it's never applied twice;
//    - this user already has writes waiting, so the new one goes behind them
//      (keeps a quote's lines in the order they were posted).
//  A refusal from JMS itself (4xx) is returned as Failed and not saved.
//
//  JmsWriteQueueWorker calls ProcessDueAsync to drain the table, each user's
//  writes in order, as that user, with backoff between attempts.
// ═══════════════════════════════════════════════════════════════════════════

public sealed class JmsWriteQueue
{
    private const int MaxAttempts = 50;
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(15);

    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    private readonly IDbContextFactory<WorkshopDbContext> _dbFactory;
    private readonly JmsUserSessionPool _sessions;
    private readonly JmsReadSession _reads;
    private readonly JmsStatus _status;
    private readonly ILogger<JmsWriteQueue> _logger;
    private readonly SemaphoreSlim _workSignal = new(0);

    public JmsWriteQueue(IDbContextFactory<WorkshopDbContext> dbFactory, JmsUserSessionPool sessions, JmsReadSession reads, JmsStatus status,
        ILogger<JmsWriteQueue> logger)
    {
        _logger = logger;
        _dbFactory = dbFactory;
        _sessions = sessions;
        _reads = reads;
        _status = status;
        _status.Changed += () => { if (_status.IsUp == true) Signal(); };
    }

    /// <summary>Fires with the username whenever that user's queue changes.</summary>
    public event Action<string>? Changed;

    // ---------------------------
    // Submitting
    // ---------------------------

    public async Task<JmsWriteResult> SubmitAsync(string username, JmsWrite write)
    {
        if (_status.IsUp == false || await HasPendingAsync(username))
            return await EnqueueAsync(username, write, needsCheck: false);

        var (client, _) = await _sessions.GetAsync(username);
        if (client is null)
            return await EnqueueAsync(username, write, needsCheck: false);

        HtmlResponse res;
        try
        {
            res = await write.SendAsync(client);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            _logger.LogWarning("Send failed, queueing: {Write} — {Error}", write.Describe(), ex.Message);
            return await EnqueueAsync(username, write, needsCheck: true);
        }

        if (res.Success) return new(JmsWriteStatus.Done, res);
        if (IsTransient(res)) return await EnqueueAsync(username, write, needsCheck: true);
        return new(JmsWriteStatus.Failed, res);
    }

    private async Task<JmsWriteResult> EnqueueAsync(string username, JmsWrite write, bool needsCheck)
    {
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            db.JmsOutbox.Add(new JmsOutboxItem
            {
                Username = username,
                WriteJson = JsonSerializer.Serialize(write, Json),
                Description = write.Describe(),
                State = JmsOutboxState.Pending,
                NeedsCheck = needsCheck,
                CreatedAtUtc = DateTime.UtcNow,
                NextAttemptAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        Changed?.Invoke(username);
        Signal();
        return new(JmsWriteStatus.Queued, null);
    }

    // ---------------------------
    // Viewing / managing (/jms-pending)
    // ---------------------------

    public async Task<List<JmsOutboxItem>> ListAsync(string username)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.JmsOutbox.AsNoTracking().Where(i => i.Username == username).OrderBy(i => i.Id).ToListAsync();
    }

    public async Task<(int Pending, int Failed)> CountAsync(string username)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var states = await db.JmsOutbox.Where(i => i.Username == username).Select(i => i.State).ToListAsync();
        return (states.Count(s => s == JmsOutboxState.Pending), states.Count(s => s == JmsOutboxState.Failed));
    }

    /// <summary>Puts a failed write back in line to be tried again now.</summary>
    public async Task RetryAsync(string username, int id)
    {
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            await db.JmsOutbox.Where(i => i.Id == id && i.Username == username).ExecuteUpdateAsync(s => s
                .SetProperty(i => i.State, JmsOutboxState.Pending)
                .SetProperty(i => i.Attempts, 0)
                .SetProperty(i => i.NextAttemptAtUtc, DateTime.UtcNow));
        }

        Changed?.Invoke(username);
        Signal();
    }

    public async Task DiscardAsync(string username, int id)
    {
        await using (var db = await _dbFactory.CreateDbContextAsync())
            await db.JmsOutbox.Where(i => i.Id == id && i.Username == username).ExecuteDeleteAsync();

        Changed?.Invoke(username);
    }

    private async Task<bool> HasPendingAsync(string username)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.JmsOutbox.AnyAsync(i => i.Username == username && i.State == JmsOutboxState.Pending);
    }

    // ---------------------------
    // Draining (JmsWriteQueueWorker)
    // ---------------------------

    /// <summary>Waits until there may be something to send (a new write, JMS coming back up),
    /// or the timeout passes.</summary>
    internal Task WaitForWorkAsync(TimeSpan timeout, CancellationToken ct) => _workSignal.WaitAsync(timeout, ct);

    /// <summary>Sends every write that's due, oldest first per user. A user's writes stop at
    /// the first one that can't be sent yet, so later ones never overtake it.</summary>
    internal async Task ProcessDueAsync(CancellationToken ct)
    {
        if (_status.IsUp == false) return;

        List<string> usernames;
        await using (var db = await _dbFactory.CreateDbContextAsync(ct))
            usernames = await db.JmsOutbox.Where(i => i.State == JmsOutboxState.Pending)
                .Select(i => i.Username).Distinct().ToListAsync(ct);

        foreach (var username in usernames)
        {
            List<JmsOutboxItem> items;
            await using (var db = await _dbFactory.CreateDbContextAsync(ct))
                items = await db.JmsOutbox.Where(i => i.Username == username && i.State == JmsOutboxState.Pending)
                    .OrderBy(i => i.Id).ToListAsync(ct);

            foreach (var item in items)
            {
                if (ct.IsCancellationRequested || item.NextAttemptAtUtc > DateTime.UtcNow) break;
                if (!await TrySendAsync(item)) break;
            }

            Changed?.Invoke(username);
        }
    }

    /// <summary>True if the item is finished with (sent, found already applied, or failed for
    /// good) and the user's next write can go.</summary>
    private async Task<bool> TrySendAsync(JmsOutboxItem item)
    {
        var write = JsonSerializer.Deserialize<JmsWrite>(item.WriteJson, Json)!;

        var (client, state) = await _sessions.GetAsync(item.Username);
        if (client is null)
        {
            await RescheduleAsync(item, state == JmsUserSessionState.PasswordNeeded
                ? "Your saved JMS password no longer works — re-enter it on /jms-reconnect."
                : "JMS can't be reached.", countAttempt: false);
            return false;
        }

        try
        {
            if (item.NeedsCheck)
            {
                var reader = (await _reads.GetClientAsync())!;
                if (await write.AlreadyAppliedAsync(reader, DateTime.UtcNow - item.CreatedAtUtc))
                {
                    _logger.LogInformation("Already in JMS, not sending again: {Write}", item.Description);
                    await DeleteAsync(item);
                    return true;
                }
            }

            // From here a send may reach JMS, so any retry has to check first — saved before
            // sending in case the app stops mid-send.
            if (!item.NeedsCheck)
            {
                item.NeedsCheck = true;
                await SaveAsync(item);
            }
            var res = await write.SendAsync(client);

            if (res.Success)
            {
                _logger.LogInformation("Sent: {Write}", item.Description);
                await DeleteAsync(item);
                return true;
            }

            if (!IsTransient(res))
            {
                await FailAsync(item, $"JMS refused it: HTTP {res.StatusCode} {res.ReasonPhrase}");
                return true;
            }

            await RescheduleAsync(item, $"JMS error: HTTP {res.StatusCode} {res.ReasonPhrase}", countAttempt: true);
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            await RescheduleAsync(item, ex.Message, countAttempt: true);
            return false;
        }
    }

    private async Task RescheduleAsync(JmsOutboxItem item, string error, bool countAttempt)
    {
        if (countAttempt) item.Attempts++;
        if (item.Attempts >= MaxAttempts)
        {
            await FailAsync(item, $"Gave up after {item.Attempts} attempts. Last error: {error}");
            return;
        }

        var backoff = TimeSpan.FromSeconds(30 * Math.Pow(2, Math.Min(item.Attempts, 10)));
        item.NextAttemptAtUtc = DateTime.UtcNow + (backoff < MaxBackoff ? backoff : MaxBackoff);
        item.LastError = error;
        await SaveAsync(item);
    }

    private Task FailAsync(JmsOutboxItem item, string error)
    {
        item.State = JmsOutboxState.Failed;
        item.LastError = error;
        return SaveAsync(item);
    }

    private async Task SaveAsync(JmsOutboxItem item)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        db.JmsOutbox.Update(item);
        await db.SaveChangesAsync();
    }

    private async Task DeleteAsync(JmsOutboxItem item)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await db.JmsOutbox.Where(i => i.Id == item.Id).ExecuteDeleteAsync();
    }

    private void Signal()
    {
        if (_workSignal.CurrentCount == 0) _workSignal.Release();
    }

    // No response at all, or a server-side error: JMS may be struggling rather than saying no.
    private static bool IsTransient(HtmlResponse res) => res.StatusCode == 0 || res.StatusCode >= 500;
}
