namespace WorkshopDashboard.Models;

public class TrackingResult
{
    public string? TrackingNumber { get; set; }
    public string Carrier { get; set; } = "Unknown";
    public string Status { get; set; } = "Unknown";
    public string? LastEvent { get; set; }
    public string? LastLocation { get; set; }
    public DateTime? LastEventTime { get; set; }

    /// <summary>Timestamp of the earliest known event in the shipment's history (e.g. booked/picked
    /// up) — used to show how long a non-delivered shipment has been in transit. Null when the
    /// carrier's response didn't include enough history to determine this.</summary>
    public DateTime? FirstEventTime { get; set; }

    public bool IsDelivered { get; set; }
    public string? ErrorMessage { get; set; }
}
