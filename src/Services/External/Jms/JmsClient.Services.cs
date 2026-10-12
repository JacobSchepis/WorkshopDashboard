using WorkshopDashboard.Models;

namespace WorkshopDashboard.Services.External.Jms;

// ═══════════════════════════════════════════════════════════════════════════
//  JmsClient.Services.cs
//
//  Named, typed convenience methods for the handful of service_type_id values
//  this app actually posts against — sitting on top of AddJobServiceAsync
//  (see JmsClient.JobService.cs) rather than duplicating its form-building.
//
//  JMS's full service_type table (for reference — only the three marked ✓ are
//  used by this app today):
//      1  INSTALLATION           11 NOTE                     ✓
//      2  LABOUR                 13 TOLLS - EXPENSE
//      3  MISC                   14 PARKING - EXPENSE
//      4  PARTS                  15 ACCOMMODATION - EXPENSE
//      5  ROUTINE SERVICE        16 TRANSPORT - EXPENSE
//      6  SALES                  17 MEALS - EXPENSE
//      7  TRAVEL                 18 PHONE - EXPENSE
//      8  STANDARDISATION        19 TOOLS - EXPENSE
//      9  QUOTED                 ✓  20 PARTS - EXPENSE
//      10 FREIGHT                ✓
//
//  Before this file existed, every quote page (and JobNotesService) posted
//  everything — repair lines, freight, informational notes — as service_type
//  9 (QUOTED) by hand-building a multipart form via QuoteServiceItem/
//  CommonQuoteServices.BuildForm, bypassing AddJobServiceAsync entirely and
//  never touching FREIGHT/NOTE even though those existed for exactly this.
//  That's what these three methods fix — pick the right type, then post it
//  the one real way (AddJobServiceAsync).
// ═══════════════════════════════════════════════════════════════════════════

public sealed partial class JmsClient
{
    internal const int ServiceTypeQuoted = 9;
    internal const int ServiceTypeFreight = 10;
    internal const int ServiceTypeNote = 11;

    // The form's user_id field on every service this app posts has always been this fixed
    // user id — carried over unchanged from the old CommonQuoteServices.CreateServiceForm.
    // The request itself runs under the logged-in user's own JMS session (JmsUserSession);
    // filling this field from CurrentUserResolver instead would be a separate change.
    private const int DefaultServiceUserId = 189;

    /// <summary>A priced repair/quote line (service_type 9, QUOTED) — the actual chargeable work.</summary>
    public Task<HtmlResponse> AddQuoteLineServiceAsync(int jobId, string description, decimal costPrice, string equipmentId = "", CancellationToken ct = default) =>
        AddJobServiceAsync(jobId, BuildPricedRequest(ServiceTypeQuoted, description, costPrice, equipmentId), ct);

    /// <summary>A freight/handling charge (service_type 10, FREIGHT).</summary>
    public Task<HtmlResponse> AddFreightServiceAsync(int jobId, string description, decimal costPrice, string equipmentId = "", CancellationToken ct = default) =>
        AddJobServiceAsync(jobId, BuildPricedRequest(ServiceTypeFreight, description, costPrice, equipmentId), ct);

    /// <summary>An informational, non-chargeable line — technical observations, work order
    /// notes, tagged notes (JobNotesService), quote validity disclaimers, etc.
    /// (service_type 11, NOTE, $0).</summary>
    public Task<HtmlResponse> AddNoteServiceAsync(int jobId, string description, string equipmentId = "", CancellationToken ct = default) =>
        AddJobServiceAsync(jobId, BuildPricedRequest(ServiceTypeNote, description, 0m, equipmentId), ct);

    /// <summary>List/market/actual price and invoice amount all mirror cost price, and tax
    /// is the flat 10% GST component — the same shape CommonQuoteServices.CreateServiceForm
    /// used to build by hand for every service this app posts.</summary>
    internal static JobServiceRequest BuildPricedRequest(int serviceTypeId, string description, decimal costPrice, string equipmentId) => new()
    {
        ServiceTypeId = serviceTypeId,
        Description = description,
        UserId = DefaultServiceUserId,
        EquipmentId = equipmentId,
        CostPrice = costPrice,
        ListPrice = costPrice,
        MarketPrice = costPrice,
        ActualPrice = costPrice,
        InvoiceAmount = costPrice,
        TaxAmount = costPrice / 10m,
        Quantity = 1,
    };
}
