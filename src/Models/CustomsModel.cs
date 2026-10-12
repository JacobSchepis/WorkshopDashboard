namespace WorkshopDashboard.Models;

// One model's customs profile, keyed by JMS's model.model_id so it can be resolved
// automatically from equipment on a job — no need to match on model name text. References a
// CustomsManufacturer (by Name) and a CustomsFda510k (by Number) rather than duplicating their
// fields — both are created once and reused across every model that shares them. A flat,
// top-level list (not nested under manufacturer): CustomsInfoManager.razor lists every model
// with customs info configured, and lets you add a new one by searching JMS for the model,
// then attaching (or inline-creating) its manufacturer and 501(k). Stored in the database via CustomsInfoStore.
public class CustomsModel
{
    public int ModelId { get; set; }
    public string ModelLabel { get; set; } = "";
    public string ManufacturerName { get; set; } = "";
    public string Fda510kNumber { get; set; } = "";
    public string Type { get; set; } = "";
    public string HtsCode { get; set; } = "";

    // Customs declared value — a plain number (e.g. "150"), no currency symbol or wording, since
    // it's used both on the customs PDF (currency added at render time) and as the strict
    // numeric "Value" field DHL's item-upload CSV requires.
    public string DeclaredValue { get; set; } = "";

    // Shipping weight in kilograms, e.g. "2.5" — DHL's item-upload CSV requires a weight per
    // item and JMS doesn't track this anywhere.
    public string Weight { get; set; } = "";

    // FDA Device Listing number (the "LST" line in the reason-for-export statement). Left
    // blank when not yet known — CustomsInfoManager and the Repair Request page's Equipment
    // tab both flag models missing this so it doesn't get lost.
    public string DeviceListingNumber { get; set; } = "";
}
