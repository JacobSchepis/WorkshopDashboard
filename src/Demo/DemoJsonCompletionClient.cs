using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using WorkshopDashboard.Services.External.OpenAi;

namespace WorkshopDashboard.Demo;

// ═══════════════════════════════════════════════════════════════════════════
//  DemoJsonCompletionClient
//
//  Demo mode's LLM. Rather than canned answers per extractor, it builds a reply
//  from the JSON schema each caller already sends (the same schema OpenAI's
//  structured output is held to), so every quote page and the repair suggester
//  get a well-formed answer and nothing here needs updating when a schema
//  changes. Values come from the field names — prices for *_price / *_cost,
//  repair wording for repairs/observations — and from the request itself: the
//  job number is the first 6-digit number in the PDF's text, and any *code*
//  field picks a code from the price list in the prompt. Totals are summed from
//  the line items generated alongside them, so the pages' reconciliation checks
//  pass.
// ═══════════════════════════════════════════════════════════════════════════

public sealed class DemoJsonCompletionClient : IJsonCompletionClient
{
    private static readonly string[] Repairs =
    [
        "Replace bending section rubber", "Replace light guide bundle", "Overhaul angulation system",
        "Replace distal tip lens", "Re-seal and leak test", "Replace biopsy channel",
    ];

    private static readonly string[] Observations =
    [
        "Bending section rubber perforated at the distal end", "Angulation slack in the up/down direction",
        "Minor fluid staining under the distal lens", "Insertion tube shows light abrasion near the boot",
    ];

    public bool IsConfigured => true;

    public async Task<string> CompleteJsonSchemaAsync(string systemPrompt, string pdfText, string userPrompt, string schemaName, string jsonSchema) =>
        (await CompleteJsonSchemaWithUsageAsync(systemPrompt, pdfText, userPrompt, schemaName, jsonSchema)).Json;

