namespace WorkshopDashboard.Services.Quoting.Dorado;

public static class DoradoServiceDescriptions
{
    private static readonly Dictionary<string, string> LevelDescriptions = new()
    {
        {
            "level 0", "Thorough diagnostic testing and external cleaning of scope. Polishing of sapphire window or tip lens (non-coated)."
        },
        {
            "level 1", "Thorough diagnostic testing and cleaning of scope. Polishing of sapphire window or tip lens (non-coated).\r\n"
        },
        {
            "level 2", "Thorough diagnostic testing and cleaning of scope. Straighten tubing as needed. Repair/Replace objective lens assembly. Replacement of up to one (1) rod lens as needed. Recalibrate optic lens spacing. Fiber-optic light connection rebonding and polishing. Polishing of sapphire window or tip lens (non-coated). Comprehensive leak and shock test."
        },
        {
            "level 3", "Complete rebuild of scope. Replace inner and outer tubing. Replace fibre-optic light bundle. Repair/Replace objective lens assembly. Recalibrate optic lens spacing. Replacement of one (1) rod lens as needed. Polishing of sapphire window or tip lens (non-coated). Comprehensive leak and shock test."
        },
        { "unrepairable",  "Scope assessed as beyond economical repair." },
    };

    /// <summary>
    /// True when we have canned wording for this level.
    /// </summary>
    public static bool HasLevelDescription(string? level) =>
        !string.IsNullOrWhiteSpace(level) && LevelDescriptions.ContainsKey(level.Trim().ToLower());

    /// <summary>
    /// Description for a standard (non-specialty) rigid scope service.
    /// An unknown level simply omits the level wording rather than throwing.
    /// </summary>
    public static string GetTechServiceDescription(string? level, IEnumerable<string> observations)
    {
        var observationText = string.Join("\n", observations);
        var text = "Technical Observation:\n" + observationText + "\n\nRepair/Service: ";

        if (!string.IsNullOrWhiteSpace(level)
            && LevelDescriptions.TryGetValue(level.Trim().ToLower(), out var levelText))
        {
            text += "\nRepair inclusive of: " + levelText;
        }

        return Australianise(text);
    }

    /// <summary>
    /// Description for a semi-rigid / no-level scope priced through Apex.
    /// Deliberately carries no price text: the customer-facing figure is our AUD Apex price, and
    /// the quote's own numbers are Apex's USD cost — printing them would expose our buy price.
    /// </summary>
    public static string GetSemiRigidDescription(IEnumerable<string> observations, string? repairDescription)
    {
        var observationText = string.Join("\n", observations);
        var text = "Technical Observation:\n" + observationText + "\n\nRepair/Service: ";

        if (!string.IsNullOrWhiteSpace(repairDescription))
            text += "\nRepair inclusive of: " + repairDescription.Trim();

        return Australianise(text);
    }

    /// <summary>
    /// Description for a specialty scope service that sits alongside a level repair.
    /// </summary>
    public static string GetSpecialtyScopeDescription(IEnumerable<string> observations, string specialtyScopeDescription, decimal specialtyScopePrice)
    {
        var observationText = string.Join("\n", observations);

        return Australianise("Technical Observation:\n"
            + observationText
            + "\nRepair inclusive of: " + specialtyScopeDescription.Trim()
            + "\nRepair/Service: @ $" + specialtyScopePrice.ToString("0.00"));
    }

    /// <summary>
    /// Description for the specialty scope surcharge line (separate service from the base level).
    /// </summary>
    public static string GetSpecialtySurchargeDescription(string specialtyScopeDescription, decimal price)
    {
        var description = string.IsNullOrWhiteSpace(specialtyScopeDescription) ? "" : specialtyScopeDescription.Trim() + "\n";
        return Australianise("Specialty Scope Additional Cost:\n"
            + description
            + "@ $" + price.ToString("0.00"));
    }

    /// <summary>
    /// Description for standard additional rod lenses ($100 each).
    /// </summary>
    public static string GetRodLensDescription(int quantity, decimal fullPrice, decimal discount)
    {
        var desc = "Additional Costs:\n"
            + "Replacement of additional rod / ocular lenses (x" + quantity + "): $" + fullPrice.ToString("0.00");

        if (discount > 0)
            desc += "\nAgreed Pricing Discount: -$" + discount.ToString("0.00");

        return desc;
    }

    /// <summary>
    /// Description for 4K rod lenses, priced at the unit price extracted from the quote.
    /// </summary>
    public static string GetRodLens4kDescription(int quantity, decimal unitPrice, decimal fullPrice, decimal discount)
    {
        var desc = "Additional Costs:\n"
            + "Replacement of additional 4K rod / ocular lenses (x" + quantity + " @ $" + unitPrice.ToString("0.00") + " each): $" + fullPrice.ToString("0.00");

        if (discount > 0)
            desc += "\nAgreed Pricing Discount: -$" + discount.ToString("0.00");

        return desc;
    }

    private static readonly Dictionary<string, string> AmericanToAustralian = new()
    {
        { "fibers", "fibres" },
        { "Fibers", "Fibres" },
        { "fiber", "fibre" },
        { "Fiber", "Fibre" },
        { "analyze", "analyse" },
        { "Analyze", "Analyse" },
        { "center", "centre" },
        { "Center", "Centre" },
        { "color", "colour" },
        { "Color", "Colour" },
    };

    private static string Australianise(string text)
    {
        foreach (var (american, australian) in AmericanToAustralian)
            text = text.Replace(american, australian);
        return text;
    }
}
