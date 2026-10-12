using System.Net.Http.Headers;
using WorkshopDashboard.Models;

namespace WorkshopDashboard.Services.External.Jms;

/// <summary>
/// Fields for AddJobService's "Add Service" form. Every price/quantity field
/// defaults to 0 — callers set whatever a given service line actually needs, nothing
/// here guesses a price on your behalf.
/// </summary>
public class JobServiceRequest
{
    public required int ServiceTypeId { get; init; }
    public string Description { get; init; } = "";
    public required int UserId { get; init; }
    public string EquipmentId { get; init; } = "";
    public string StockNo { get; init; } = "";
    public decimal CostPrice { get; init; }
    public decimal ListPrice { get; init; }
    public decimal MarketPrice { get; init; }
    public decimal CustomerPrice { get; init; }
    public decimal Discount { get; init; }
    public decimal ActualPrice { get; init; }
    public int Quantity { get; init; } = 1;
    public decimal InvoiceAmount { get; init; }
    public decimal TaxAmount { get; init; }
    public bool ItemHide { get; init; }

    /// <summary>Defaults to today when left null.</summary>
    public DateTime? ServiceDate { get; init; }
}

/// <summary>
/// Fields for UpdateJobService's "Update Service" form — a different shape from
/// the add form: no equipment/stock/customer_price/file fields, but it wants a
/// "technician" display name plus a gst_include radio the add form doesn't have.
/// </summary>
public class JobServiceUpdateRequest
{
    public required int ServiceTypeId { get; init; }
    public string Description { get; init; } = "";
    public required int UserId { get; init; }
    public required string TechnicianName { get; init; }
    public decimal CostPrice { get; init; }
    public decimal ListPrice { get; init; }
    public decimal MarketPrice { get; init; }
    public decimal Discount { get; init; }
    public decimal ActualPrice { get; init; }
    public int Quantity { get; init; } = 1;
    public bool GstIncluded { get; init; }
    public decimal InvoiceAmount { get; init; }

    /// <summary>Left null to match this field's readonly/blank state on the real form.</summary>
    public decimal? TaxAmount { get; init; }
    public bool ItemHide { get; init; }

    /// <summary>Defaults to today when left null.</summary>
    public DateTime? ServiceDate { get; init; }
}

public sealed partial class JmsClient
{
    /// <summary>Creates a new service line on a job via AddJobService — the same
    /// form the site's own "Add Service" page submits.</summary>
    public async Task<HtmlResponse> AddJobServiceAsync(int jobId, JobServiceRequest request, CancellationToken ct = default)
    {
        using var mp = new MultipartFormDataContent();
        mp.Add(new StringContent((request.ServiceDate ?? DateTime.Now).ToString("yyyy-MM-dd")), "service_date");
        mp.Add(new StringContent(request.ServiceTypeId.ToString()), "service_type");
        mp.Add(new StringContent(request.EquipmentId), "equipment_id_add");
        mp.Add(new StringContent(request.UserId.ToString()), "user_id");
        mp.Add(new StringContent(request.StockNo), "stock_no");
        mp.Add(new StringContent(request.Description), "description");
        mp.Add(new StringContent(request.CostPrice.ToString("0.00")), "cost_price");
        mp.Add(new StringContent(request.ListPrice.ToString("0.00")), "list_price");
        mp.Add(new StringContent(request.MarketPrice.ToString("0.00")), "market_price");
        mp.Add(new StringContent(request.CustomerPrice.ToString("0.00")), "customer_price");
        mp.Add(new StringContent(request.Discount.ToString("0.00")), "discount");
        mp.Add(new StringContent(request.ActualPrice.ToString("0.00")), "actual_price");
        mp.Add(new StringContent(request.Quantity.ToString()), "quantity");
        mp.Add(new StringContent(request.InvoiceAmount.ToString("0.00")), "invoice_amount");

        // The form always posts a "resource" file part, even with nothing attached.
        var fileContent = new ByteArrayContent(Array.Empty<byte>());
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        fileContent.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
        {
            Name = "\"resource\"",
            FileName = "\"\""
        };
        mp.Add(fileContent);

        mp.Add(new StringContent(request.TaxAmount.ToString("0.00")), "tax_amount");
        mp.Add(new StringContent(request.ItemHide ? "t" : "f"), "item_hide");
        mp.Add(new StringContent("Add Service"), "submit");

        return await SendAsync(HttpMethod.Post, $"{E.AddJobService}?id={jobId}", mp, ct);
    }

    /// <summary>Updates an existing service line via UpdateJobService. Note this does
    /// NOT bump job.last_worked_date on JMS's side the way AddJobServiceAsync does — if
    /// that's what you're actually after, create-then-delete a throwaway service instead
    /// (see WaitingWorkshopProcessing.razor's TouchLastWorkedAsync).</summary>
    public async Task<HtmlResponse> UpdateJobServiceAsync(int jobId, int serviceId, JobServiceUpdateRequest request, CancellationToken ct = default)
    {
        using var mp = new MultipartFormDataContent();
        // This form's own date-pick widget renders dd-MM-yyyy, unlike the add form's yyyy-MM-dd.
        mp.Add(new StringContent((request.ServiceDate ?? DateTime.Now).ToString("dd-MM-yyyy")), "service_date");
        mp.Add(new StringContent(request.ServiceTypeId.ToString()), "service_type");
        mp.Add(new StringContent(request.TechnicianName), "technician");
        mp.Add(new StringContent(request.UserId.ToString()), "user_id");
        mp.Add(new StringContent(request.Description), "description");
        mp.Add(new StringContent(request.CostPrice.ToString("0.00")), "cost_price");
        mp.Add(new StringContent(request.ListPrice.ToString("0.00")), "list_price");
        mp.Add(new StringContent(request.MarketPrice.ToString("0.00")), "market_price");
        mp.Add(new StringContent(request.Discount.ToString("0.00")), "discount");
        mp.Add(new StringContent(request.ActualPrice.ToString("0.00")), "actual_price");
        mp.Add(new StringContent(request.Quantity.ToString()), "quantity");
        mp.Add(new StringContent(request.GstIncluded ? "t" : "f"), "gst_include");
        mp.Add(new StringContent(request.InvoiceAmount.ToString("0.00")), "invoice_amount");
        mp.Add(new StringContent(request.TaxAmount?.ToString("0.00") ?? ""), "tax_amount");
        mp.Add(new StringContent(request.ItemHide ? "t" : "f"), "item_hide");
        mp.Add(new StringContent("Update Service"), "submit");

        return await SendAsync(HttpMethod.Post, $"{E.UpdateJobService}?id={jobId}&s_id={serviceId}", mp, ct);
    }

    /// <summary>Soft-deletes a service line via AddJobService's own del= param.</summary>
    public async Task<HtmlResponse> DeleteJobServiceAsync(int jobId, int serviceId, CancellationToken ct = default) =>
        await SendAsync(HttpMethod.Get, $"{E.AddJobService}?id={jobId}&del={serviceId}", null, ct);
}
