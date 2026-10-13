using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.EntityFrameworkCore;
using UglyToad.PdfPig;
using WorkshopDashboard.Data;
using WorkshopDashboard.Services.Analytics;
using WorkshopDashboard.Services.Reporting;
using WorkshopDashboard.Services.Stores;
using WorkshopDashboard.Tests.Fakes;

namespace WorkshopDashboard.Tests.Reporting;

// The monthly site report packs: fetching the four JMS reports for a month, merging them,
// and the queue of per-site runs the background worker drains.
public sealed class MonthlyReportTests : IAsyncLifetime
{
    private const int Company = 77;
    private static readonly DateTime September = new(2026, 9, 1);

    private JmsHarness _h = null!;
    private MonthlyReportBuilder _builder = null!;
    private MonthlyReportService _reports = null!;
    private readonly string _contentRoot = Path.Combine(Path.GetTempPath(), "monthly-report-tests", Guid.NewGuid().ToString("N"));
    private AppDataDirectory AppData => new(Path.Combine(_contentRoot, "App_Data"));

    public async Task InitializeAsync()
    {
        _h = await JmsHarness.StartAsync();
        _builder = new MonthlyReportBuilder(_h.Reads) { PollInterval = TimeSpan.FromMilliseconds(50), MaxBackgroundWait = TimeSpan.FromSeconds(2) };
        _reports = new MonthlyReportService(_h.Db, _builder, new SettingsStore(_h.Db), _h.Status,
            new AnalyticsWriter(AppData), AppData,
            NullLogger<MonthlyReportService>.Instance);
    }

    public async Task DisposeAsync()
    {
        await _h.DisposeAsync();
        if (Directory.Exists(_contentRoot)) Directory.Delete(_contentRoot, recursive: true);
    }

    // ── Building one pack ────────────────────────────────────────────────────

    [Fact]
    public async Task Pack_IsTheFourReportsInOrder()
    {
        var pdf = await _builder.BuildAsync(Company, "Alpha Hospital", September);

        using var doc = PdfDocument.Open(pdf);
        Assert.Equal(new[] { "rs-schedule", "repair-summary", "job-progress", "rs-performance" },
            doc.GetPages().Select(p => p.Text.Trim()));
    }

    [Fact]
    public async Task Reports_AskForTheWholeMonth()
    {
        await _builder.BuildAsync(Company, "Alpha Hospital", September);

        Assert.Equal(new[]
        {
            "rs-schedule?cid=77",
            "repair-summary?company_id=77&date=2026-09-30",
            "rs-performance?start_date=2026-09-01&end_date=2026-09-30&company_id=77",
        }, _h.Jms.ReportRequests);

        var job = Assert.Single(_h.Jms.BackgroundJobs);
        Assert.Equal("report_job_progress_cli", job["job_type"]);
        Assert.Equal("77", job["company_id"]);
        Assert.Equal("Alpha Hospital", job["company_name"]);
        Assert.Equal("2026-09-01", job["start_date"]);
        Assert.Equal("2026-09-30", job["end_date"]);
    }

    [Fact]
    public async Task JobProgress_WaitsForItsBackgroundJobToFinish()
    {
        _h.Jms.BackgroundJobDelay = TimeSpan.FromMilliseconds(300);
        var steps = new List<string>();

        var pdf = await _builder.BuildAsync(Company, "Alpha Hospital", September, s => { steps.Add(s); return Task.CompletedTask; });

        using var doc = PdfDocument.Open(pdf);
        Assert.Equal("job-progress", doc.GetPage(3).Text.Trim());
        Assert.Contains(steps, s => s.StartsWith("Job Progress (3/4) — waiting for JMS"));
    }

    [Fact]
    public async Task JobProgress_IgnoresNotificationsFromBeforeItWasSubmitted()
    {
        // Last month's notice for the same site is already there; this run's never arrives.
        _h.Jms.AddNotification("PDF Complete",
            "Job Progress Report for Alpha Hospital has been completed, Please check your notification to download.",
            "bg-files/last-month.pdf");
        _h.Jms.BackgroundJobsNeverFinish = true;

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => _builder.BuildAsync(Company, "Alpha Hospital", September));

