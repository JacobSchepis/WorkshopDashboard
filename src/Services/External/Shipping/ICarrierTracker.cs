using WorkshopDashboard.Models;

namespace WorkshopDashboard.Services.External.Shipping;

/// <summary>
/// One implementation per carrier (TNT, JMLA). The <see cref="TrackingService"/>
/// coordinator detects the carrier and routes each lookup to the matching tracker.
/// </summary>
public interface ICarrierTracker
{
    /// <summary>Carrier key this tracker handles, e.g. "TNT" or "JMLA".</summary>
    string Carrier { get; }

    /// <summary>
    /// Called once at startup. Carriers that log in and bulk-cache (JMLA) do that
    /// work here; stateless carriers (TNT) simply no-op.
    /// </summary>
    Task PrimeAsync();

    /// <summary>Looks up a single tracking number for this carrier.</summary>
    Task<TrackingResult> GetTrackingAsync(string trackingNumber);
}
