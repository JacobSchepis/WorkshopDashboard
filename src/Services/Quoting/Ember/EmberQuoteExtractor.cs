using WorkshopDashboard.Services.External.OpenAi;
using WorkshopDashboard.Services.Quoting;

namespace WorkshopDashboard.Services.Quoting.Ember;

public class EmberQuoteExtractor : BaseQuoteExtractor
{
    public EmberQuoteExtractor(IJsonCompletionClient client) : base(client) { }

    protected override string SchemaName => "ember_quote";

    protected override string SystemPrompt => """
        You are an assistant that extracts information from Ember transducer
        evaluation and repair quotes. These are US-based estimates priced in USD, for a single
        transducer per quote.
        Extract only what is explicitly present in the document — never invent or assume values.
        """;

    protected override string UserPrompt => """
        Extract the following from this Ember quote and return as JSON:

        - job_number: the PO Number (e.g. "140133")

        - technical_observations: the "Results of Initial Evaluation" text, broken into an array of short,
          factual sentences/findings — one finding per array entry. Do not include the "Recommended Repairs"
          text here.

        - repairs_required: the "Recommended Repairs" text, broken into an array of short sentences describing
          what will be done — one action/step per array entry.

        - warranty_days: the number of days from the "Warranty" field (e.g. "90 Days" -> 90).

        - turnaround_estimate: the "Estimated Turnaround Time" text exactly as printed (e.g. "5-7 Business Days").

        - total_price_usd: the "Cost of Repair" amount, in USD, as a number (no $ sign).
        """;

    protected override string JsonSchema => """
        {
            "type": "object",
            "properties": {
                "job_number":                { "type": "string" },
                "technical_observations":    { "type": "array", "items": { "type": "string" } },
                "repairs_required":          { "type": "array", "items": { "type": "string" } },
                "warranty_days":             { "type": "integer" },
                "turnaround_estimate":       { "type": "string" },
                "total_price_usd":           { "type": "number" }
            },
            "required": ["job_number", "technical_observations", "repairs_required", "warranty_days", "turnaround_estimate", "total_price_usd"],
            "additionalProperties": false
        }
        """;

    public Task<EmberQuoteExtraction> ExtractAsync(byte[] pdfBytes)
        => ExtractAsync<EmberQuoteExtraction>(pdfBytes);
}

public class EmberQuoteExtraction
{
    public string JobNumber { get; set; } = string.Empty;
    public List<string> TechnicalObservations { get; set; } = new();
    public List<string> RepairsRequired { get; set; } = new();
    public int WarrantyDays { get; set; }
    public string TurnaroundEstimate { get; set; } = string.Empty;
    public decimal TotalPriceUsd { get; set; }
}
