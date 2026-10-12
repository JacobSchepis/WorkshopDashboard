using WorkshopDashboard.Abstract;
using WorkshopDashboard.Models;
using WorkshopDashboard.Services.External.Jms;
using WorkshopDashboard.Services.Jobs;
using WorkshopDashboard.Services.Workshop;
using WorkshopDashboard.Services.Admin;
using WorkshopDashboard.Services.Stores;

namespace WorkshopDashboard.Services.Quoting;

/// <summary>
/// The one class every quote page (Dorado/Apex/Bayside/Cobalt/Ember/Falcon) submits through. Each
/// page still builds its own list of repair line items from its own extractor/pricing output —
/// that part is genuinely different per repairer — and decides which admin-configured notices
/// the operator actually ticked for this quote (see QuoteNoticeStore.GetApplicableAsync, shown as
/// checkboxes on the page). This service posts the repair lines first, then the selected
/// notices sorted by their configured Order, since JMS prints a quote's lines in the order they
/// were created. Freight isn't a line of its own: the pages that charge it (Apex flex scopes,
/// Ember probes) fold it into their repair line's price and breakdown. There's no separate hardcoded "quote
/// validity" line any more — anything that used to be baked into a page (or this service) as universal boilerplate is now just an admin-configured
/// Global notice (QuoteNoticeStore), so it's editable and tick/untick-able like everything else
/// instead of always posting twice when an admin adds an equivalent notice. Posts through
/// JmsUserSession.WriteAsync with JmsWrite.AddQuoteLine (same request JmsClient.Services.cs builds). Everything posted
/// here lands as service_type QUOTED, the same type as the repair line itself; FREIGHT and NOTE
/// are for AddFreightServiceAsync callers (e.g. InternationalShipping.razor's Book Shipment
/// section) and JobNotesService respectively, not here.
/// </summary>
public class QuoteSubmissionService
{
    private readonly JmsUserSession _jmsSession;
    private readonly TimesheetLoggingService _timesheetLogging;

    public QuoteSubmissionService(JmsUserSession jmsSession, TimesheetLoggingService timesheetLogging)
    {
        _jmsSession = jmsSession;
        _timesheetLogging = timesheetLogging;
    }

    /// <summary>
    /// Posts <paramref name="lineItems"/> first, then <paramref name="selectedNotices"/> sorted by
    /// their configured Order.
    /// Throws if JMS refuses a line; while JMS is down the lines are queued instead (JmsWriteQueue).
    /// </summary>
    public async Task SubmitAsync(
        int jobId,
        string equipmentId,
        IEnumerable<(string Description, decimal Price)> lineItems,
        IEnumerable<QuoteNotice> selectedNotices,
        CancellationToken ct = default)
    {
        foreach (var (description, price) in lineItems)
            await PostLineAsync(jobId, description, price, equipmentId);

        foreach (var notice in selectedNotices.OrderBy(n => n.Order))
            await PostLineAsync(jobId, notice.Text, 0m, equipmentId);

        // The one place every quote page's submit funnels through, so this covers all six
        // repairer pages (Dorado/Apex/Bayside/Cobalt/Ember/Falcon) without touching each of them.
        await _timesheetLogging.LogForJobAsync(jobId, 5.0 / 60, "Quote services created", ct: ct);
    }

    /// <summary>Posts a single ad-hoc quote line — used by QuoteDorado.razor's "Apply Credit"
    /// picker, which posts one negative-price line against an existing service rather than
    /// a full SubmitAsync batch.</summary>
    public async Task<bool> PostAdjustmentAsync(int jobId, string description, decimal price, string equipmentId, CancellationToken ct = default) =>
        (await _jmsSession.WriteAsync(JmsWrite.AddQuoteLine(jobId, description, price, equipmentId))).Accepted;

    // Queued lines (JMS down) still count as posted — the queue sends each user's writes in
    // order, so the quote's lines land in JMS in the same order they were submitted.
    private async Task PostLineAsync(int jobId, string description, decimal price, string equipmentId)
    {
        var res = await _jmsSession.WriteAsync(JmsWrite.AddQuoteLine(jobId, description, price, equipmentId));
        if (!res.Accepted)
        {
            var response = res.ToResponse();
            throw new Exception($"Service failed: HTTP {response.StatusCode} ({response.ReasonPhrase})");
        }
    }
}
