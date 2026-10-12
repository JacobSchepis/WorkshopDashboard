using Microsoft.EntityFrameworkCore;
using WorkshopDashboard.Data;
using WorkshopDashboard.Models;
using WorkshopDashboard.Services.Analytics;
using WorkshopDashboard.Services.External.Jms;
using WorkshopDashboard.Services.Stores;

namespace WorkshopDashboard.Services.Reporting;

// ═══════════════════════════════════════════════════════════════════════════
//  MonthlyReportService
//
//  The monthly site report packs: which hospitals get one (MonthlyReportSites),
//  and one MonthlyReportRun row per site per month tracking its progress.
//  MonthlyReportWorker drives it — on the 1st of each month (from 6am) it queues
//  last month for every enabled site, then builds the packs one at a time.
//  A failed pack is retried automatically a couple of times, then left Failed
//  for someone to retry from the Monthly Reports page. Finished PDFs are written
//  to App_Data/monthly-reports/<month>/, so nothing big sits in memory or the DB.
//
//  A JMS outage isn't the pack's fault, so it doesn't use up an attempt: while
//  JmsStatus says JMS is down nothing starts, and a pack that hits an outage
//  goes back to waiting and starts over once JMS is back.
// ═══════════════════════════════════════════════════════════════════════════

public class MonthlyReportService
{
    /// <summary>Automatic tries before a pack is left Failed.</summary>
    public const int MaxAttempts = 3;

    /// <summary>Local time on the 1st after which last month's packs are queued.</summary>
    /// Midnight, so the packs are done before anyone's in and the JMS load is out of hours.
    public static readonly TimeSpan ScheduleTimeOfDay = TimeSpan.Zero;

    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(30);

    /// <summary>How soon a pack that hit an outage tries again if JmsStatus hasn't caught up
    /// yet (it's also woken as soon as JMS is reported back up).</summary>
    private static readonly TimeSpan OutageRecheck = TimeSpan.FromMinutes(1);
    private const string LastScheduledMonthKey = "MonthlyReports.LastScheduledMonth";

    private readonly IDbContextFactory<WorkshopDbContext> _db;
    private readonly MonthlyReportBuilder _builder;
    private readonly SettingsStore _settings;
    private readonly JmsStatus _status;
    private readonly AnalyticsWriter _analytics;
    private readonly ILogger<MonthlyReportService> _logger;
    private readonly string _dataDir;
    private readonly SemaphoreSlim _workSignal = new(0);

    public MonthlyReportService(
        IDbContextFactory<WorkshopDbContext> db, MonthlyReportBuilder builder, SettingsStore settings, JmsStatus status,
        AnalyticsWriter analytics, IWebHostEnvironment env, ILogger<MonthlyReportService> logger)
    {
        _db = db;
        _builder = builder;
        _settings = settings;
        _status = status;
        _analytics = analytics;
        _logger = logger;
        _status.Changed += () => { if (_status.IsUp == true) Notify(); };
        _dataDir = Path.Combine(env.ContentRootPath, "App_Data");
    }

    /// <summary>Raised whenever a run changes, so the status page can refresh.</summary>
    public event Action? Changed;

    // ── Sites ────────────────────────────────────────────────────────────────

    public async Task<List<MonthlyReportSite>> ListSitesAsync()
    {
        await using var db = await _db.CreateDbContextAsync();
        return await db.MonthlyReportSites.OrderBy(s => s.CompanyName).ToListAsync();
    }

    /// <summary>Adds the site, or re-enables it if it's already there.</summary>
    public async Task AddSiteAsync(int companyId, string companyName)
    {
        await using var db = await _db.CreateDbContextAsync();
        var site = await db.MonthlyReportSites.FindAsync(companyId);
        if (site == null)
            db.MonthlyReportSites.Add(new MonthlyReportSite { CompanyId = companyId, CompanyName = companyName.Trim() });
        else
            site.Enabled = true;
        await db.SaveChangesAsync();
    }

