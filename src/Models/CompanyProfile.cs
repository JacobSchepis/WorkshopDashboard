namespace WorkshopDashboard.Models;

// The business this app runs for — printed on generated PDFs (repair request forms, customs
// declarations) and used as the default sender on outgoing email. One place to rebrand.
public static class CompanyProfile
{
    public const string Name = "Workshop Dashboard Demo";
    public const string LegalName = "Workshop Dashboard Demo Pty Ltd";
    public const string Department = "Scopes Department";

    /// <summary>Return address printed on repair request forms.</summary>
    public const string Address = "1 Example Street\nSydney, NSW 2000\nAustralia";
    public const string Phone = "02 9000 0000";

    /// <summary>Where the workshop's own mail goes, and the default sender for customer email.</summary>
    public const string WorkshopEmail = "workshop@example.com";
    /// <summary>Where repairers are asked to send their quotes.</summary>
    public const string QuotesEmail = "quotes@example.com";

    /// <summary>The suburb outbound consignments leave from — TNT tracking picks our consignment by it.</summary>
    public const string DepotSuburb = "Sydney";
}
