using Microsoft.Extensions.Options;
using WorkshopDashboard.Services.Stores;

namespace WorkshopDashboard.Services.External.Jms;

// ═══════════════════════════════════════════════════════════════════════════
//  JmsUptimeMonitorService
//
//  Pings JMS's configured base URL (JmsOptions.BaseUrl) every minute. No
//  login — a plain HTTP GET is a reachability check,
//  not a literal "is the database specifically up" query — good enough for
//  "is JMS usable right now", which is what actually matters day to day.
//
//  Records only state TRANSITIONS (up->down, down->up) via JmsUptimeStore,
//  not every ping — see JmsUptimeReport for turning that history into daily
//  downtime totals for the JMS Uptime page.
// ═══════════════════════════════════════════════════════════════════════════

public class JmsUptimeMonitorService : BackgroundService
{
    private static readonly TimeSpan PingInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(10);

    private readonly Uri _pingUrl;
    private readonly HttpClient _http;
    private readonly JmsUptimeStore _store;
    private readonly JmsStatus _status;
    private readonly ILogger<JmsUptimeMonitorService> _logger;

    private bool? _lastKnownUp;

    public JmsUptimeMonitorService(HttpClient http, JmsUptimeStore store, JmsStatus status, IOptions<JmsOptions> options,
        ILogger<JmsUptimeMonitorService> logger)
    {
        _logger = logger;
        _pingUrl = options.Value.BaseUri;
        _http = http;
        _store = store;
        _status = status;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var isUp = await PingAsync(stoppingToken);
            var now = DateTime.Now;
            _status.Report(isUp, now);

            if (_lastKnownUp != isUp)
            {
                if (isUp)
                {
                    await _store.EndOutageAsync(now);
                    _logger.LogInformation("JMS back up at {Time:yyyy-MM-dd HH:mm}", now);
                }
                else
                {
                    await _store.StartOutageAsync(now);
                    _logger.LogWarning("JMS went down at {Time:yyyy-MM-dd HH:mm}", now);
                }

                _lastKnownUp = isUp;
            }

            try
            {
                await Task.Delay(PingInterval, stoppingToken);
            }
            catch (TaskCanceledException)
            {
                // shutting down
            }
        }
    }

    private async Task<bool> PingAsync(CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(PingTimeout);

            using var response = await _http.GetAsync(_pingUrl, cts.Token);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}
