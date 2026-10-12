using WorkshopDashboard.Models;

namespace WorkshopDashboard.Services.External.Jms;

/// <summary>
/// Fields for CreateJob's own form. Owner/bill-to are (name, id) pairs because the live
/// page posts both the autocomplete's display text and the hidden id it resolved to — JMS
/// doesn't appear to validate the name against the id, but we send the real name anyway to
/// match what a human filling the form would submit.
///
/// ShipTo/ShippingAddress/BillingAddress/StandardLabourRate/LabourBasis/TravelBasis/PartsBasis
/// are all things the live page's JS auto-fills the moment you pick the owner from its
/// autocomplete (see JmsQueries.OwnerJobDefaultsQuery) — nothing here guesses at them.
/// </summary>
public class CreateJobRequest
{
    public required int OwnerCompanyId { get; init; }
    public required string OwnerCompanyName { get; init; }
    public required int BillToCompanyId { get; init; }
    public required string BillToCompanyName { get; init; }
    public decimal? StandardLabourRate { get; init; }

    public required int OwnerContactId { get; init; }
    public required string OwnerContactName { get; init; }
    public int? OwnerDepartmentId { get; init; }
    public required int BillContactId { get; init; }
    public required string BillContactName { get; init; }
    public int? BillDepartmentId { get; init; }

    public string ShipTo { get; init; } = "";
    public string ShippingAddress { get; init; } = "";
    public string BillingAddress { get; init; } = "";

    public required int BusinessTypeId { get; init; }
    public required int UserId { get; init; }
    public required int ContactUserId { get; init; }
    public required int JobPriorityId { get; init; }
    public int LabourBasis { get; init; }
    public int TravelBasis { get; init; }
    public int PartsBasis { get; init; }

    public DateTime BookedDate { get; init; } = DateTime.Today;
    public DateTime ScheduleDate { get; init; } = DateTime.Today;
    public required DateTime RequiredDate { get; init; }
    public decimal LabourEstimate { get; init; }

    public string Instructions { get; init; } = "";
}

public sealed partial class JmsClient
{
    /// <summary>Creates a new job via CreateJob's own form (job_submit=Create). On success
    /// JMS redirects to JobSummary?id={jobId} — callers should read the new job id back off
    /// HtmlResponse.Url rather than parsing the response body.</summary>
    public async Task<HtmlResponse> CreateJobAsync(CreateJobRequest request, CancellationToken ct = default)
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["owner"] = request.OwnerCompanyName,
            ["company_id"] = request.OwnerCompanyId.ToString(),
            ["bill_to"] = request.BillToCompanyName,
            ["bill_to_id"] = request.BillToCompanyId.ToString(),
            ["standard_labour_rate"] = request.StandardLabourRate?.ToString("0.00") ?? "",

            ["owner_contact"] = request.OwnerContactName,
            ["owner_contact_id"] = request.OwnerContactId.ToString(),
            ["department"] = request.OwnerDepartmentId?.ToString() ?? "",

            ["bill_contact"] = request.BillContactName,
            ["bill_contact_id"] = request.BillContactId.ToString(),
            ["bill_department"] = request.BillDepartmentId?.ToString() ?? "",

            ["ship_to"] = request.ShipTo,
            ["ship_to_id"] = request.OwnerCompanyId.ToString(),
            ["order_no"] = "",
            ["shipping_address"] = request.ShippingAddress,
            ["billing_address"] = request.BillingAddress,

            ["business_type"] = request.BusinessTypeId.ToString(),
            ["user_id"] = request.UserId.ToString(),
            ["contact_user_id"] = request.ContactUserId.ToString(),

            ["instructions_and_fault_details"] = request.Instructions,
            ["job_priority_id"] = request.JobPriorityId.ToString(),
            ["labour_basis"] = request.LabourBasis.ToString(),
            ["travel_basis"] = request.TravelBasis.ToString(),
            ["parts_basis"] = request.PartsBasis.ToString(),

            ["booked_date"] = request.BookedDate.ToString("dd/MM/yyyy"),
            ["schedule_date"] = request.ScheduleDate.ToString("dd/MM/yyyy"),
            ["required_date"] = request.RequiredDate.ToString("dd/MM/yyyy"),
            // The real form just posts whatever text is in the box (e.g. "5", not "5.00") — the
            // labour_estimate_error message ("Please estimate the total hours...") appears to fire
            // on a strict server-side format check, so match that instead of forcing 2 decimals.
            ["labour_estimate"] = request.LabourEstimate.ToString("0.##"),

            ["job_submit"] = "Create"
        });

        return await SendAsync(HttpMethod.Post, E.CreateJob, form, ct);
    }
}
