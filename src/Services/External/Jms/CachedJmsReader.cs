using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;
using WorkshopDashboard.Abstract;
using WorkshopDashboard.Models;

namespace WorkshopDashboard.Services.External.Jms;

// ═══════════════════════════════════════════════════════════════════════════
//  CachedJmsReader
//
//  The IJmsReader every service actually gets from JmsReadSession. SQL queries
//  always go to JMS live — a cache that answered first would show stale data
//  right after someone's own change (add a note, reload, note missing) — but
//  each successful result is kept, so when JMS is down or a query fails the
//  last good result is served instead of an empty page. JmsOfflineBanner tells
//  people when that's happening.
//
//  Identical queries already in flight share one request (kiosks and busy
//  pages fire the same ones at the same moment).
//
//  While JmsStatus says JMS is down, no live attempt is made at all, so pages
//  load straight from the cache instead of each waiting out a timeout.
//
//  Only queries are cached. Documents (quote PDFs, reports, stickers) and
//  lookups pass straight through; with no live session they return the same
//  empty values callers already handled when the session used to be null.
// ═══════════════════════════════════════════════════════════════════════════

public sealed class CachedJmsReader : IJmsReader, IDisposable
{
    private static readonly TimeSpan KeepFor = TimeSpan.FromHours(24);

    private static readonly HtmlResponse Unavailable =
        new(false, 0, "JMS unavailable.", "", null, null, null, new Dictionary<string, string>());

    private readonly Func<CancellationToken, Task<IJmsReader?>> _getLiveClient;
    private readonly JmsStatus _status;
    private readonly ILogger<CachedJmsReader> _logger;