    public async Task SetSiteEnabledAsync(int companyId, bool enabled)
    {
        await using var db = await _db.CreateDbContextAsync();
        var site = await db.MonthlyReportSites.FindAsync(companyId);
        if (site == null) return;
        site.Enabled = enabled;
        await db.SaveChangesAsync();
    }

    public async Task RemoveSiteAsync(int companyId)
    {
        await using var db = await _db.CreateDbContextAsync();
        await db.MonthlyReportSites.Where(s => s.CompanyId == companyId).ExecuteDeleteAsync();
    }

    // ── Runs ─────────────────────────────────────────────────────────────────

    public static string MonthKey(DateTime month) => month.ToString("yyyy-MM");

    /// <summary>The month whose packs are built on the 1st of <paramref name="today"/>'s month.</summary>
    public static DateTime PreviousMonth(DateTime today) => new DateTime(today.Year, today.Month, 1).AddMonths(-1);

    /// <summary>Every month that has runs, newest first.</summary>
    public async Task<List<string>> ListMonthsAsync()
    {
        await using var db = await _db.CreateDbContextAsync();
        return await db.MonthlyReportRuns.Select(r => r.Month).Distinct().OrderByDescending(m => m).ToListAsync();
    }

    public async Task<List<MonthlyReportRun>> ListRunsAsync(string month)
    {
        await using var db = await _db.CreateDbContextAsync();
        return await db.MonthlyReportRuns.Where(r => r.Month == month).OrderBy(r => r.CompanyName).ToListAsync();
    }

    public async Task<MonthlyReportRun?> GetRunAsync(int id)
    {
        await using var db = await _db.CreateDbContextAsync();
        return await db.MonthlyReportRuns.FindAsync(id);
    }

    /// <summary>Queues <paramref name="month"/> for every enabled site that doesn't have a run for
    /// it yet. Returns how many were added. Sites that already have one are left alone — use
    /// <see cref="RetryAsync"/> to rebuild those.</summary>
    public async Task<int> QueueMonthAsync(DateTime month)
    {
        var key = MonthKey(month);
        await using var db = await _db.CreateDbContextAsync();

        var existing = await db.MonthlyReportRuns.Where(r => r.Month == key).Select(r => r.CompanyId).ToListAsync();
        var sites = await db.MonthlyReportSites.Where(s => s.Enabled && !existing.Contains(s.CompanyId)).ToListAsync();

        var now = DateTime.UtcNow;
        db.MonthlyReportRuns.AddRange(sites.Select(s => new MonthlyReportRun
        {
            Month = key,
            CompanyId = s.CompanyId,
            CompanyName = s.CompanyName,
            State = MonthlyReportState.Pending,
            CreatedAtUtc = now,
            NextAttemptAtUtc = now,
        }));
        await db.SaveChangesAsync();

        if (sites.Count > 0) Notify();
        return sites.Count;
    }

    /// <summary>Builds the pack again from scratch, whatever state it's in (except mid-build).</summary>
    public async Task RetryAsync(int id)
    {
        await using var db = await _db.CreateDbContextAsync();
        var run = await db.MonthlyReportRuns.FindAsync(id);
        if (run == null || run.State == MonthlyReportState.Running) return;

        run.State = MonthlyReportState.Pending;
        run.Attempts = 0;
        run.LastError = null;
        run.NextAttemptAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();
        Notify();
    }

    /// <summary>Full path of a finished pack's PDF, or null if it isn't there.</summary>
    public string? GetFilePath(MonthlyReportRun run)
    {
        if (run.FilePath == null) return null;
        var path = Path.Combine(_dataDir, run.FilePath);
        return File.Exists(path) ? path : null;
    }

    /// <summary>A Done pack whose PDF has gone from disk (e.g. deleted by hand).</summary>
    public bool IsFileMissing(MonthlyReportRun run) => run.State == MonthlyReportState.Done && GetFilePath(run) == null;

