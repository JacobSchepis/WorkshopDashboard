namespace WorkshopDashboard.Models;

// One row in an App_Data/analytics/events-*.ndjson file — a page view or a
// hand-instrumented click, written by AnalyticsService/AnalyticsWriter.
public class AnalyticsEvent
{
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string EventType { get; set; } = ""; // "PageView" or "Click"
    public string Name { get; set; } = ""; // route path for PageView, action label for Click
    public string? Username { get; set; }
    public string? Detail { get; set; }

    // Optional summable count for events like "Book Shipment" (4 clicks, 16
    // total equipment) — kept separate from Detail so free-text details that
    // happen to look numeric (job numbers, PO numbers) never get summed.
    public int? Quantity { get; set; }
}
