using System.Diagnostics;
using System.Text;
using System.Text.Json;
using WorkshopDashboard.Data;
using WorkshopDashboard.Services.External.OpenAi;
using WorkshopDashboard.Services.Stores;

namespace WorkshopDashboard.Services.Quoting.Apex;

public enum SuggestionConfidence { High, Medium, Low }

/// <summary>A price sheet line the AI picked for a finding, and why.</summary>
public record SuggestedRepair(ApexPriceLine Line, string Reason, SuggestionConfidence Confidence);

/// <summary>A second-pass change: <see cref="Line"/> in place of <see cref="Replaced"/> — either a
/// cap / broader repair swapped in for several lines (IsSwap), or lines dropped because another
/// chosen line (Line, already in the quote) includes them. Accepted only if it's cheaper.</summary>
public record RepairConsolidation(
    ApexPriceLine Line,
    List<ApexPriceLine> Replaced,
    string Explanation,
    bool Accepted,
    string? RejectedBecause,
    bool IsSwap = true)
{
    public decimal ReplacedUsd => Replaced.Sum(r => r.PriceUsd);
    public decimal SavingUsd => IsSwap ? ReplacedUsd - Line.PriceUsd : ReplacedUsd;
}

public record ApexRepairSuggestion(
    List<string> TechnicalObservations,
    List<SuggestedRepair> Repairs,
    List<string> UnmatchedFindings,
    List<string> RejectedCodes,
    string Model,
    bool Deterministic,
    int InputTokens,
    int OutputTokens,
    TimeSpan Elapsed,
    string RawJson)
{
    public decimal TotalUsd => Repairs.Sum(r => r.Line.PriceUsd);

    /// <summary>Pass 1's line-per-finding picks, before combining (same as Repairs if pass 2
    /// didn't run or changed nothing).</summary>
    public List<SuggestedRepair> PassOneRepairs { get; init; } = new();
    public decimal PassOneTotalUsd => PassOneRepairs.Sum(r => r.Line.PriceUsd);

    /// <summary>What pass 2 proposed, accepted or not.</summary>
    public List<RepairConsolidation> Consolidations { get; init; } = new();
    public string? PassTwoRawJson { get; init; }
}

// ═══════════════════════════════════════════════════════════════════════════
//  ApexRepairSuggester
//
//  Reads a technician's inspection notes and picks the matching lines from
//  Apex's flexible repair price sheet (ApexPriceSheetStore), for in-house
//  flex quotes, in two passes:
//    1. What's wrong: one best-fitting line per finding (no prices shown, so
//       the choice is about fit).
//    2. Combining: given those lines with prices, plus the whole sheet with
//       prices, propose where a cap price or a broader repair covers several
//       of them, or where one chosen line already includes another.
//  The AI only proposes; the code decides. Every code must be on the sheet,
//  descriptions and prices always come from the sheet, and a pass-2 change is
//  only applied if it lowers the total (the arithmetic is done here, not by
//  the AI). Temperature 0 where the model allows it. Suggestions are for a
//  person to review; this never posts anything.
// ═══════════════════════════════════════════════════════════════════════════

public class ApexRepairSuggester
{
    /// <summary>Models offered on the Repair Suggestion Test page, default first. The GPT-6
    /// ones reason before answering; gpt-4o is the older model the quote PDF extractors use.</summary>
    public static readonly (string Id, string Label)[] Models =
    [
        ("gpt-6.1-sol", "GPT-6.1 Sol — recommended, ~10c a run"),
        ("gpt-6-astra", "GPT-6 Astra — most capable, ~35c a run"),
        ("gpt-6-luna", "GPT-6 Luna — cheapest, under 1c a run"),
        ("gpt-4o", "GPT-4o — older, what the quote PDFs use"),
    ];

    public static string DefaultModel => Models[0].Id;

    private readonly ApexPriceSheetStore _priceSheet;
    private readonly IJsonCompletionClient _client;

    public ApexRepairSuggester(ApexPriceSheetStore priceSheet, IJsonCompletionClient client)
    {
        _priceSheet = priceSheet;
        _client = client;
    }

    public async Task<ApexRepairSuggestion> SuggestAsync(string inspectionNotes, string? scopeModel, string? model = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(inspectionNotes))
            throw new ArgumentException("Paste some inspection notes first.");

        var lines = await _priceSheet.LoadAsync();
        if (lines.Count == 0)
            throw new InvalidOperationException("The Apex price sheet hasn't been imported yet — do that on the Apex Price Sheet page.");

        var modelId = string.IsNullOrWhiteSpace(model) ? DefaultModel : model;
        var stopwatch = Stopwatch.StartNew();

