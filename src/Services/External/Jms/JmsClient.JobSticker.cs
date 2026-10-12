namespace WorkshopDashboard.Services.External.Jms;

public record JobStickerFile(byte[] Bytes, string? FileName);

public sealed partial class JmsClient
{
    /// <summary>Downloads a job's sticker PDF via JobSticker's own "Print Job Sticker" link
    /// (a plain GET form on AddJobEquipment: type=job, title={jobId}, printer blank, a fixed
    /// sn=123456789 left over from the same template's equipment-sticker form, copies). Returns null
    /// if the response doesn't look like a PDF (e.g. an expired session serving back an HTML page).
    /// FileName is whatever the server's own Content-Disposition header names it — the same name a
    /// real manual download would get — rather than something invented here.</summary>
    public async Task<JobStickerFile?> GetJobStickerPdfAsync(int jobId, int copies = 1, CancellationToken ct = default)
    {
        var endpoint = $"{E.JobSticker}?type=job&title={jobId}&printer=&sn=123456789&copies={copies}".TrimStart('/');

        using var req = new HttpRequestMessage(HttpMethod.Get, endpoint);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(DefaultTimeout);

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        resp.EnsureSuccessStatusCode();

        var bytes = await resp.Content.ReadAsByteArrayAsync(cts.Token);
        if (!LooksLikePdf(bytes)) return null;

        var fileName = resp.Content.Headers.ContentDisposition?.FileNameStar
            ?? resp.Content.Headers.ContentDisposition?.FileName?.Trim('"');

        return new JobStickerFile(bytes, fileName);
    }
}