    /// <summary>Queues every Done pack in <paramref name="month"/> whose file has gone. Returns how many.</summary>
    public async Task<int> RebuildMissingAsync(string month)
    {
        var missing = (await ListRunsAsync(month)).Where(IsFileMissing).Select(r => r.Id).ToList();
        foreach (var id in missing)
            await RetryAsync(id);
        return missing.Count;
    }

    /// <summary>Removes a pack — its file and its row. A pack mid-build is left alone.</summary>
    public async Task<bool> DeleteRunAsync(int id)
    {
        await using var db = await _db.CreateDbContextAsync();
        var run = await db.MonthlyReportRuns.FindAsync(id);
        if (run == null || run.State == MonthlyReportState.Running) return false;

        db.MonthlyReportRuns.Remove(run);
        await db.SaveChangesAsync();
        DeleteFile(run.FilePath);
        Changed?.Invoke();
        return true;
    }

    /// <summary>Removes every pack in <paramref name="month"/> except one mid-build. Returns how
    /// many were deleted and how many were skipped because they were building.</summary>
    public async Task<(int Deleted, int Skipped)> DeleteMonthAsync(string month)
    {
        await using var db = await _db.CreateDbContextAsync();
        var runs = await db.MonthlyReportRuns.Where(r => r.Month == month).ToListAsync();
        var deletable = runs.Where(r => r.State != MonthlyReportState.Running).ToList();

        db.MonthlyReportRuns.RemoveRange(deletable);
        await db.SaveChangesAsync();
        foreach (var run in deletable)
            DeleteFile(run.FilePath);

        if (deletable.Count > 0) Changed?.Invoke();
        return (deletable.Count, runs.Count - deletable.Count);
    }

    // ── Worker entry points ──────────────────────────────────────────────────

    /// <summary>Queues last month once it's the 1st (from <see cref="ScheduleTimeOfDay"/>) or later,
    /// once per month — so a server that was off on the 1st catches up when it's back.</summary>
    internal async Task ScheduleIfDueAsync(DateTime nowLocal)
    {
        var firstOfMonth = new DateTime(nowLocal.Year, nowLocal.Month, 1);
        if (nowLocal < firstOfMonth + ScheduleTimeOfDay) return;

        var month = MonthKey(PreviousMonth(nowLocal));
        if (await _settings.GetAsync(LastScheduledMonthKey) == month) return;

        await QueueMonthAsync(PreviousMonth(nowLocal));
        await _settings.SetAsync(LastScheduledMonthKey, month);
    }

