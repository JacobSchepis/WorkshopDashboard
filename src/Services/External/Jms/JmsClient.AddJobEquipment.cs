using WorkshopDashboard.Models;

namespace WorkshopDashboard.Services.External.Jms;

/// <summary>Fields for AddJobEquipment's "Add" form. `OwnerCompanyId` is posted as `cid` —
/// the live page's own equipment autocomplete scopes its search to this same id.</summary>
public class AddJobEquipmentRequest
{
    public required int JobId { get; init; }
    public required int OwnerCompanyId { get; init; }
    public required int EquipmentId { get; init; }
    public string SerialNo { get; init; } = "";
    public string BarcodeNo { get; init; } = "";
    public string Comment { get; init; } = "";
    public string Accessories { get; init; } = "";
    public int NoOfAccessories { get; init; } = 0;
}

public sealed partial class JmsClient
{
    /// <summary>Adds an equipment unit to a job via AddJobEquipment's own form. Equipment is
    /// resolved by the caller first (see JmsQueries.EquipmentSearchQuery) — this just posts the
    /// picked equipment_id plus its serial/barcode, the same shape a real submission uses.</summary>
    public async Task<HtmlResponse> AddJobEquipmentAsync(AddJobEquipmentRequest request, CancellationToken ct = default)
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["cid"] = request.OwnerCompanyId.ToString(),
            ["job_id"] = request.JobId.ToString(),
            ["barcode_no"] = request.BarcodeNo,
            ["equipment_id"] = "",
            ["equipment_id_add"] = request.EquipmentId.ToString(),
            ["serial_no"] = request.SerialNo,
            ["equipent_id_entry"] = "",
            ["equipment_comment"] = request.Comment,
            ["equipment_accessories"] = request.Accessories,
            ["no_of_accessories"] = request.NoOfAccessories.ToString(),
            ["submit"] = "Add"
        });

        return await SendAsync(HttpMethod.Post, $"{E.AddJobEquipment}?id={request.JobId}", form, ct);
    }
}
