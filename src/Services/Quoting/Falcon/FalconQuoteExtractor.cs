using WorkshopDashboard.Services.External.OpenAi;
using System.Text.Json.Serialization;
using WorkshopDashboard.Services.Quoting;

namespace WorkshopDashboard.Services.Quoting.Falcon;

public class FalconQuoteExtractor : BaseQuoteExtractor
{
    public FalconQuoteExtractor(IJsonCompletionClient client) : base(client) { }

    protected override string SchemaName => "falcon_quote";

    protected override string SystemPrompt => """
        You are a parser for Falcon Surgical repair quotes. You classify the lines of
        each quote row into structured fields. You never invent, infer or tidy values —
        you only assign text that is physically printed in the document to a field.
        Formatting and arithmetic are done downstream; your only job is correct
        transcription and classification.
        """;

    protected override string UserPrompt => """
    Parse this Falcon Surgical repair quote and return JSON.

    === HEADER FIELDS ===
    - job_number:   the "Your Reference" field (6-digit job number, e.g. 140114)
    - quote_number: the "Quote No" field (e.g. 10234)
    - currency:     AUD unless the document says otherwise
    - subtotal:     the Subtotal figure printed in the quote's footer box, before GST.

    ABOUT subtotal — READ THIS CAREFULLY:
    Copy the printed Subtotal blind, exactly as it appears. Do NOT compute it. Do NOT
    check it against the line items you extracted. Do NOT adjust, round or "correct"
    it if it disagrees with them. Downstream code sums the line items and compares the
    result to this figure to detect whether any row was missed or duplicated. That
    check only works if you transcribe the printed value without reference to your own
    line items. A subtotal that disagrees with the line items is a useful signal, not
    an error to fix.

    Do NOT extract the GST or Order Total figures at all — they are derived downstream
    from the line items.

    === MULTI-PAGE LAYOUT ===
    The quote runs over several pages, marked "Page N of M" and "Continued over .../".
    Extract line items from EVERY page — the table continues across the page break.
    The Subtotal / GST / Order Total footer box is the whole-quote total and is
    REPRINTED IDENTICALLY on every page. It is not a per-page total. Read it once;
    never add the pages' footers together.

    === THE CODE COLUMN IS NOT DATA ===
    The leftmost "Code" column holds Falcon internal repair codes ("7-039-01",
    "7-061-94", "500-003-A"). Never extract one. It is never a model, serial,
    catalogue or part number — not even when the same code is echoed inside the
    Description text as a cross-reference.

    === DESCRIPTION CELL GRAMMAR (what you are parsing) ===
    Every row's Description cell is a stack of printed lines with this shape:

      line 1        <Instrument type> - <Repair action>
      lines 2..n-1  zero or more of: a plain-English name for the instrument,
                    one identifier line, dash-prefixed work notes
      last line     usually a bracketed condition, e.g. "(tips bent)"

    Assign every line of that stack to exactly one role below. Nothing is dropped
    silently and nothing is duplicated across roles.

    === ROLES ===

    source_description_lines
        Transcribe the Description cell verbatim first, one array element per
        printed line, no edits, no reordering. Emit this field BEFORE the others.
        Every other field must be derivable from it — it is the audit trail.

    equipment_type
        Line 1, everything BEFORE the repair action. Internal hyphens belong to the
        type — keep them: "Scissor - Micro(Spring) - POTTS", "Needle Holder - T'C's",
        "Forceps - Tissue", "Biopsy Punch / Kerrison".

    repair_action
        Line 1, the trailing segment describing the work to be done:
        "Sharpen, Tighten & Polish", "Realign & Polish", "Replace T/C's & Polish",
        "Service".
        If line 1 contains "Unserviceable", repair_action is "Unserviceable" plus
        the ONE segment immediately following it — e.g. "Unserviceable - Ring
        damaged at tip", "Unserviceable - Uneconomical to repair". Any further
        segments on line 1 after that become notes.

    identifier
        The ONE line naming the physical instrument: a brand/manufacturer and/or a
        catalogue, model, part or serial code. Examples of real identifier lines:
        "INKA 12168.12 LOT 916703", "KAISER'S GS1573", "AESCULAP BC323R",
        "VD-SCR-606", "Duckworth & Kent 2-2-716G-9", "Medtronic PN9730027",
        "bob instruments 0180.62", "WA Surgical 11237.20".
        Copy it verbatim, but strip a leading "N x " quantity prefix
        ("2 x Medtronic PN9730027" -> "Medtronic PN9730027").
        Set identifier to null when the cell says "No brand/marking", or when no
        line carries a code. Never invent one. Never fall back to the Code column.
        Never promote a purely descriptive line ("Suture Scissor", "Sickle knife",
        "Adsons", "Cvd Suture Fcp", "Spinal rel instr") into the identifier —
        those are notes.

    notes
        Every remaining description line, in the order printed, one array element
        each. Normalise each:
          - drop the leading "- " of dash-prefixed notes
          - drop the surrounding brackets: "(tips bent)" -> "tips bent"
          - otherwise keep the wording exactly as printed
        Omit entirely: "Surgical tool", "No brand/marking", and any line that
        merely restates the equipment_type (a bare "Needle Holder" line under a
        Needle Holder row). Empty array if nothing remains.

    === ONE LINE ITEM = ONE PHYSICAL INSTRUMENT ===
    A row with Qty > 1 covers that many physical units (e.g. Qty 2, identifier
    "2 x Medtronic PN9730027"). Emit that many line_items, each with quantity 1,
    each carrying the same unit_price and the same identifier. Only keep
    quantity > 1 if the row is genuinely one indivisible service.

    === PRICING ===
    - quantity:    1 for each expanded item.
    - unit_price:  the row's Unit Price column as printed. It is already per-unit,
                   so every expanded item from a grouped row takes that same value.
                   Never take the Extd Price column. 0 if blank.
    - is_zero_cost: true when unit_price is 0 or missing.

    Expanding a Qty 2 row into two quantity-1 items at the same unit_price
    preserves the row's Extd Price, so the sum over all line_items still equals
    the printed Subtotal. Getting this wrong is exactly what the checksum catches.

    === WORKED EXAMPLES ===

    Source cell (Qty 1, Unit Price 39.90):
        Forceps - Tissue - Realign & Polish
        Micro Adsons Toothed (no tooth found)
        INKA 12168.12 LOT 916703
        (tips bent)
    ->  equipment_type: "Forceps - Tissue"
        repair_action:  "Realign & Polish"
        identifier:     "INKA 12168.12 LOT 916703"
        notes:          ["Micro Adsons Toothed (no tooth found)", "tips bent"]
        quantity: 1, unit_price: 39.90, is_zero_cost: false

    Source cell (Qty 1, Unit Price 109.90):
        Needle Holder - T'C's - Replace T/C's & Polish
        Needle Holder
        No brand/marking
    ->  equipment_type: "Needle Holder - T'C's"
        repair_action:  "Replace T/C's & Polish"
        identifier:     null
        notes:          []
        quantity: 1, unit_price: 109.90, is_zero_cost: false

    Source cell (Qty 1, Unit Price 0.00):
        Needle Holder - T'C's - Unserviceable - Uneconomical to repair - Require
        grinding out T/C's which will reduce material for foot plate
        Neivert N.Holder
        bob instruments 0180.62
        (tip not gripping)
    ->  equipment_type: "Needle Holder - T'C's"
        repair_action:  "Unserviceable - Uneconomical to repair"
        identifier:     "bob instruments 0180.62"
        notes:          ["Require grinding out T/C's which will reduce material for foot plate",
                         "Neivert N.Holder", "tip not gripping"]
        quantity: 1, unit_price: 0, is_zero_cost: true

    Source cell (Qty 1, Unit Price 29.90):
        Scissor - Std T/C - Sharpen, Tighten & Polish
        VD-SCR-606
        (for sharpening)
    ->  equipment_type: "Scissor - Std T/C"
        repair_action:  "Sharpen, Tighten & Polish"
        identifier:     "VD-SCR-606"
        notes:          ["for sharpening"]
        quantity: 1, unit_price: 29.90, is_zero_cost: false

    Source cell (Qty 2, Unit Price 159.90, Extd Price 319.80) — expands to TWO items:
        Spinal Navigation Tool - Service
        Spinal rel instr
        2 x Medtronic PN9730027
         - Shortened 2 x Hex drivers to remove damage
         - Re-surface heads to remove damage
         - Best can do
         - Note: Job completed as part of assessment
    ->  equipment_type: "Spinal Navigation Tool"
        repair_action:  "Service"
        identifier:     "Medtronic PN9730027"
        notes:          ["Spinal rel instr", "Shortened 2 x Hex drivers to remove damage",
                         "Re-surface heads to remove damage", "Best can do",
                         "Note: Job completed as part of assessment"]
        quantity: 1, unit_price: 159.90, is_zero_cost: false
        (emitted twice — 2 x 159.90 = the printed 319.80)
    """;

