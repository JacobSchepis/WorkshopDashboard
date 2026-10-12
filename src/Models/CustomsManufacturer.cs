namespace WorkshopDashboard.Models;

// A manufacturer for US customs paperwork purposes (DHL commercial invoice / customs info
// attachments) — created once here and referenced by name from any number of CustomsModel
// entries. Keyed by Name, same convention as Repairer. Stored in the database via CustomsInfoStore. Deliberately separate from JMS's own `company` table — the address/
// registration number here are the FDA-facing manufacturer details, not JMS's general
// business address for that company.
public class CustomsManufacturer
{
    public string Name { get; set; } = "";
    public string Address { get; set; } = "";
    public string RegistrationNumber { get; set; } = "";
    public string Country { get; set; } = "";

    // FDA's establishment registration & device listing lookup page for this registration
    // number (the "Manufacturer FDA Page" column in the reference customs spreadsheet).
    public string FdaEstablishmentPageUrl { get; set; } = "";
}
