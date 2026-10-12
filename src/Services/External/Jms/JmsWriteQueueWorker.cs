namespace WorkshopDashboard.Services.External.Jms;

/// <summary>
/// Drains JmsWriteQueue in the background: whenever a write is queued or JMS comes back up,
/// and every 20 seconds regardless (to pick up items whose backoff has passed).
/// </summary>
public class JmsWriteQueueWorker : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(20);

    private readonly JmsWriteQueue _queue;
    private readonly ILogger<JmsWriteQueueWorker> _logger;

    public JmsWriteQueueWorker(JmsWriteQueue queue, ILogger<JmsWriteQueueWorker> logger)
    {
        _queue = queue;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _queue.ProcessDueAsync(stoppingToken);
                await _queue.WaitForWorkAsync(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // shutting down
            }
            catch (Exception ex)
            {
                // Never let one bad item stop the worker for good.
                _logger.LogError(ex, "Write queue pass failed");
                await Task.Delay(PollInterval, stoppingToken).ContinueWith(_ => { });
            }
        }
    }
}