    protected override string JsonSchema => """
        {
            "type": "object",
            "properties": {
                "job_number":   { "type": "string" },
                "quote_number": { "type": "string" },
                "line_items": {
                    "type": "array",
                    "items": {
                        "type": "object",
                        "properties": {
                            "source_description_lines": {
                                "type": "array",
                                "items": { "type": "string" }
                            },
                            "equipment_type": { "type": "string" },
                            "repair_action":  { "type": "string" },
                            "identifier":     { "type": ["string", "null"] },
                            "notes": {
                                "type": "array",
                                "items": { "type": "string" }
                            },
                            "quantity":     { "type": "integer" },
                            "unit_price":   { "type": "number" },
                            "is_zero_cost": { "type": "boolean" }
                        },
                        "required": [
                            "source_description_lines",
                            "equipment_type",
                            "repair_action",
                            "identifier",
                            "notes",
                            "quantity",
                            "unit_price",
                            "is_zero_cost"
                        ],
                        "additionalProperties": false
                    }
                },
                "subtotal": { "type": "number" },
                "currency": { "type": "string" }
            },
            "required": ["job_number", "quote_number", "line_items", "subtotal", "currency"],
            "additionalProperties": false
        }
        """;

    public Task<FalconQuoteExtraction> ExtractAsync(byte[] pdfBytes)
        => ExtractAsync<FalconQuoteExtraction>(pdfBytes);
}

