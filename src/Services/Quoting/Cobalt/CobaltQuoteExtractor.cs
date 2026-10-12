using WorkshopDashboard.Services.External.OpenAi;
using WorkshopDashboard.Services.Quoting;

namespace WorkshopDashboard.Services.Quoting.Cobalt;

public class CobaltQuoteExtractor : BaseQuoteExtractor
{
    public CobaltQuoteExtractor(IJsonCompletionClient client) : base(client) { }

    protected override string SchemaName => "cobalt_quote";

    protected override string SystemPrompt => """
        You are an assistant that extracts information from Cobalt, Inc. repair estimates.
        These are US-based repair estimates priced in USD, with one row per item in a table.
        Extract only what is explicitly present in the document — never invent or assume values.
        """;

    protected override string UserPrompt => """
        Extract the following from this Cobalt repair estimate and return as JSON:

        - job_number: the Purchase Order number (e.g. "140125")

        - line_items: one entry per row of the "Item Description / Qty. / Work Needed and Comments / Repair Cost" table.
          For each row, the "Work Needed and Comments" text is always laid out as what they're going to do FIRST,
          then a blank line, then the findings/defects that justify it. Split it into two separate arrays:
            - item_description: the item name only (e.g. "Medtronic M5 Micro Debrider w/Cord") — do NOT include the
              Model No., Serial No. or Lot No. lines, those are handled elsewhere.
            - quantity: the Qty. value as an integer (default 1 if blank).
            - repairs_required: the work-needed instructions (the portion ABOVE the blank line), one sentence/action
              per array entry, e.g. "Replace bearings, o-rings, seals, as needed.", "Replace nose ring.", "Replace motor.",
              "Clean, lubricate, function and safety test".
            - technical_observations: the findings/defects (the portion BELOW the blank line), split on "ⅼ" or "|"
              into separate array entries, e.g. "Corrosion", "Motor shorted", "Unit stuck on back seal".
            - repair_cost_usd: the Repair Cost value for that row, in USD, as a number (no $ sign).

        - total_price_usd: the "Total Estimate" amount at the bottom of the quote, in USD, as a number (no $ sign).
        """;

    protected override string JsonSchema => """
        {
            "type": "object",
            "properties": {
                "job_number": { "type": "string" },
                "line_items": {
                    "type": "array",
                    "items": {
                        "type": "object",
                        "properties": {
                            "item_description":       { "type": "string" },
                            "quantity":                { "type": "integer" },
                            "repairs_required":        { "type": "array", "items": { "type": "string" } },
                            "technical_observations":  { "type": "array", "items": { "type": "string" } },
                            "repair_cost_usd":         { "type": "number" }
                        },
                        "required": ["item_description", "quantity", "repairs_required", "technical_observations", "repair_cost_usd"],
                        "additionalProperties": false
                    }
                },
                "total_price_usd": { "type": "number" }
            },
            "required": ["job_number", "line_items", "total_price_usd"],
            "additionalProperties": false
        }
        """;

    public Task<CobaltQuoteExtraction> ExtractAsync(byte[] pdfBytes)
        => ExtractAsync<CobaltQuoteExtraction>(pdfBytes);
}

public class CobaltLineItem
{
    public string ItemDescription { get; set; } = string.Empty;
    public int Quantity { get; set; } = 1;
    public List<string> RepairsRequired { get; set; } = new();
    public List<string> TechnicalObservations { get; set; } = new();
    public decimal RepairCostUsd { get; set; }
}

public class CobaltQuoteExtraction
{
    public string JobNumber { get; set; } = string.Empty;
    public List<CobaltLineItem> LineItems { get; set; } = new();
    public decimal TotalPriceUsd { get; set; }

    /// <summary>Sum of the individual line item costs, for reconciling against TotalPriceUsd.</summary>
    public decimal LineItemUsdTotal => LineItems.Sum(i => i.RepairCostUsd);

    public decimal UsdVariance => Math.Round(LineItemUsdTotal - TotalPriceUsd, 2, MidpointRounding.AwayFromZero);

    public bool Reconciles => Math.Abs(UsdVariance) < 0.01m;
}
