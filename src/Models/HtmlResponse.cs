namespace WorkshopDashboard.Models;

public readonly record struct HtmlResponse(
    bool Success,
    int StatusCode,
    string? ReasonPhrase,
    string Content,
    string? Url,
    string? ContentType,
    long? ContentLength,
    IReadOnlyDictionary<string, string> Headers
);