    // Sized by content length; ~200 MB of CSV is far more than the app's distinct queries
    // produce, so this only ever evicts on genuinely unusual days.
    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 200_000_000 });
    private readonly ConcurrentDictionary<string, Lazy<Task<HtmlResponse?>>> _inFlight = new();

    public CachedJmsReader(Func<CancellationToken, Task<IJmsReader?>> getLiveClient, JmsStatus status, ILogger<CachedJmsReader> logger)
    {
        _getLiveClient = getLiveClient;
        _status = status;
        _logger = logger;
    }

    // ---------------------------
    // SQL queries (cached)
    // ---------------------------

    public async Task<HtmlResponse> PostQueryForCsvAsync(string query)
    {
        var live = await _inFlight.GetOrAdd(query, q => new Lazy<Task<HtmlResponse?>>(() => QueryLiveAsync(q))).Value;
        if (live is { Success: true }) return live.Value;

        if (_cache.TryGetValue(query, out HtmlResponse cached))
        {
            _logger.LogInformation("JMS query failed — serving the last good result instead");
            return cached;
        }

        return live ?? Unavailable;
    }

    private async Task<HtmlResponse?> QueryLiveAsync(string query)
    {
        try
        {
            if (_status.IsUp == false) return null;

            var client = await _getLiveClient(CancellationToken.None);
            if (client is null) return null;

            var res = await client.PostQueryForCsvAsync(query);
            if (res.Success)
                _cache.Set(query, res, new MemoryCacheEntryOptions
                {
                    Size = Math.Max(1, res.Content.Length * 2),
                    AbsoluteExpirationRelativeToNow = KeepFor
                });
            return res;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            _logger.LogWarning("JMS query failed: {Error}", ex.Message);
            return null;
        }
        finally
        {
            _inFlight.TryRemove(query, out _);
        }
    }

    // ---------------------------
    // Everything else (live only)
    // ---------------------------

    public Task<HtmlResponse> PostUncachedQueryForCsvAsync(string query) =>
        LiveAsync(c => c.PostQueryForCsvAsync(query), Unavailable, CancellationToken.None);

    public Task<List<JmsUser>> SearchUsersAsync(string q, CancellationToken ct = default) =>
        LiveAsync(c => c.SearchUsersAsync(q, ct), new List<JmsUser>(), ct);

    public Task<string?> GetItemPricingJsonAsync(int masterItemId, CancellationToken ct = default) =>
        LiveAsync(c => c.GetItemPricingJsonAsync(masterItemId, ct), null, ct);

    public Task<HtmlResponse> GetNotYetReceivedItemsAsync(int vendorCompanyId, CancellationToken ct = default) =>
        LiveAsync(c => c.GetNotYetReceivedItemsAsync(vendorCompanyId, ct), Unavailable, ct);

    public Task<byte[]?> GetQuotePdfAsync(string fileLocation, string type = "quote", CancellationToken ct = default) =>
        LiveAsync(c => c.GetQuotePdfAsync(fileLocation, type, ct), null, ct);

    public Task<JobStickerFile?> GetJobStickerPdfAsync(int jobId, int copies = 1, CancellationToken ct = default) =>
        LiveAsync(c => c.GetJobStickerPdfAsync(jobId, copies, ct), null, ct);

    public Task<byte[]> GetWorkSummaryReportAsync(int jobId, int reportId, int companyId, CancellationToken ct = default) =>
        LiveAsync(c => c.GetWorkSummaryReportAsync(jobId, reportId, companyId, ct), Array.Empty<byte>(), ct);

    public Task<byte[]> GetNotSeenReportAsync(int jobId, int reportId, int companyId, CancellationToken ct = default) =>
        LiveAsync(c => c.GetNotSeenReportAsync(jobId, reportId, companyId, ct), Array.Empty<byte>(), ct);

    public Task<byte[]> GetErfaReportAsync(int jobId, int reportId, int companyId, CancellationToken ct = default) =>
        LiveAsync(c => c.GetErfaReportAsync(jobId, reportId, companyId, ct), Array.Empty<byte>(), ct);

    public Task<byte[]> GetCertificateAsync(string certificateFilename, CancellationToken ct = default) =>
        LiveAsync(c => c.GetCertificateAsync(certificateFilename, ct), Array.Empty<byte>(), ct);

    public Task<byte[]> GetDetailedInspectionReportAsync(int inspectionId, CancellationToken ct = default) =>
        LiveAsync(c => c.GetDetailedInspectionReportAsync(inspectionId, ct), Array.Empty<byte>(), ct);

    public Task<byte[]> GetRsScheduleReportAsync(int companyId, CancellationToken ct = default) =>
        LiveAsync(c => c.GetRsScheduleReportAsync(companyId, ct), Array.Empty<byte>(), ct);

    public Task<byte[]> GetJobRepairSummaryReportAsync(int companyId, DateTime date, CancellationToken ct = default) =>
        LiveAsync(c => c.GetJobRepairSummaryReportAsync(companyId, date, ct), Array.Empty<byte>(), ct);

    public Task<byte[]> GetRsPerformanceReportAsync(int companyId, DateTime from, DateTime to, CancellationToken ct = default) =>
        LiveAsync(c => c.GetRsPerformanceReportAsync(companyId, from, to, ct), Array.Empty<byte>(), ct);

    public Task<bool?> QueueJobProgressReportAsync(int companyId, string companyName, DateTime from, DateTime to, CancellationToken ct = default) =>
        LiveAsync(c => c.QueueJobProgressReportAsync(companyId, companyName, from, to, ct), (bool?)null, ct);

    public Task<byte[]> DownloadAsync(string relativePath, CancellationToken ct = default) =>
        LiveAsync(c => c.DownloadAsync(relativePath, ct), Array.Empty<byte>(), ct);

    private async Task<T> LiveAsync<T>(Func<IJmsReader, Task<T>> call, T whenUnavailable, CancellationToken ct)
    {
        var client = await _getLiveClient(ct);
        return client is null ? whenUnavailable : await call(client);
    }

    public void Dispose() => _cache.Dispose();
}
