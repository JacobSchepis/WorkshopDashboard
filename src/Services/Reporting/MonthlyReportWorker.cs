namespace WorkshopDashboard.Services.Reporting;

/// <summary>
/// Runs MonthlyReportService in the background: queues last month's packs on the 1st, then
/// builds whatever is due, one pack at a time. Wakes when something is queued or retried,
/// and every minute regardless (to notice the 1st and retries whose delay has passed).
/// </summary>
public class MonthlyReportWorker : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);

    private readonly MonthlyReportService _reports;
    private readonly ILogger<MonthlyReportWorker> _logger;

    public MonthlyReportWorker(MonthlyReportService reports, ILogger<MonthlyReportWorker> logger)
    {
        _reports = reports;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _reports.ResetInterruptedAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Resetting interrupted monthly reports failed");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _reports.ScheduleIfDueAsync(DateTime.Now);
                while (await _reports.ProcessNextAsync(stoppingToken)) { }
                await _reports.WaitForWorkAsync(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // shutting down
            }
            catch (Exception ex)
            {
                // Never let one bad pack stop the worker for good.
                _logger.LogError(ex, "Monthly report pass failed");
                await Task.Delay(PollInterval, stoppingToken).ContinueWith(_ => { });
            }
        }
    }
}
