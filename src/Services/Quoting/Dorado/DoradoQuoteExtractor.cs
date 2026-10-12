using WorkshopDashboard.Services.External.OpenAi;
using WorkshopDashboard.Services.Quoting;

namespace WorkshopDashboard.Services.Quoting.Dorado;

public class DoradoQuoteExtractor : BaseQuoteExtractor
{
    public DoradoQuoteExtractor(IJsonCompletionClient client) : base(client) { }

    protected override string SchemaName => "dorado_quote";

    protected override string SystemPrompt => """
        You are an assistant that extracts information from Dorado endoscope repair quotes.
        These quotes have a consistent structure with specific line items.
        Extract only what is explicitly present in the document — never invent or assume values.
        """;

    protected override string UserPrompt => """
    Extract the following from this Dorado quote and return as JSON:

    - job_number: the P.O. number (6-digit number in the P.O. No. column, e.g. 140032)
    - technical_observations: the list of observations from the "Tech observations" row in the line items table (split each observation into its own array entry)
    - works_required: the level name from the level line item, exactly one of "Level 1", "Level 2", "Level 3" or "Unrepairable" — not the description text.
      IMPORTANT: many semi-rigid and specialty scope quotes have NO level line item at all. If there is no level line item, return an empty string. Never guess or infer a level from the description or the price.
    - is_specialty_scope: true if there is a "Specialty Scopes" line item with a price greater than 0, otherwise false
    - specialty_scope_price: the price from the "Specialty Scopes" line item (excl GST) if present, otherwise 0
    - specialty_scope_description: the full description text from the "Specialty Scopes" line item if present, otherwise empty string
    - additional_rod_lenses: the quantity from the "Additional Rod lens" line item (standard, $100 each) if present, otherwise 0
    - additional_rod_lenses_4k: the quantity from the "Additional Rod lens 4K" (or similar 4K/HD variant) line item if present, otherwise 0
    - rod_lenses_4k_unit_price: the per-unit price of the 4K rod lens line item if present, otherwise 0
    - total_price: the Total amount at the bottom of the quote
    - currency: the currency (default USD if not specified)
    """;

    protected override string JsonSchema => """
        {
            "type": "object",
            "properties": {
                "job_number":                    { "type": "string" },
                "technical_observations":        { "type": "array", "items": { "type": "string" } },
                "works_required":                { "type": "string", "enum": ["Level 1", "Level 2", "Level 3", "Unrepairable", ""] },
                "is_specialty_scope":            { "type": "boolean" },
                "specialty_scope_price":         { "type": "number" },
                "specialty_scope_description":   { "type": "string" },
                "additional_rod_lenses":         { "type": "integer" },
                "additional_rod_lenses_4k":      { "type": "integer" },
                "rod_lenses_4k_unit_price":      { "type": "number" },
                "total_price":                   { "type": "number" },
                "currency":                      { "type": "string" }
            },
            "required": [
                "job_number",
                "technical_observations",
                "works_required",
                "is_specialty_scope",
                "specialty_scope_price",
                "specialty_scope_description",
                "additional_rod_lenses",
                "additional_rod_lenses_4k",
                "rod_lenses_4k_unit_price",
                "total_price",
                "currency"
            ],
            "additionalProperties": false
        }
        """;

    public Task<DoradoQuoteExtraction> ExtractAsync(byte[] pdfBytes)
        => ExtractAsync<DoradoQuoteExtraction>(pdfBytes);
}

public class DoradoQuoteExtraction
{
    public string JobNumber { get; set; } = string.Empty;
    public List<string> TechnicalObservations { get; set; } = new();

    /// <summary>Canonical level name, or empty string when the quote has no level repair.</summary>
    public string WorksRequired { get; set; } = string.Empty;

    public bool IsSpecialtyScope { get; set; }
    public decimal SpecialtyScopePrice { get; set; }
    public string SpecialtyScopeDescription { get; set; } = string.Empty;
    public int AdditionalRodLenses { get; set; }
    public int AdditionalRodLenses4k { get; set; }
    public decimal RodLenses4kUnitPrice { get; set; }
    public decimal TotalPrice { get; set; }
    public string Currency { get; set; } = "USD";

    public bool HasLevel => DoradoLevels.IsKnown(WorksRequired);
}