public class FalconQuoteExtraction
{
    /// <summary>Australian GST. Change here if a quote is ever zero-rated.</summary>
    public const decimal GstRate = 0.10m;

    /// <summary>Tolerance for the reconciliation check, in dollars.</summary>
    public const decimal ReconciliationTolerance = 0.01m;

    public string JobNumber { get; set; } = string.Empty;
    public string QuoteNumber { get; set; } = string.Empty;
    public List<FalconLineItem> LineItems { get; set; } = new();
    public string Currency { get; set; } = "AUD";

    /// <summary>
    /// The Subtotal printed on the quote, transcribed blind by the extractor.
    /// This is a checksum only — never a source of truth for pricing.
    /// </summary>
    public decimal Subtotal { get; set; }

    // ── Derived from the line items, never extracted ──────────────────

    /// <summary>Sum of every line item's extended price. The real subtotal.</summary>
    [JsonIgnore]
    public decimal FalconLineTotal => LineItems.Sum(i => i.ExtendedPrice);

    [JsonIgnore]
    public decimal Gst => Math.Round(FalconLineTotal * GstRate, 2, MidpointRounding.AwayFromZero);

    [JsonIgnore]
    public decimal TotalPrice => FalconLineTotal + Gst;

    /// <summary>Positive = extracted more than the quote says. Negative = rows missed.</summary>
    [JsonIgnore]
    public decimal SubtotalVariance => FalconLineTotal - Subtotal;

    [JsonIgnore]
    public bool Reconciles => Math.Abs(SubtotalVariance) <= ReconciliationTolerance;

    /// <summary>Count of items the extractor couldn't find an identifier for.</summary>
    [JsonIgnore]
    public int MissingIdentifierCount =>
        LineItems.Count(i => string.IsNullOrWhiteSpace(i.Identifier));
}

public class FalconLineItem
{
    // ── Extracted atoms ──────────────────────────────────────────────
    public List<string> SourceDescriptionLines { get; set; } = new();
    public string EquipmentType { get; set; } = string.Empty;
    public string RepairAction { get; set; } = string.Empty;
    public string? Identifier { get; set; }
    public List<string> Notes { get; set; } = new();
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public bool IsZeroCost { get; set; }

    [JsonIgnore]
    public decimal ExtendedPrice => Quantity * UnitPrice;

    // ── Composition (was the model's job, now ours) ───────────────────

    // Lines the model occasionally leaks through despite the prompt. Editing this
    // list is a one-line change; changing the prompt is a re-roll of every field.
    private static readonly string[] FillerNotes =
    {
        "surgical tool",
        "no brand/marking",
        "no brand / marking",
        "no brand marking",
        "n/a"
    };

    [JsonIgnore]
    public IEnumerable<string> CleanNotes =>
        Notes.Select(NormaliseNote)
             .Where(n => n.Length > 0)
             .Where(n => !FillerNotes.Contains(n.ToLowerInvariant()))
             .Where(n => !string.Equals(n, EquipmentType.Trim(), StringComparison.OrdinalIgnoreCase))
             .Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>Line 1 of the output: "[Equipment type] - [Repair action]".</summary>
    [JsonIgnore]
    public string Header =>
        string.IsNullOrWhiteSpace(RepairAction)
            ? EquipmentType.Trim()
            : EquipmentType.Trim() + " - " + RepairAction.Trim();

    /// <summary>
    /// The block posted to AddJobService:
    ///   [Equipment type] - [Repair action]
    ///   [Identifier]
    ///   [Note]
    ///   [Note]
    /// </summary>
    [JsonIgnore]
    public string ServiceDescription
    {
        get
        {
            var lines = new List<string>();
            if (Header.Length > 0) lines.Add(Header);
            if (!string.IsNullOrWhiteSpace(Identifier)) lines.Add(Identifier.Trim());
            lines.AddRange(CleanNotes);
            return string.Join(Environment.NewLine, lines);
        }
    }

    /// <summary>Raw notes for the editor textarea — unfiltered so typing isn't fought.</summary>
    [JsonIgnore]
    public string RawNotesText => string.Join(Environment.NewLine, Notes);

    /// <summary>The Description cell exactly as printed on the quote.</summary>
    [JsonIgnore]
    public string SourceText => string.Join(Environment.NewLine, SourceDescriptionLines);

    public void SetNotesFromText(string? text) =>
        Notes = (text ?? string.Empty)
            .Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .ToList();

    private static string NormaliseNote(string? raw)
    {
        var n = (raw ?? string.Empty).Trim();
        if (n.StartsWith("-")) n = n.TrimStart('-', ' ').Trim();
        if (n.Length > 2 && n.StartsWith("(") && n.EndsWith(")"))
            n = n.Substring(1, n.Length - 2).Trim();
        if (n.StartsWith("Note:", StringComparison.OrdinalIgnoreCase))
            n = n.Substring(5).Trim();
        return n;
    }
}