namespace WorkshopDashboard.Services.Quoting;

using System.IO;
using System.Text.Json;
using System.Text;
using UglyToad.PdfPig;
using WorkshopDashboard.Services.External.OpenAi;

public abstract class BaseQuoteExtractor
{
    private readonly IJsonCompletionClient _client;

    protected BaseQuoteExtractor(IJsonCompletionClient client)
    {
        _client = client;
    }

    protected abstract string SystemPrompt { get; }
    protected abstract string SchemaName { get; }
    protected abstract string JsonSchema { get; }

    protected virtual string UserPrompt => """
        Extract the following and return as JSON:
        - job_number: a 6-digit reference or job number found anywhere in the document (return empty string if not found)
        - technical_observations: notes about the condition or findings of the item
        - works_required: the list of repair tasks or services to be performed
        - parts: any parts, components or consumables mentioned
        - work_order_notes: any miscellaneous notes, instructions or comments
        - line_items: each billable item with description, quantity, unit price and total
        - total_price: the final total amount
        - currency: the currency (default AUD if not specified)
        """;

    protected async Task<T> ExtractAsync<T>(byte[] pdfBytes)
    {
        var pdfText = ExtractPdfText(pdfBytes);
        var json = await _client.CompleteJsonSchemaAsync(SystemPrompt, pdfText, UserPrompt, SchemaName, JsonSchema);

        return JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        }) ?? throw new Exception("Failed to deserialize quote extraction.");
    }

    private static string ExtractPdfText(byte[] pdfBytes)
    {
        var sb = new StringBuilder();
        using var pdf = PdfDocument.Open(pdfBytes);
        foreach (var page in pdf.GetPages())
            sb.AppendLine(string.Join(" ", page.GetWords().Select(w => w.Text)));
        return sb.ToString();
    }
}

public class QuoteExtraction
{
    public string JobNumber { get; set; } = string.Empty;
    public List<string> TechnicalObservations { get; set; } = new();
    public List<string> WorksRequired { get; set; } = new();
    public List<string> Parts { get; set; } = new();
    public List<string> WorkOrderNotes { get; set; } = new();
    public List<LineItem> LineItems { get; set; } = new();
    public decimal TotalPrice { get; set; }
    public string Currency { get; set; } = "AUD";
}

public class LineItem
{
    public string Description { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal Total { get; set; }
}
