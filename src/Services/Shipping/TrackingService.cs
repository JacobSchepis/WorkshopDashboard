using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using WorkshopDashboard.Data;
using WorkshopDashboard.Models;
using WorkshopDashboard.Services.External.Shipping;

namespace WorkshopDashboard.Services.Shipping;

/// <summary>
/// Detects the carrier from free text and routes each lookup to the matching
/// <see cref="ICarrierTracker"/>. Register one tracker per carrier in DI and this
/// coordinator picks the right one at runtime.
///
/// Also owns the database-backed cache (TrackingCache table) for carriers whose lookup is a live network call
/// (DHL, TNT) — this used to live in a FileCached* wrapper around each raw client, which
/// meant two near-identical caching implementations with two independently-drifting
/// "don't cache this" lists. Caching is a coordinator concern, not something a raw API
/// client should know about, so it lives here instead. JMLA is deliberately excluded — it
/// already bulk-fetches and caches every consignment in memory itself (see
/// JmlaTrackingService.PrimeAsync); adding another cache layer on top would just serve
/// stale data for up to a day after JMLA's own cache refreshes.
/// </summary>
public class TrackingService
{
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(24);

    // Carriers whose raw lookup is a live network call worth caching. JMLA manages its
    // own freshness (see class doc comment above) so it's intentionally left out here.
    private static readonly HashSet<string> Cacheable = new(StringComparer.OrdinalIgnoreCase) { "DHL", "TNT" };

    // Results that describe OUR failure rather than the parcel's real state. Caching
    // these would poison a row for a whole day off one blip, so they're never stored
    // and simply get re-looked-up next load. Union of what DhlTrackingService and
    // TntTrackingService each return on failure.
    private static readonly HashSet<string> DoNotCache = new(StringComparer.OrdinalIgnoreCase)
    {
        "N/A", "Missing API Key", "Unauthorized", "Not Found", "Not found",
        "Unavailable", "API Offline", "Error", "Rate Limited", "Unknown Structure"
    };

    private readonly Dictionary<string, ICarrierTracker> _trackers;
    private readonly ILogger<TrackingService> _logger;
    private readonly IDbContextFactory<WorkshopDbContext> _dbFactory;

    private readonly object _lock = new();
    private Dictionary<string, TrackingCacheEntry>? _entries;

    public TrackingService(IEnumerable<ICarrierTracker> trackers, IDbContextFactory<WorkshopDbContext> dbFactory, ILogger<TrackingService> logger)
    {
        _trackers = trackers.ToDictionary(t => t.Carrier, StringComparer.OrdinalIgnoreCase);
        _logger = logger;
        _dbFactory = dbFactory;
    }

    // ── Carrier detection ─────────────────────────────────────────────────────
    // The single source of truth for "what carrier/number is this text talking
    // about" — used for job status connotes/notes, Monday board Connote columns,
    // AND job "service" tracking entries (see WaitingWorkshopAssignmentQuery),
    // so all three stop guessing independently.
    //
    // An explicit "<Carrier>: <number>" prefix (how job service entries record
    // it, e.g. "DHL: 1234567890") is always trusted over the digit-length guess
    // below. Bare text with no prefix (connotes/notes/Monday columns) falls
    // through to the digit-length heuristic.
    // Extend patterns here once you have real JMLA tracking number examples.

    // Public so every "pick a carrier" UI (InternationalShipping's Book Shipment dropdown,
    // WorkshopFinalisationComplete's manual-entry dropdown) and the tracking_raw SQL filter
    // in JmsQueries read the same list — one place to add a carrier, not four.
    public static readonly string[] KnownCarrierPrefixes = { "DHL", "TNT", "JMLA", "Smartways", "FedEx" };

    public static (string? trackingNumber, string? carrier) ExtractTracking(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return (null, null);

        var trimmed = text.Trim();

        foreach (var carrier in KnownCarrierPrefixes)
        {
            if (trimmed.StartsWith(carrier + ":", StringComparison.OrdinalIgnoreCase))
            {
                var value = trimmed[(carrier.Length + 1)..].Trim();
                return value.Length > 0 ? (value, carrier) : (null, null);
            }
        }

        // TNT: exactly 9 digits
        var tnt = Regex.Match(trimmed, @"\b(\d{9})\b");
        if (tnt.Success)
            return (tnt.Groups[1].Value, "TNT");

        // Smartways: exactly 8 digits — checked before JMLA to avoid mismatches
        var smartways = Regex.Match(trimmed, @"\b(\d{8})\b");
        if (smartways.Success)
            return (smartways.Groups[1].Value, "Smartways");

        // JMLA: exactly 5 digits
        var jmla = Regex.Match(trimmed, @"\b(\d{5})\b");
        if (jmla.Success)
            return (jmla.Groups[1].Value, "JMLA");

        return (null, null);
    }

    // ── Startup ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Primes every carrier that needs it (JMLA logs in and bulk-caches; TNT/DHL no-op).
    /// Call this once at startup — replaces the old PrimeJmlaCacheAsync().
    /// </summary>
    public Task PrimeAllAsync() =>
        Task.WhenAll(_trackers.Values.Select(t => t.PrimeAsync()));

    // ── Lookup ────────────────────────────────────────────────────────────────

