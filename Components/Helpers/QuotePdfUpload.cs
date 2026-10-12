using Microsoft.AspNetCore.Components.Forms;

namespace WorkshopDashboard.Components.Helpers;

/// <summary>
/// Shared PDF upload validation/read logic for the quote extractor pages.
/// </summary>
public static class QuotePdfUpload
{
    /// <summary>
    /// Validates the selected file is a .pdf and reads it into memory.
    /// Returns null (after calling <paramref name="onError"/>) if validation fails.
    /// </summary>
    public static async Task<byte[]?> ReadPdfAsync(InputFileChangeEventArgs e, Action<string> onError)
    {
        var file = e.File;

        if (!file.Name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            onError("Please upload a PDF file.");
            return null;
        }

        using var stream = new MemoryStream();
        await file.OpenReadStream(maxAllowedSize: 10 * 1024 * 1024).CopyToAsync(stream);
        return stream.ToArray();
    }
}