    public async Task<(string Json, int InputTokens, int OutputTokens, bool TemperatureApplied)> CompleteJsonSchemaWithUsageAsync(
        string systemPrompt, string text, string userPrompt, string schemaName, string jsonSchema, float? temperature = null, string? model = null)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(700)); // feels like a model call, not an instant lookup

        var random = new Random(StableHash(text));
        var context = new Context(random, text);
        var schema = JsonNode.Parse(jsonSchema)!;
        var reply = Generate(schema, "", context)!.ToJsonString();

        return (reply, InputTokens: text.Length / 4, OutputTokens: reply.Length / 4, TemperatureApplied: temperature != null);
    }

    private sealed record Context(Random Random, string Text)
    {
        public string JobNumber { get; } = Regex.Match(Text, @"\b(\d{6})\b") is { Success: true } m ? m.Groups[1].Value : "";

        // Price list lines look like "CODE | category | ..." (see ApexRepairSuggester.BuildPriceList).
        public string[] Codes { get; } = Regex.Matches(Text, @"^\s*([A-Z0-9][A-Z0-9-]{1,15})\s*\|", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value).Distinct().ToArray();
    }

    private static JsonNode? Generate(JsonNode schema, string name, Context ctx)
    {
        if (schema["enum"] is JsonArray options && options.Count > 0)
            return options[ctx.Random.Next(options.Count)]!.DeepClone();

        return TypeOf(schema) switch
        {
            "object" => GenerateObject(schema, ctx),
            "array" => GenerateArray(schema, name, ctx),
            "string" => JsonValue.Create(StringFor(name, ctx)),
            "number" => JsonValue.Create(NumberFor(name, ctx)),
            "integer" => JsonValue.Create(IntegerFor(name, ctx)),
            "boolean" => JsonValue.Create(false),
            _ => null,
        };
    }

    // "type" can be a list (e.g. ["string", "null"]); use the first non-null one.
    private static string? TypeOf(JsonNode schema) => schema["type"] switch
    {
        JsonArray types => types.Select(t => t!.GetValue<string>()).FirstOrDefault(t => t != "null"),
        JsonValue v => v.GetValue<string>(),
        _ => schema["properties"] != null ? "object" : null,
    };

    private static JsonObject GenerateObject(JsonNode schema, Context ctx)
    {
        var obj = new JsonObject();
        if (schema["properties"] is not JsonObject properties) return obj;

        foreach (var (key, sub) in properties)
            obj[key] = Generate(sub!, key, ctx);

        // A total sits next to the line items it adds up — make it match them.
        foreach (var (key, _) in properties.Where(p => p.Key.StartsWith("total", StringComparison.OrdinalIgnoreCase)))
        {
            if (obj[key] is not JsonValue v || !v.TryGetValue<double>(out _)) continue;
            var lines = obj.Where(p => p.Value is JsonArray).SelectMany(p => p.Value!.AsArray()).OfType<JsonObject>().ToList();
            var sum = lines.Sum(line => line.Where(f => IsMoney(f.Key) && !f.Key.Contains("unit")).Select(f => f.Value is JsonValue n && n.TryGetValue<double>(out var d) ? d : 0).FirstOrDefault());
            if (sum > 0) obj[key] = Math.Round(sum, 2);
        }
        return obj;
    }

    private static JsonArray GenerateArray(JsonNode schema, string name, Context ctx)
    {
        var count = name.Contains("note", StringComparison.OrdinalIgnoreCase) ? ctx.Random.Next(0, 2) : ctx.Random.Next(1, 4);
        var array = new JsonArray();
        for (var i = 0; i < count && schema["items"] is { } items; i++)
            array.Add(Generate(items, Singular(name), ctx));
        return array;
    }

    private static string StringFor(string name, Context ctx)
    {
        var n = name.ToLowerInvariant();
        return n switch
        {
            _ when n.Contains("job_number") || n == "job_no" => ctx.JobNumber,
            _ when n.Contains("code") => ctx.Codes.Length > 0 ? ctx.Codes[ctx.Random.Next(ctx.Codes.Length)] : "",
            _ when n.Contains("currency") => "USD",
            _ when n.Contains("date") => DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            _ when n.Contains("serial") => ctx.Random.Next(1000000, 9999999).ToString(CultureInfo.InvariantCulture),
            _ when n.Contains("model") => "GIF-H190",
            _ when n.Contains("observation") || n.Contains("finding") || n.Contains("reason") => Observations[ctx.Random.Next(Observations.Length)],
            _ when n.Contains("note") => "Turnaround approximately 10 working days from approval.",
            _ when n.Contains("turnaround") => "10 working days",
            _ when n.Contains("level") => "Level 2",
            _ => Repairs[ctx.Random.Next(Repairs.Length)],
        };
    }

    private static double NumberFor(string name, Context ctx) =>
        IsMoney(name) ? Math.Round(ctx.Random.Next(80, 900) * 5 / 5.0 * 5, 0) : ctx.Random.Next(1, 10);

    private static int IntegerFor(string name, Context ctx)
    {
        var n = name.ToLowerInvariant();
        if (n.Contains("days")) return 10;
        if (n.Contains("rod_lens")) return ctx.Random.Next(0, 2);
        return 1; // quantities and counts
    }

    private static bool IsMoney(string name) =>
        name.Contains("price", StringComparison.OrdinalIgnoreCase) || name.Contains("cost", StringComparison.OrdinalIgnoreCase)
        || name.Contains("amount", StringComparison.OrdinalIgnoreCase) || name.Equals("total", StringComparison.OrdinalIgnoreCase);

    private static string Singular(string name) => name.EndsWith('s') ? name[..^1] : name;

    // Same document, same answer — like temperature 0.
    private static int StableHash(string s)
    {
        unchecked
        {
            var h = 17;
            foreach (var c in s) h = h * 31 + c;
            return h;
        }
    }
}