    public Task<TrackingResult> GetTrackingAsync(string trackingNumber, string carrier)
    {
        if (!_trackers.TryGetValue(carrier, out var tracker))
        {
            return Task.FromResult(new TrackingResult
            {
                TrackingNumber = trackingNumber,
                Carrier = carrier,
                Status = "Unsupported carrier"
            });
        }

        return Cacheable.Contains(carrier)
            ? GetCachedAsync(trackingNumber, carrier, tracker)
            : tracker.GetTrackingAsync(trackingNumber);
    }

    // Text -> tracking result in one call: tries each connote in order (keeping the last
    // one that yields a tracking number, so the most recent shipment entry wins), then
    // falls back to general job notes. Used by every page that shows a job's live
    // shipping status instead of each re-implementing extract-then-lookup itself.
    public async Task<TrackingResult> GetJobTrackingAsync(IEnumerable<string?> connotes, string? notes)
    {
        string? trackingNumber = null;
        string? carrier = null;

        foreach (var connote in connotes)
        {
            var (tn, c) = ExtractTracking(connote);
            if (tn != null)
            {
                trackingNumber = tn;
                carrier = c;
            }
        }

        if (trackingNumber == null && !string.IsNullOrWhiteSpace(notes))
            (trackingNumber, carrier) = ExtractTracking(notes);

        if (trackingNumber == null || carrier == null)
            return new TrackingResult { Status = "—", TrackingNumber = null };

        return await GetTrackingAsync(trackingNumber, carrier);
    }

    // ── Cache (DHL / TNT only — see Cacheable) ──────────────────────────────────

    private async Task<TrackingResult> GetCachedAsync(string trackingNumber, string carrier, ICarrierTracker tracker)
    {
        if (string.IsNullOrWhiteSpace(trackingNumber))
            return await tracker.GetTrackingAsync(trackingNumber).ConfigureAwait(false);

        var key = $"{carrier}:{trackingNumber.Trim()}";

        if (TryGetFresh(key, out var cached))
            return cached;

        var result = await tracker.GetTrackingAsync(trackingNumber).ConfigureAwait(false);

        if (IsCacheable(result))
            Store(key, result);

        return result;
    }

    private bool TryGetFresh(string key, out TrackingResult result)
    {
        result = null!;
        lock (_lock)
        {
            EnsureLoaded();
            if (_entries!.TryGetValue(key, out var entry)
                && DateTime.UtcNow - entry.FetchedUtc < Ttl)
            {
                result = CacheEntry.ToResult(entry);
                return true;
            }
        }
        return false;
    }

    private void Store(string key, TrackingResult result)
    {
        lock (_lock)
        {
            EnsureLoaded();
            _entries![key] = CacheEntry.From(key, result);
            Save(_entries[key]);
        }
    }

    private static bool IsCacheable(TrackingResult? result)
    {
        if (result is null) return false;
        var status = (result.Status ?? "").Trim();
        if (status.Length == 0) return false;
        if (status.StartsWith("HTTP", StringComparison.OrdinalIgnoreCase)) return false;
        return !DoNotCache.Contains(status);
    }

    // --- database I/O (always called under _lock) ---

    // The whole cache is loaded once and kept in memory; each new result is written through
    // as a single row.
    private void EnsureLoaded()
    {
        if (_entries != null) return;

        try
        {
            using var db = _dbFactory.CreateDbContext();
            _entries = db.TrackingCache.AsNoTracking().ToDictionary(e => e.Key);
            return;
        }
        catch (Exception ex)
        {
            // An unreadable cache is not fatal — start clean.
            _logger.LogWarning(ex, "Could not read tracking cache; starting empty.");
        }

        _entries = new Dictionary<string, TrackingCacheEntry>();
    }

    private void Save(TrackingCacheEntry entry)
    {
        try
        {
            using var db = _dbFactory.CreateDbContext();
            var existing = db.TrackingCache.Find(entry.Key);
            if (existing is null) db.TrackingCache.Add(entry);
            else db.Entry(existing).CurrentValues.SetValues(entry);
            db.SaveChanges();
        }
        catch (Exception ex)
        {
            // A failed write just means we re-fetch next time — not worth throwing over.
            _logger.LogWarning(ex, "Could not write tracking cache entry {Key}.", entry.Key);
        }
    }

    private static class CacheEntry
    {
        public static TrackingCacheEntry From(string key, TrackingResult r) => new()
        {
            Key = key,
            TrackingNumber = r.TrackingNumber,
            Carrier = r.Carrier,
            Status = r.Status,
            LastEvent = r.LastEvent,
            LastLocation = r.LastLocation,
            LastEventTime = r.LastEventTime,
            FirstEventTime = r.FirstEventTime,
            IsDelivered = r.IsDelivered,
            ErrorMessage = r.ErrorMessage,
            FetchedUtc = DateTime.UtcNow
        };

        public static TrackingResult ToResult(TrackingCacheEntry e) => new()
        {
            TrackingNumber = e.TrackingNumber,
            Carrier = e.Carrier,
            Status = e.Status,
            LastEvent = e.LastEvent,
            LastLocation = e.LastLocation,
            LastEventTime = e.LastEventTime,
            FirstEventTime = e.FirstEventTime,
            IsDelivered = e.IsDelivered,
            ErrorMessage = e.ErrorMessage
        };
    }
}
