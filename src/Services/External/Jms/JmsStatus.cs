namespace WorkshopDashboard.Services.External.Jms;

/// <summary>
/// Live "is JMS reachable right now" state, set by JmsUptimeMonitorService after each ping
/// and read by the nav bar's JmsStatusIndicator. Singleton; Changed fires only when the
/// state actually flips, so subscribers can update immediately instead of polling.
/// </summary>
public sealed class JmsStatus
{
    /// <summary>Null until the first ping completes.</summary>
    public bool? IsUp { get; private set; }

    /// <summary>When the current outage started; null while JMS is up.</summary>
    public DateTime? DownSince { get; private set; }

    public event Action? Changed;

    public void Report(bool isUp, DateTime at)
    {
        if (IsUp == isUp) return;

        IsUp = isUp;
        DownSince = isUp ? null : at;
        Changed?.Invoke();
    }
}
