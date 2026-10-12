using WorkshopDashboard.Services.External.OpenAi;
using WorkshopDashboard.Models;
using System.Text.RegularExpressions;
using WorkshopDashboard.Services.Quoting;

namespace WorkshopDashboard.Services.Quoting.Bayside;

public class BaysideQuoteExtractor : BaseQuoteExtractor
{
    public BaysideQuoteExtractor(IJsonCompletionClient client) : base(client) { }

    protected override string SchemaName => "bayside_quote";

    protected override string SystemPrompt => """
        You are an assistant that extracts information from Bayside
        job quotes. These are US-based estimates priced in USD, laid out as a table with one row per
        item (Type of Equipment / Make / Model / Serial # / Quote of Item), each followed by a free-text
        note line.
        Extract only what is explicitly present in the document — never invent or assume values.
        """;

    protected override string UserPrompt => """
        Extract the following from this Bayside job quote and return as JSON:

        - job_number: the PO# (e.g. "140163")

        - line_items: one entry per POPULATED equipment row (ignore blank rows with no equipment type and $0.00).
          For each row:
            - item_description: the "Type of Equipment" value only (e.g. "Camera head w/coupler", "Coupler") —
              do NOT include Make, Model or Serial #, those are handled elsewhere.
            - repairs_required: the portion of the note line describing what Bayside will DO to fix the item
              (e.g. "Replace the thread body and rebuild the coupler."), split into short entries. If the item
              is unrepairable or no repair action is described, return an empty array.
            - technical_observations: the portion of the note line describing the finding/fault/reason
              (e.g. "Windows hazy/scratched.", "Thread body threads stripped.", or for an unrepairable item,
              the full reason such as "Unrepairable by Bayside due to bad ccd/ribbon cable.", "Intermittent CCD/ribbon
              cable in head."), split into short entries.
            - cost_usd: the "Quote of Item" value for that row, in USD, as a number (no $ sign; use 0 if $0.00).

        - total_price_usd: the "TOTAL OF QUOTE" amount, in USD, as a number (no $ sign).
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
                            "repairs_required":        { "type": "array", "items": { "type": "string" } },
                            "technical_observations":  { "type": "array", "items": { "type": "string" } },
                            "cost_usd":                { "type": "number" }
                        },
                        "required": ["item_description", "repairs_required", "technical_observations", "cost_usd"],
                        "additionalProperties": false
                    }
                },
                "total_price_usd": { "type": "number" }
            },
            "required": ["job_number", "line_items", "total_price_usd"],
            "additionalProperties": false
        }
        """;

    public async Task<BaysideQuoteExtraction> ExtractAsync(byte[] pdfBytes)
    {
        var result = await ExtractAsync<BaysideQuoteExtraction>(pdfBytes);

        // Bayside's "Unrepairable by Bayside" wording reads oddly once it's on our own quote —
        // swap the attribution to us rather than the sub-repairer.
        foreach (var item in result.LineItems)
        {
            for (int i = 0; i < item.TechnicalObservations.Count; i++)
                item.TechnicalObservations[i] = ReplaceUnrepairableAttribution(item.TechnicalObservations[i]);
            for (int i = 0; i < item.RepairsRequired.Count; i++)
                item.RepairsRequired[i] = ReplaceUnrepairableAttribution(item.RepairsRequired[i]);
        }

        return result;
    }

    private static readonly Regex UnrepairableByBayside = new("Unrepairable by Bayside", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string ReplaceUnrepairableAttribution(string text) =>
        UnrepairableByBayside.Replace(text, $"Unrepairable by {CompanyProfile.Name}");
}

public class BaysideLineItem
{
    public string ItemDescription { get; set; } = string.Empty;
    public List<string> RepairsRequired { get; set; } = new();
    public List<string> TechnicalObservations { get; set; } = new();
    public decimal CostUsd { get; set; }
}

public class BaysideQuoteExtraction
{
    public string JobNumber { get; set; } = string.Empty;
    public List<BaysideLineItem> LineItems { get; set; } = new();
    public decimal TotalPriceUsd { get; set; }

    /// <summary>Sum of the individual line item costs, for reconciling against TotalPriceUsd.</summary>
    public decimal LineItemUsdTotal => LineItems.Sum(i => i.CostUsd);

    public decimal UsdVariance => Math.Round(LineItemUsdTotal - TotalPriceUsd, 2, MidpointRounding.AwayFromZero);

    public bool Reconciles => Math.Abs(UsdVariance) < 0.01m;
}