    /// <summary>A build interrupted by a restart never finished — put it back in the queue.</summary>
    internal async Task ResetInterruptedAsync()
    {
        await using var db = await _db.CreateDbContextAsync();
        await db.MonthlyReportRuns.Where(r => r.State == MonthlyReportState.Running)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.State, MonthlyReportState.Pending).SetProperty(r => r.Step, (string?)null));
    }

    /// <summary>Builds the next due pack, if any. Returns false when there was nothing to do,
    /// or JMS is down (packs wait for it rather than failing one after another).</summary>
    internal async Task<bool> ProcessNextAsync(CancellationToken ct)
    {
        if (_status.IsUp == false) return false;

        MonthlyReportRun? run;
        await using (var db = await _db.CreateDbContextAsync(ct))
        {
            var now = DateTime.UtcNow;
            run = await db.MonthlyReportRuns
                .Where(r => r.State == MonthlyReportState.Pending && r.NextAttemptAtUtc <= now)
                .OrderBy(r => r.Id)
                .FirstOrDefaultAsync(ct);
            if (run == null) return false;

            run.State = MonthlyReportState.Running;
            run.Attempts++;
            run.LastError = null;
            await db.SaveChangesAsync(ct);
        }
        Notify();

        try
        {
            var month = DateTime.ParseExact(run.Month + "-01", "yyyy-MM-dd", null);
            var pdf = await _builder.BuildAsync(run.CompanyId, run.CompanyName, month, step => UpdateStepAsync(run.Id, step), ct);

            var relative = Path.Combine("monthly-reports", run.Month, FileNameFor(run));
            var full = Path.Combine(_dataDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            var temp = full + ".part";
            await File.WriteAllBytesAsync(temp, pdf, ct);
            File.Move(temp, full, overwrite: true);

            var kept = await FinishAsync(run.Id, r =>
            {
                r.State = MonthlyReportState.Done;
                r.FilePath = relative;
                r.FinishedAtUtc = DateTime.UtcNow;
            });

            // Deleted from the page while it was being built — don't leave the file behind.
            if (!kept) DeleteFile(relative);
            else await _analytics.AppendAsync(new AnalyticsEvent
            {
                EventType = UsageSummaries.Action, Name = "Monthly report pack built",
                Detail = $"{run.CompanyName} ({run.Month})", Username = UsageSummaries.AutomaticUser,
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // shutting down — ResetInterruptedAsync picks it up next start
        }
        catch (Exception ex)
        {
            var outage = IsOutage(ex);
            _logger.LogWarning("Monthly report for {Company} ({Month}) attempt {Attempt}{Outage} failed: {Error}",
                run.CompanyName, run.Month, run.Attempts, outage ? " (JMS outage, not counted)" : "", ex.Message);
            await FinishAsync(run.Id, r =>
            {
                if (outage)
                {
                    r.Attempts = Math.Max(0, r.Attempts - 1);
                    r.State = MonthlyReportState.Pending;
                    r.NextAttemptAtUtc = DateTime.UtcNow + OutageRecheck;
                    r.LastError = ex.Message + " Waiting for JMS to come back.";
                    return;
                }

                r.LastError = ex.Message;
                r.FinishedAtUtc = DateTime.UtcNow;
                if (r.Attempts >= MaxAttempts)
                {
                    r.State = MonthlyReportState.Failed;
                }
                else
                {
                    r.State = MonthlyReportState.Pending;
                    r.NextAttemptAtUtc = DateTime.UtcNow + RetryDelay;
                }
            });
        }
        return true;
    }

    internal Task<bool> WaitForWorkAsync(TimeSpan timeout, CancellationToken ct) => _workSignal.WaitAsync(timeout, ct);

    // Couldn't reach JMS, as opposed to JMS answering with something wrong.
    private bool IsOutage(Exception ex) =>
        ex is JmsUnavailableException
        || ex is HttpRequestException { StatusCode: null }
        || _status.IsUp == false;

    private async Task UpdateStepAsync(int id, string step)
    {
        await using var db = await _db.CreateDbContextAsync();
        await db.MonthlyReportRuns.Where(r => r.Id == id).ExecuteUpdateAsync(s => s.SetProperty(r => r.Step, step));
        Changed?.Invoke();
    }

    /// <summary>Applies <paramref name="update"/> to the run; false if it's been deleted meanwhile.</summary>
    private async Task<bool> FinishAsync(int id, Action<MonthlyReportRun> update)
    {
        await using var db = await _db.CreateDbContextAsync();
        var run = await db.MonthlyReportRuns.FindAsync(id);
        if (run == null) return false;
        run.Step = null;
        update(run);
        await db.SaveChangesAsync();
        Changed?.Invoke();
        return true;
    }

    // Removes a pack's file, and its month folder once that's empty.
    private void DeleteFile(string? relativePath)
    {
        if (relativePath == null) return;
        var full = Path.Combine(_dataDir, relativePath);
        try
        {
            if (File.Exists(full)) File.Delete(full);
            var dir = Path.GetDirectoryName(full)!;
            if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
        }
        catch (IOException ex)
        {
            _logger.LogWarning("Couldn't delete {Path}: {Error}", relativePath, ex.Message);
        }
    }

    private void Notify()
    {
        if (_workSignal.CurrentCount == 0) _workSignal.Release();
        Changed?.Invoke();
    }

    internal static string FileNameFor(MonthlyReportRun run)
    {
        var name = $"{run.CompanyName} {run.Month}";
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '-');
        return $"{name} ({run.CompanyId}).pdf";
    }
}
