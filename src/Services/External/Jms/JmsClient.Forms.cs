using WorkshopDashboard.Models;

namespace WorkshopDashboard.Services.External.Jms;

// JMS forms whose field set the calling page builds itself (recall, document upload,
// purchasing) — these only carry the payload to the right page.
public sealed partial class JmsClient
{
    public Task<HtmlResponse> CreateRecallAsync(HttpContent content, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, E.CreateRecall, content, ct);

    public Task<HtmlResponse> UploadJobResourceAsync(int jobId, HttpContent content, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"{E.JobResourceUpload}?id={jobId}", content, ct);

    // ---------------------------
    // Purchasing
    // ---------------------------

    public Task<HtmlResponse> GetNotYetReceivedItemsAsync(int vendorCompanyId, CancellationToken ct = default)
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["company_id"] = vendorCompanyId.ToString() });
        return SendAsync(HttpMethod.Post, E.NotYetReceivedItems, form, ct);
    }

    public async Task<string?> GetItemPricingJsonAsync(int masterItemId, CancellationToken ct = default)
    {
        var res = await SendAsync(HttpMethod.Get, $"{E.PartPricingSearch}?id={masterItemId}&table=item", null, ct);
        return res.Success && !string.IsNullOrWhiteSpace(res.Content) ? res.Content : null;
    }

    public Task<HtmlResponse> CreateDeliveryDocketAsync(IDictionary<string, string> form, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, E.CreateDeliveryDocket, new FormUrlEncodedContent(form), ct);

    public Task<HtmlResponse> AddJobPartsAsync(int jobId, IDictionary<string, string> form, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"{E.AddJobParts}?id={jobId}", new FormUrlEncodedContent(form), ct);
}
