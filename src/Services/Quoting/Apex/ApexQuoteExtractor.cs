using WorkshopDashboard.Services.External.OpenAi;
using WorkshopDashboard.Services.Quoting;

namespace WorkshopDashboard.Services.Quoting.Apex;

public class ApexQuoteExtractor : BaseQuoteExtractor
{
    public ApexQuoteExtractor(IJsonCompletionClient client) : base(client) { }

    protected override string SchemaName => "apex_quote";

    protected override string SystemPrompt => """
        You are an assistant that extracts and formats information from Apex endoscope repair work order quotes.
        Extract only what is explicitly present in the document — never invent or assume values.
        """;

    protected override string UserPrompt => """
        Extract the following from this Apex work order quote and return as JSON:

        - job_number: the PO / Reference# field (e.g. "140067")

        - technical_observations: a list of short declarative sentences describing defects and notes.
          Each sentence should be concise and factual — no filler phrases like "upon inspection" or "it was found that".
          Format: rewrite each defect as "[Location/component] [finding]."
          IMPORTANT: Group related defects on the same component into a single sentence rather than separate entries.
          GROUPING EXAMPLES:
            "Bending section — broken eyelets" + "Bending section — worn eyelets" → "Bending section has broken and worn eyelets."
            "Angulation system — out of specification" + "Angulation knob/lever — failed leak test" → "Angulation system out of specification, knob/lever failing leak test."
            "Forceps elevator control lever — leaking actuator o-ring" + "Forceps elevator position — incorrect location stopper(s)" → "Forceps elevator control lever leaking at actuator o-ring, position incorrect at stopper(s)."
          OTHER EXAMPLES:
            "Fluid contamination — Control Body" → "Fluid contamination identified in control body."
            "Insertion tube type — aftermarket" → "Insertion tube is aftermarket."
            "Bending section — poor arc when angulated" → "Bending section showing poor arc when angulated."
          Omit purely procedural notes like "bending rubber removed during evaluation" that add no useful diagnostic information.
          Also include each Check Sheet Note rewritten the same way (unless procedural).

        - repairs_required: a list of short past-tense sentences, one per repair line item.
          Include ALL line items (chargeable, courtesy, and included).
          No prices, no codes, no repair type, no status — just what is to be done.
          EXAMPLES:
            "Reseal control knob assembly"
            "Rebuild bending section"
            "Replace Olympus bending section TJF-Q180V"
            "Remove fluid contamination from video scope"
            "Replace forceps elevator o ring"

        - estimated_completion_days: the estimated completion time in working days as an integer
          (e.g. if the notes say "05-07 Working days" extract the upper number and add 4, so 11)

        - total_price: the Total value as an integer (whole dollars, rounded)
        """;

    protected override string JsonSchema => """
        {
            "type": "object",
            "properties": {
                "job_number":                 { "type": "string" },
                "technical_observations":     { "type": "array", "items": { "type": "string" } },
                "repairs_required":           { "type": "array", "items": { "type": "string" } },
                "estimated_completion_days":  { "type": "integer" },
                "total_price":                { "type": "integer" }
            },
            "required": ["job_number", "technical_observations", "repairs_required", "estimated_completion_days", "total_price"],
            "additionalProperties": false
        }
        """;

    public Task<ApexQuoteExtraction> ExtractAsync(byte[] pdfBytes)
        => ExtractAsync<ApexQuoteExtraction>(pdfBytes);
}

public class ApexQuoteExtraction
{
    public string JobNumber { get; set; } = string.Empty;
    public List<string> TechnicalObservations { get; set; } = new();
    public List<string> RepairsRequired { get; set; } = new();
    public int EstimatedCompletionDays { get; set; }
    public int TotalPrice { get; set; }
}