        Assert.StartsWith("Job Progress: JMS's background job didn't finish", ex.Message);
    }

    [Fact]
    public async Task JobProgress_IgnoresOtherSitesNotifications()
    {
        _h.Jms.BackgroundJobsNeverFinish = true;
        _ = Task.Delay(200).ContinueWith(_ => _h.Jms.AddNotification("PDF Complete",
            "Job Progress Report for Alpha Hospital North has been completed, Please check your notification to download.",
            "bg-files/other-site.pdf"));

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => _builder.BuildAsync(Company, "Alpha Hospital", September));

        Assert.StartsWith("Job Progress:", ex.Message);
    }

    [Theory]
    [InlineData(2026, 2, 28)]
    [InlineData(2028, 2, 29)]
    [InlineData(2026, 12, 31)]
    public void MonthRange_EndsOnTheMonthsLastDay(int year, int month, int lastDay)
    {
        var (first, last) = MonthlyReportBuilder.MonthRange(new DateTime(year, month, 15));

        Assert.Equal(new DateTime(year, month, 1), first);
        Assert.Equal(new DateTime(year, month, lastDay), last);
    }

    [Fact]
    public async Task ReportThatIsntAPdf_FailsNamingTheReport_AndWhatJmsSaid()
    {
        _h.Jms.BrokenReports.Add("rs-performance");

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => _builder.BuildAsync(Company, "Alpha Hospital", September));

        Assert.StartsWith("RS Performance:", ex.Message);
        Assert.Contains("No data for company", ex.Message);
        Assert.DoesNotContain("<", ex.Message);
    }

    [Fact]
    public async Task JmsDown_FailsAsUnavailable()
    {
        await _h.TakeJmsDownAsync();

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => _builder.BuildAsync(Company, "Alpha Hospital", September));

        Assert.Equal("RS Progress: JMS is unavailable.", ex.Message);
    }

    // ── Queueing ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task QueueMonth_AddsEnabledSites_OnceEach()
    {
        await _reports.AddSiteAsync(1, "Alpha Hospital");
        await _reports.AddSiteAsync(2, "Beta Hospital");
        await _reports.AddSiteAsync(3, "Gamma Hospital");
        await _reports.SetSiteEnabledAsync(3, false);

        Assert.Equal(2, await _reports.QueueMonthAsync(September));
        Assert.Equal(0, await _reports.QueueMonthAsync(September));

        var runs = await _reports.ListRunsAsync("2026-09");
        Assert.Equal(new[] { "Alpha Hospital", "Beta Hospital" }, runs.Select(r => r.CompanyName));
        Assert.All(runs, r => Assert.Equal(MonthlyReportState.Pending, r.State));
    }

    [Fact]
    public async Task Schedule_QueuesLastMonthAtMidnightOnThe1st_Once()
    {
        await _reports.AddSiteAsync(1, "Alpha Hospital");

        await _reports.ScheduleIfDueAsync(new DateTime(2026, 10, 1, 0, 0, 0));
        Assert.Equal(new[] { "2026-09" }, await _reports.ListMonthsAsync());

        await _reports.ScheduleIfDueAsync(new DateTime(2026, 10, 1, 0, 1, 0));
        Assert.Single(await _reports.ListRunsAsync("2026-09"));

        // A site added later in the month isn't swept into last month's batch automatically.
        await _reports.AddSiteAsync(2, "Beta Hospital");
        await _reports.ScheduleIfDueAsync(new DateTime(2026, 10, 14, 9, 0, 0));
        Assert.Single(await _reports.ListRunsAsync("2026-09"));
    }

    [Fact]
    public async Task Schedule_CatchesUp_WhenTheServerWasOffOnThe1st()
    {
        await _reports.AddSiteAsync(1, "Alpha Hospital");

        await _reports.ScheduleIfDueAsync(new DateTime(2026, 11, 3, 14, 0, 0));

        Assert.Equal(new[] { "2026-10" }, await _reports.ListMonthsAsync());
    }

    // ── Building queued packs ────────────────────────────────────────────────

    [Fact]
    public async Task Process_WritesThePackToDisk_AndMarksItDone()
    {
        await _reports.AddSiteAsync(Company, "Alpha / Beta Hospital");
        await _reports.QueueMonthAsync(September);

        Assert.True(await _reports.ProcessNextAsync(default));
        Assert.False(await _reports.ProcessNextAsync(default));

        var run = Assert.Single(await _reports.ListRunsAsync("2026-09"));
        Assert.Equal(MonthlyReportState.Done, run.State);
        var path = _reports.GetFilePath(run);
        Assert.NotNull(path);
        Assert.Equal("Alpha - Beta Hospital 2026-09 (77).pdf", Path.GetFileName(path));
        using var doc = PdfDocument.Open(await File.ReadAllBytesAsync(path!));
        Assert.Equal(4, doc.NumberOfPages);
    }

    [Fact]
    public async Task Process_Failure_RetriesLater_ThenGivesUp()
    {
        _h.Jms.BrokenReports.Add("rs-schedule");
        await _reports.AddSiteAsync(Company, "Alpha Hospital");
        await _reports.QueueMonthAsync(September);

        await _reports.ProcessNextAsync(default);
        var run = Assert.Single(await _reports.ListRunsAsync("2026-09"));
        Assert.Equal(MonthlyReportState.Pending, run.State);
        Assert.Equal(1, run.Attempts);
        Assert.True(run.NextAttemptAtUtc > DateTime.UtcNow.AddMinutes(20));
        Assert.StartsWith("RS Progress:", run.LastError);

        // Not due yet, so nothing to do.
        Assert.False(await _reports.ProcessNextAsync(default));

        for (var i = 1; i < MonthlyReportService.MaxAttempts; i++)
        {
            await MakeDueAsync();
            await _reports.ProcessNextAsync(default);
        }

        run = Assert.Single(await _reports.ListRunsAsync("2026-09"));
        Assert.Equal(MonthlyReportState.Failed, run.State);
        Assert.Equal(MonthlyReportService.MaxAttempts, run.Attempts);
        Assert.Null(_reports.GetFilePath(run));
    }

    [Fact]
    public async Task Retry_BuildsAFailedPackAgain_Straight_Away()
    {
        _h.Jms.BrokenReports.Add("rs-schedule");
        await _reports.AddSiteAsync(Company, "Alpha Hospital");
        await _reports.QueueMonthAsync(September);
        await _reports.ProcessNextAsync(default);

        _h.Jms.BrokenReports.Clear();
        var run = Assert.Single(await _reports.ListRunsAsync("2026-09"));
        await _reports.RetryAsync(run.Id);
        Assert.True(await _reports.ProcessNextAsync(default));

        run = Assert.Single(await _reports.ListRunsAsync("2026-09"));
        Assert.Equal(MonthlyReportState.Done, run.State);
        Assert.Null(run.LastError);
    }

    [Fact]
    public async Task BuildInterruptedByARestart_GoesBackInTheQueue()
    {
        await _reports.AddSiteAsync(Company, "Alpha Hospital");
        await _reports.QueueMonthAsync(September);
        using (var db = _h.Db.CreateDbContext())
            await db.MonthlyReportRuns.ExecuteUpdateAsync(s => s.SetProperty(r => r.State, MonthlyReportState.Running));

        await _reports.ResetInterruptedAsync();

        Assert.Equal(MonthlyReportState.Pending, Assert.Single(await _reports.ListRunsAsync("2026-09")).State);
    }

    // ── Missing files and deleting ───────────────────────────────────────────

    [Fact]
    public async Task FileDeletedByHand_ShowsAsMissing_AndRebuildMissingBringsItBack()
    {
        var run = await BuildOneAsync(Company, "Alpha Hospital");
        File.Delete(_reports.GetFilePath(run)!);
        run = Assert.Single(await _reports.ListRunsAsync("2026-09"));
        Assert.True(_reports.IsFileMissing(run));

        Assert.Equal(1, await _reports.RebuildMissingAsync("2026-09"));
        await _reports.ProcessNextAsync(default);

        run = Assert.Single(await _reports.ListRunsAsync("2026-09"));
        Assert.False(_reports.IsFileMissing(run));
        Assert.NotNull(_reports.GetFilePath(run));
    }

    [Fact]
    public async Task RebuildMissing_LeavesPacksThatStillHaveTheirFile()
    {
        await BuildOneAsync(Company, "Alpha Hospital");

        Assert.Equal(0, await _reports.RebuildMissingAsync("2026-09"));
        Assert.Equal(MonthlyReportState.Done, Assert.Single(await _reports.ListRunsAsync("2026-09")).State);
    }

    [Fact]
    public async Task DeleteRun_RemovesTheFileTheRowAndTheEmptyFolder()
    {
        var run = await BuildOneAsync(Company, "Alpha Hospital");
        var path = _reports.GetFilePath(run)!;

        Assert.True(await _reports.DeleteRunAsync(run.Id));

        Assert.Empty(await _reports.ListRunsAsync("2026-09"));
        Assert.False(File.Exists(path));
        Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
    }

    [Fact]
    public async Task DeleteMonth_RemovesEveryPackForThatMonthOnly()
    {
        await _reports.AddSiteAsync(1, "Alpha Hospital");
        await _reports.AddSiteAsync(2, "Beta Hospital");
        await _reports.QueueMonthAsync(September);
        await _reports.QueueMonthAsync(September.AddMonths(-1));
        while (await _reports.ProcessNextAsync(default)) { }
        var septemberFiles = (await _reports.ListRunsAsync("2026-09")).Select(r => _reports.GetFilePath(r)!).ToList();

        var (deleted, skipped) = await _reports.DeleteMonthAsync("2026-09");

        Assert.Equal((2, 0), (deleted, skipped));
        Assert.Empty(await _reports.ListRunsAsync("2026-09"));
        Assert.All(septemberFiles, f => Assert.False(File.Exists(f)));
        Assert.Equal(2, (await _reports.ListRunsAsync("2026-08")).Count(r => _reports.GetFilePath(r) != null));
    }

    [Fact]
    public async Task Delete_LeavesAPackThatsMidBuildAlone()
    {
        await _reports.AddSiteAsync(Company, "Alpha Hospital");
        await _reports.QueueMonthAsync(September);
        using (var db = _h.Db.CreateDbContext())
            await db.MonthlyReportRuns.ExecuteUpdateAsync(s => s.SetProperty(r => r.State, MonthlyReportState.Running));
        var run = Assert.Single(await _reports.ListRunsAsync("2026-09"));

        Assert.False(await _reports.DeleteRunAsync(run.Id));
        Assert.Equal((0, 1), await _reports.DeleteMonthAsync("2026-09"));
        Assert.Single(await _reports.ListRunsAsync("2026-09"));
    }

    [Fact]
    public async Task PackDeletedWhileItWasBuilding_DoesNotLeaveItsFileBehind()
    {
        _h.Jms.BackgroundJobDelay = TimeSpan.FromMilliseconds(300);
        await _reports.AddSiteAsync(Company, "Alpha Hospital");
        await _reports.QueueMonthAsync(September);
        var id = Assert.Single(await _reports.ListRunsAsync("2026-09")).Id;

        // Remove the row behind the worker's back mid-build (the page won't, but a race can).
        var building = _reports.ProcessNextAsync(default);
        await Task.Delay(100);
        using (var db = _h.Db.CreateDbContext())
            await db.MonthlyReportRuns.Where(r => r.Id == id).ExecuteDeleteAsync();
        await building;

        Assert.False(Directory.Exists(Path.Combine(_contentRoot, "App_Data", "monthly-reports", "2026-09")));
    }

    private async Task<MonthlyReportRun> BuildOneAsync(int companyId, string name)
    {
        await _reports.AddSiteAsync(companyId, name);
        await _reports.QueueMonthAsync(September);
        Assert.True(await _reports.ProcessNextAsync(default));
        var run = Assert.Single(await _reports.ListRunsAsync("2026-09"));
        Assert.Equal(MonthlyReportState.Done, run.State);
        return run;
    }

    // ── JMS outages ──────────────────────────────────────────────────────────

    [Fact]
    public async Task WhileJmsIsKnownToBeDown_NothingStarts_ThenItCarriesOnWhenBack()
    {
        await _reports.AddSiteAsync(Company, "Alpha Hospital");
        await _reports.QueueMonthAsync(September);

        _h.Status.Report(false, DateTime.Now);
        Assert.False(await _reports.ProcessNextAsync(default));
        var run = Assert.Single(await _reports.ListRunsAsync("2026-09"));
        Assert.Equal((MonthlyReportState.Pending, 0), (run.State, run.Attempts));
        Assert.Empty(_h.Jms.ReportRequests);

        _h.Status.Report(true, DateTime.Now);
        Assert.True(await _reports.ProcessNextAsync(default));
        Assert.Equal(MonthlyReportState.Done, Assert.Single(await _reports.ListRunsAsync("2026-09")).State);
    }

    [Fact]
    public async Task JmsComingBackUp_WakesTheWorker()
    {
        _h.Status.Report(false, DateTime.Now);
        var woken = _reports.WaitForWorkAsync(TimeSpan.FromSeconds(5), default);

        _h.Status.Report(true, DateTime.Now);

        Assert.True(await woken);
    }

    [Fact]
    public async Task OutageMidPack_NeverUsesUpAttempts_AndThePackFinishesOnceJmsIsBack()
    {
        await WarmUpSessionAsync();
        await _reports.AddSiteAsync(Company, "Alpha Hospital");
        await _reports.QueueMonthAsync(September);

        // Far more outages than MaxAttempts — none of them should count.
        _h.Jms.Unreachable = true;
        for (var i = 0; i < MonthlyReportService.MaxAttempts + 2; i++)
        {
            await MakeDueAsync();
            Assert.True(await _reports.ProcessNextAsync(default));
        }

        var run = Assert.Single(await _reports.ListRunsAsync("2026-09"));
        Assert.Equal((MonthlyReportState.Pending, 0), (run.State, run.Attempts));
        Assert.Contains("Waiting for JMS", run.LastError);

        _h.Jms.Unreachable = false;
        await MakeDueAsync();
        await _reports.ProcessNextAsync(default);

        run = Assert.Single(await _reports.ListRunsAsync("2026-09"));
        Assert.Equal(MonthlyReportState.Done, run.State);
        Assert.Equal(1, run.Attempts);
    }

    [Fact]
    public async Task RealFailure_StillUsesAnAttempt()
    {
        _h.Jms.BrokenReports.Add("rs-performance");
        await _reports.AddSiteAsync(Company, "Alpha Hospital");
        await _reports.QueueMonthAsync(September);

        await _reports.ProcessNextAsync(default);

        var run = Assert.Single(await _reports.ListRunsAsync("2026-09"));
        Assert.Equal(1, run.Attempts);
        Assert.DoesNotContain("Waiting for JMS", run.LastError);
    }

    [Fact]
    public async Task JobProgress_ShortOutageWhileWaiting_IsRiddenOut()
    {
        await WarmUpSessionAsync();
        _h.Jms.BackgroundJobDelay = TimeSpan.FromMilliseconds(400);
        var blipped = false;

        var pdf = await _builder.BuildAsync(Company, "Alpha Hospital", September, step =>
        {
            if (!blipped && step.Contains("waiting for JMS"))
            {
                blipped = true;
                _h.Jms.Unreachable = true;
                _ = Task.Delay(200).ContinueWith(_ => _h.Jms.Unreachable = false);
            }
            return Task.CompletedTask;
        });

        Assert.True(blipped);
        using var doc = PdfDocument.Open(pdf);
        Assert.Equal("job-progress", doc.GetPage(3).Text.Trim());
    }

    [Fact]
    public async Task JobProgress_JmsStillDownWhenTheWaitRunsOut_IsAnOutage()
    {
        await WarmUpSessionAsync();
        _builder.MaxBackgroundWait = TimeSpan.FromMilliseconds(300);
        _h.Jms.BackgroundJobsNeverFinish = true;

        await Assert.ThrowsAsync<JmsUnavailableException>(() =>
            _builder.BuildAsync(Company, "Alpha Hospital", September, step =>
            {
                if (step.Contains("waiting for JMS")) _h.Jms.Unreachable = true;
                return Task.CompletedTask;
            }));
    }

    [Fact]
    public async Task JobProgress_NeverFinishingWhileJmsIsUp_IsARealFailure()
    {
        _builder.MaxBackgroundWait = TimeSpan.FromMilliseconds(300);
        _h.Jms.BackgroundJobsNeverFinish = true;

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => _builder.BuildAsync(Company, "Alpha Hospital", September));

        Assert.IsNotType<JmsUnavailableException>(ex);
    }

    // Log the service account in while JMS is reachable, as it would be before an outage —
    // otherwise the read session backs off after its failed login and the test waits it out.
    private async Task WarmUpSessionAsync()
    {
        var jms = await _h.Reads.GetClientAsync();
        Assert.True((await jms!.PostQueryForCsvAsync("SELECT 1")).Success);
    }

    private async Task MakeDueAsync()
    {
        using var db = _h.Db.CreateDbContext();
        await db.MonthlyReportRuns.ExecuteUpdateAsync(s => s.SetProperty(r => r.NextAttemptAtUtc, DateTime.UtcNow.AddSeconds(-1)));
    }
}
