namespace WorkshopDashboard.Models;

// An FDA 510(k) clearance record — created once and referenced by number from any number of
// CustomsModel entries, since several models are often cleared under the same 510(k) (common
// in the reference customs spreadsheet this replaces). Keyed by Number. Stored in the database via CustomsInfoStore.
public class CustomsFda510k
{
    public string Number { get; set; } = "";
    public string ProductCode { get; set; } = "";
    public string RegulationNumber { get; set; } = "";
    public string PageUrl { get; set; } = "";
}