        // Pass 1 — a line per finding.
        var (json, inputTokens, outputTokens, deterministic) = await _client.CompleteJsonSchemaWithUsageAsync(
            SystemPrompt, BuildPriceList(lines), BuildRequest(inspectionNotes, scopeModel),
            "apex_repair_suggestion", JsonSchema, temperature: 0, model: modelId);
        var suggestion = Interpret(json, lines);

        // Pass 2 — combine into caps / broader repairs where that's cheaper. Needs at least two
        // lines to combine.
        var repairs = suggestion.Repairs;
        var consolidations = new List<RepairConsolidation>();
        string? passTwoJson = null;
        if (repairs.Count >= 2)
        {
            var two = await _client.CompleteJsonSchemaWithUsageAsync(
                CombineSystemPrompt, BuildPriceList(lines, withPrices: true), BuildCombineRequest(repairs, scopeModel),
                "apex_repair_combine", CombineJsonSchema, temperature: 0, model: modelId);
            passTwoJson = two.Json;
            inputTokens += two.InputTokens;
            outputTokens += two.OutputTokens;
            deterministic &= two.TemperatureApplied;
            (repairs, consolidations) = ApplyConsolidations(repairs, ParseCombine(two.Json), lines);
        }
        stopwatch.Stop();

        return suggestion with
        {
            Repairs = repairs,
            PassOneRepairs = suggestion.Repairs,
            Consolidations = consolidations,
            PassTwoRawJson = passTwoJson,
            Model = modelId,
            Deterministic = deterministic,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            Elapsed = stopwatch.Elapsed,
        };
    }

    /// <summary>A change pass 2 proposed, as the AI gave it.</summary>
    internal record ProposedChange(string UseCode, List<string> ReplacesCodes, string Explanation);

    /// <summary>Applies pass 2's proposals to pass 1's lines, one at a time, keeping only those
    /// that are valid and make the quote cheaper: every code on the sheet, only replacing lines
    /// still in the quote, and — for a swap to a new line — the new line costing less than what
    /// it replaces. A line dropped because another chosen line includes it is always cheaper.</summary>
    internal static (List<SuggestedRepair> Repairs, List<RepairConsolidation> Consolidations) ApplyConsolidations(
        List<SuggestedRepair> passOne, IEnumerable<ProposedChange> proposals, IReadOnlyCollection<ApexPriceLine> sheet)
    {
        var byCode = sheet.ToDictionary(l => l.Code, StringComparer.OrdinalIgnoreCase);
        var current = passOne.ToList();
        var results = new List<RepairConsolidation>();

        foreach (var p in proposals)
        {
            var explanation = p.Explanation.Trim();
            if (!byCode.TryGetValue(p.UseCode.Trim(), out var line))
            {
                results.Add(Rejected(new ApexPriceLine { Code = p.UseCode, Description = "(not on the price sheet)" }, new(), explanation,
                    $"{p.UseCode} isn't on the price sheet."));
                continue;
            }

            // Only lines still in the quote can be replaced, and never the line being kept.
            var replaced = current
                .Where(r => p.ReplacesCodes.Any(c => string.Equals(c.Trim(), r.Line.Code, StringComparison.OrdinalIgnoreCase))
                            && !string.Equals(r.Line.Code, line.Code, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (replaced.Count == 0)
            {
                results.Add(Rejected(line, new(), explanation, "It didn't name any lines that are in the quote."));
                continue;
            }

            var keepingExisting = current.Any(r => string.Equals(r.Line.Code, line.Code, StringComparison.OrdinalIgnoreCase));
            var replacedUsd = replaced.Sum(r => r.Line.PriceUsd);
            if (!keepingExisting && line.PriceUsd >= replacedUsd)
            {
                results.Add(Rejected(line, replaced.Select(r => r.Line).ToList(), explanation,
                    $"{line.Code} ({line.PriceUsd:C}) isn't cheaper than the {replaced.Count} line(s) it replaces ({replacedUsd:C})."));
                continue;
            }

            foreach (var r in replaced) current.Remove(r);
            if (!keepingExisting)
            {
                // The swapped-in line takes the place of the first line it replaces, with the
                // lowest confidence among them — it's only as sure as its least sure finding.
                var at = passOne.IndexOf(replaced[0]);
                var confidence = replaced.Max(r => r.Confidence);
                current.Insert(Math.Min(Math.Max(0, at), current.Count), new SuggestedRepair(line, explanation, confidence));
            }
            results.Add(new RepairConsolidation(line, replaced.Select(r => r.Line).ToList(), explanation, Accepted: true, RejectedBecause: null, IsSwap: !keepingExisting));
        }

        return (current, results);

        static RepairConsolidation Rejected(ApexPriceLine line, List<ApexPriceLine> replaced, string explanation, string why) =>
            new(line, replaced, explanation, Accepted: false, RejectedBecause: why);
    }

    internal static List<ProposedChange> ParseCombine(string json)
    {
        var parsed = JsonSerializer.Deserialize<AiCombineResponse>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return (parsed?.changes ?? new())
            .Where(c => !string.IsNullOrWhiteSpace(c.use_code))
            .Select(c => new ProposedChange(c.use_code!, c.replaces_codes ?? new(), c.explanation ?? ""))
            .ToList();
    }

    /// <summary>Turns the AI's JSON into a suggestion, keeping only codes that are on the sheet.</summary>
    internal static ApexRepairSuggestion Interpret(string json, IReadOnlyCollection<ApexPriceLine> lines)
    {
        var byCode = lines.ToDictionary(l => l.Code, StringComparer.OrdinalIgnoreCase);
        var parsed = JsonSerializer.Deserialize<AiResponse>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("The AI returned an empty answer.");

        var repairs = new List<SuggestedRepair>();
        var rejected = new List<string>();
        foreach (var r in parsed.repairs ?? new())
        {
            var code = (r.code ?? "").Trim();
            if (!byCode.TryGetValue(code, out var line))
            {
                if (code.Length > 0) rejected.Add(code);
                continue;
            }
            if (repairs.Any(x => x.Line.Code == line.Code)) continue;

            var confidence = Enum.TryParse<SuggestionConfidence>(r.confidence, ignoreCase: true, out var c) ? c : SuggestionConfidence.Low;
            repairs.Add(new SuggestedRepair(line, (r.reason ?? "").Trim(), confidence));
        }

        return new ApexRepairSuggestion(
            Clean(parsed.technical_observations),
            repairs,
            Clean(parsed.unmatched_findings),
            rejected,
            Model: "",
            Deterministic: false,
            InputTokens: 0,
            OutputTokens: 0,
            Elapsed: TimeSpan.Zero,
            RawJson: json);
    }

    private static List<string> Clean(List<string>? items) =>
        (items ?? new()).Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

    // Pass 1 gets codes, categories and descriptions only — leaving prices out keeps the choice
    // about fit, not cost. Pass 2 needs the prices to see where a cap is cheaper.
    internal static string BuildPriceList(IEnumerable<ApexPriceLine> lines, bool withPrices = false)
    {
        var sb = new StringBuilder(withPrices
            ? "APEX FLEXIBLE REPAIR PRICE SHEET (code | category | USD price | description):\n"
            : "APEX FLEXIBLE REPAIR PRICE SHEET (code | category | description):\n");
        foreach (var l in lines.OrderBy(l => l.LineItem))
        {
            sb.Append(l.Code).Append(" | ").Append(l.Category).Append(" | ");
            if (withPrices) sb.Append('$').Append(l.PriceUsd.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)).Append(" | ");
            sb.AppendLine(l.Description);
        }
        return sb.ToString();
    }

    private static string BuildCombineRequest(List<SuggestedRepair> repairs, string? scopeModel)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"SCOPE MODEL: {(string.IsNullOrWhiteSpace(scopeModel) ? "not given" : scopeModel.Trim())}");
        sb.AppendLine();
        sb.AppendLine("LINES CHOSEN FOR EACH FINDING (code | category | USD price | description | why it was chosen):");
        foreach (var r in repairs)
            sb.AppendLine($"{r.Line.Code} | {r.Line.Category} | ${r.Line.PriceUsd.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)} | {r.Line.Description} | {r.Reason}");
        sb.AppendLine();
        sb.AppendLine("Propose the changes that combine these, following the rules. Propose nothing if none apply.");
        return sb.ToString();
    }

    private static string BuildRequest(string inspectionNotes, string? scopeModel) =>
        $"""
        SCOPE MODEL: {(string.IsNullOrWhiteSpace(scopeModel) ? "not given — infer it from the notes if they say, otherwise prefer lines that aren't model-specific" : scopeModel.Trim())}

        TECHNICIAN'S INSPECTION NOTES:
        {inspectionNotes.Trim()}

        Pick the repairs from the price sheet above, following the rules.
        """;

    private const string SystemPrompt = """
        You are an experienced flexible endoscope repair estimator. From a technician's inspection
        notes you choose which lines of Apex's repair price sheet the repair needs.

        Rules:
        - Only use codes that appear in the price sheet, copied exactly.
        - Each finding that needs fixing maps to the one line that best fits it. Where a repair has
          variants (manufacturer, series such as 140/160 or 180/190, large vs small diameter, a
          specific model), pick the variant that matches the scope model.
        - Don't double-charge: many lines say what they include ("Includes bending rubber and
          angulation adjustment", "Includes, if applicable, ..."). If a line you've chosen already
          covers a finding, don't add a separate line for it.
        - Prefer the smallest repair that fixes the finding (e.g. reseal before replace) unless
          the notes say it's beyond that.
        - Findings that are just observations needing no repair, or that no line fits, go in
          unmatched_findings (in plain words) instead of forcing a line.
        - reason: one short sentence tying the line to the finding in the notes.
        - confidence: high when the notes clearly call for that exact line; medium when the
          finding is clear but the variant is a judgement call; low when you're guessing.
        - technical_observations: the findings rewritten as short, factual sentences for a
          customer quote ("Bending rubber torn.", "Fluid invasion in the control body."), grouping
          related findings on the same component. No filler, no prices, no codes.
        """;

    private const string JsonSchema = """
        {
            "type": "object",
            "properties": {
                "technical_observations": { "type": "array", "items": { "type": "string" } },
                "repairs": {
                    "type": "array",
                    "items": {
                        "type": "object",
                        "properties": {
                            "code":       { "type": "string" },
                            "reason":     { "type": "string" },
                            "confidence": { "type": "string", "enum": ["high", "medium", "low"] }
                        },
                        "required": ["code", "reason", "confidence"],
                        "additionalProperties": false
                    }
                },
                "unmatched_findings": { "type": "array", "items": { "type": "string" } }
            },
            "required": ["technical_observations", "repairs", "unmatched_findings"],
            "additionalProperties": false
        }
        """;

    private const string CombineSystemPrompt = """
        You are an experienced flexible endoscope repair estimator checking a draft Apex
        quote. The draft has one price sheet line per finding. Your job is to spot where lines should
        be combined, the way Apex would price the job.

        Two kinds of change:
        1. INCLUDED — one chosen line's description already includes the work of another chosen line
           ("Includes bending rubber and angulation adjustment", "Includes, if applicable, ...").
           Keep the including line: use_code = that line's code (it must be one of the chosen lines),
           replaces_codes = the lines it already covers.
        2. CAP / BROADER REPAIR — a single line on the sheet for THIS scope model (usually a "Cap"
           price, category "Cap - Small Diameter" / "Cap - Large Diameter", or an inner component /
           insertion tube replacement) whose inclusions cover two or more chosen lines, and whose
           price is lower than those lines added together. use_code = that line's code,
           replaces_codes = the chosen lines it covers.

        Rules:
        - Only use codes that appear on the price sheet, copied exactly. The cap or broader line
          must match the scope model (manufacturer and model/series) — never another model's.
        - A cap's description lists what it includes and which "specialty parts" (e.g. CCD chip,
          bending section, objective lens, inner control chassis, light guide connector) are an
          additional charge. Never let a cap replace a line for one of its excluded specialty
          parts — those stay as separate lines.
        - Only replace a line if the new line genuinely covers that line's work. Don't use a cap
          just because it's cheaper if it doesn't cover the findings.
        - Each chosen line can be replaced at most once. Propose nothing if no change applies.
        - explanation: one short sentence saying what is combined into what and why, e.g.
          "F2020 GIF-H190 cap covers the insertion tube, U-cord and light bundle repairs."
        """;

    private const string CombineJsonSchema = """
        {
            "type": "object",
            "properties": {
                "changes": {
                    "type": "array",
                    "items": {
                        "type": "object",
                        "properties": {
                            "use_code":       { "type": "string" },
                            "replaces_codes": { "type": "array", "items": { "type": "string" } },
                            "explanation":    { "type": "string" }
                        },
                        "required": ["use_code", "replaces_codes", "explanation"],
                        "additionalProperties": false
                    }
                }
            },
            "required": ["changes"],
            "additionalProperties": false
        }
        """;

    private class AiCombineResponse
    {
        public List<AiChange>? changes { get; set; }
    }

    private class AiChange
    {
        public string? use_code { get; set; }
        public List<string>? replaces_codes { get; set; }
        public string? explanation { get; set; }
    }

    private class AiResponse
    {
        public List<string>? technical_observations { get; set; }
        public List<AiRepair>? repairs { get; set; }
        public List<string>? unmatched_findings { get; set; }
    }

    private class AiRepair
    {
        public string? code { get; set; }
        public string? reason { get; set; }
        public string? confidence { get; set; }
    }
}
