using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Chat;

namespace WorkshopDashboard.Services.External.OpenAi;

/// <summary>
/// Raw OpenAI chat-completion client used by the quote extractors (BaseQuoteExtractor and
/// its per-repairer subclasses in Services.Quoting) to turn a work-order PDF's text into a
/// structured JSON extraction. Level 0 — knows only how to talk to OpenAI's API, nothing
/// about quotes/jobs/pricing.
/// </summary>
/// <summary>Structured (JSON-schema-constrained) completions — what the quote extractors and the
/// Apex repair suggester need from an LLM. OpenAiQuoteClient in normal use; DemoJsonCompletionClient
/// in demo mode, which answers without a key.</summary>
public interface IJsonCompletionClient
{
    bool IsConfigured { get; }

    Task<string> CompleteJsonSchemaAsync(string systemPrompt, string pdfText, string userPrompt, string schemaName, string jsonSchema);

    Task<(string Json, int InputTokens, int OutputTokens, bool TemperatureApplied)> CompleteJsonSchemaWithUsageAsync(
        string systemPrompt, string text, string userPrompt, string schemaName, string jsonSchema, float? temperature = null, string? model = null);
}

public class OpenAiQuoteClient : IJsonCompletionClient
{
    public const string DefaultModel = "gpt-4o";

    private readonly string _apiKey;
    private OpenAIClient? _client;

    public OpenAiQuoteClient(IOptions<OpenAiOptions> options) => _apiKey = options.Value.ApiKey;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_apiKey);

    // Built on first use rather than in the constructor, so a missing key only fails the
    // extraction that needed it (the quote pages show the message) instead of every page
    // that injects this.
    private ChatClient ChatFor(string? model)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("OpenAI isn't configured — set OpenAI:ApiKey.");
        _client ??= new OpenAIClient(_apiKey);
        return _client.GetChatClient(string.IsNullOrWhiteSpace(model) ? DefaultModel : model);
    }

    /// <summary>Sends the PDF text + prompts to OpenAI and returns the raw JSON response text,
    /// constrained to the given JSON schema.</summary>
    public async Task<string> CompleteJsonSchemaAsync(string systemPrompt, string pdfText, string userPrompt, string schemaName, string jsonSchema) =>
        (await CompleteOnceAsync(systemPrompt, pdfText, userPrompt, schemaName, jsonSchema, temperature: null, model: null)).Json;

    /// <summary>Same call, also reporting token usage. A <paramref name="temperature"/> of 0 makes
    /// repeat runs on the same input give (near enough) the same answer — but reasoning models
    /// can refuse the setting, so if the model rejects it the call is retried once without it.
    /// TemperatureApplied says which happened.</summary>
    public async Task<(string Json, int InputTokens, int OutputTokens, bool TemperatureApplied)> CompleteJsonSchemaWithUsageAsync(
        string systemPrompt, string text, string userPrompt, string schemaName, string jsonSchema, float? temperature = null, string? model = null)
    {
        try
        {
            var (json, input, output) = await CompleteOnceAsync(systemPrompt, text, userPrompt, schemaName, jsonSchema, temperature, model);
            return (json, input, output, temperature != null);
        }
        catch (System.ClientModel.ClientResultException ex)
            when (temperature != null && ex.Message.Contains("temperature", StringComparison.OrdinalIgnoreCase))
        {
            var (json, input, output) = await CompleteOnceAsync(systemPrompt, text, userPrompt, schemaName, jsonSchema, temperature: null, model);
            return (json, input, output, false);
        }
    }

    private async Task<(string Json, int InputTokens, int OutputTokens)> CompleteOnceAsync(
        string systemPrompt, string text, string userPrompt, string schemaName, string jsonSchema, float? temperature, string? model)
    {
        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(systemPrompt),
            new UserChatMessage(
                ChatMessageContentPart.CreateTextPart(text),
                ChatMessageContentPart.CreateTextPart(userPrompt))
        };

        var options = new ChatCompletionOptions
        {
            ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
                jsonSchemaFormatName: schemaName,
                jsonSchema: BinaryData.FromString(jsonSchema),
                jsonSchemaIsStrict: true),
            Temperature = temperature,
        };

        var response = await ChatFor(model).CompleteChatAsync(messages, options);
        var usage = response.Value.Usage;
        return (response.Value.Content[0].Text, usage?.InputTokenCount ?? 0, usage?.OutputTokenCount ?? 0);
    }
}
