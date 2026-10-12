using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using WorkshopDashboard.Models;

namespace WorkshopDashboard.Services.External.Shipping
{
    /// <summary>
    /// Tracks DHL shipments via DHL's Unified Tracking API and returns the same
    /// TrackingResult shape TNT and JMLA do, so it can implement ICarrierTracker directly —
    /// no separate adapter needed (there used to be one, DhlCarrierTracker, back when this
    /// returned its own DhlTrackingResult type).
    /// </summary>
    public class DhlTrackingService : ICarrierTracker
    {
        private readonly HttpClient _httpClient;
        private readonly string _apiKey;
        private readonly ILogger<DhlTrackingService> _logger;

        // ---------------------------------------------------------------------------------
        // Minimal pacing. The page fires ~20 lookups at once; DHL's basic tier is 1/sec, so
        // without a little spacing a cold load is an instant wall of 429s that retries can't
        // dig out of. Static so the whole app shares one gate (the quota is per API key, not
        // per user). With TrackingService's cache in front, this only ever runs on a cache miss.
        //
        // Want pure retry with no pacing? Delete _gate / _nextAllowedUtc / _minInterval and
        // the WaitAsync / Delay / Release block in GetTrackingAsync.
        // ---------------------------------------------------------------------------------
        private static readonly SemaphoreSlim _gate = new(1, 1);
        private static readonly TimeSpan _minInterval = TimeSpan.FromMilliseconds(1100);
        private static DateTime _nextAllowedUtc = DateTime.MinValue;
        private const int MaxRetries = 4;

        public DhlTrackingService(HttpClient httpClient, IOptions<DhlOptions> options, ILogger<DhlTrackingService> logger)
        {
            _httpClient = httpClient;
            _logger = logger;
            _apiKey = options.Value.ApiKey;

            if (string.IsNullOrWhiteSpace(_apiKey))
                _logger.LogWarning("[DHL] Dhl:ApiKey isn't set — DHL lookups will report Missing API Key");
        }

        public string Carrier => "DHL";

        // Nothing to prime — DHL is looked up live per request (through TrackingService's cache).
        public Task PrimeAsync() => Task.CompletedTask;

        public async Task<TrackingResult> GetTrackingAsync(string trackingNumber)
        {
            if (string.IsNullOrWhiteSpace(trackingNumber)) return StatusResult(trackingNumber, "N/A");
            if (string.IsNullOrWhiteSpace(_apiKey)) return StatusResult(trackingNumber, "Missing API Key");

            // Production endpoint for the Shipment Tracking - Unified API.
            var url = $"https://api-eu.dhl.com/track/shipments?trackingNumber={Uri.EscapeDataString(trackingNumber.Trim())}";

            for (var attempt = 0; ; attempt++)
            {
                HttpResponseMessage? response = null;
                Exception? sendError = null;

                // Serialise + pace the outbound call.
                await _gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    var wait = _nextAllowedUtc - DateTime.UtcNow;
                    if (wait > TimeSpan.Zero) await Task.Delay(wait).ConfigureAwait(false);

                    // A fresh request per attempt — an HttpRequestMessage can't be sent twice.
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.Add("DHL-API-Key", _apiKey);

                    response = await _httpClient.SendAsync(request).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    sendError = ex;
                }
                finally
                {
                    // Measured from when the call finished, so a slow response doesn't let
                    // the next one fire immediately.
                    _nextAllowedUtc = DateTime.UtcNow + _minInterval;
                    _gate.Release();
                }

                if (sendError != null)
                {
                    _logger.LogError(sendError, "Exception thrown during DHL request for {TrackingNumber}", trackingNumber);
                    return StatusResult(trackingNumber, "API Offline");
                }

                // 429 -> wait a moment and try again (honouring Retry-After if DHL sends it).
                if (response!.StatusCode == HttpStatusCode.TooManyRequests && attempt < MaxRetries)
                {
                    var backoff = RetryAfter(response) ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));
                    response.Dispose();
                    _logger.LogInformation("DHL 429 for {TrackingNumber}, retrying in {Seconds:0.0}s (attempt {Attempt}).",
                        trackingNumber, backoff.TotalSeconds, attempt + 1);
                    await Task.Delay(backoff).ConfigureAwait(false);
                    continue;
                }

                using (response)
                {
                    var rawBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                    _logger.LogInformation("DHL response for {TrackingNumber}: HTTP {StatusCode}",
                        trackingNumber, (int)response.StatusCode);

                    if (response.StatusCode == HttpStatusCode.Unauthorized) return StatusResult(trackingNumber, "Unauthorized");
                    if (response.StatusCode == HttpStatusCode.NotFound) return StatusResult(trackingNumber, "Not Found");
                    if (response.StatusCode == HttpStatusCode.TooManyRequests) return StatusResult(trackingNumber, "Rate Limited");
                    if (!response.IsSuccessStatusCode) return StatusResult(trackingNumber, $"HTTP {(int)response.StatusCode}");

                    try
                    {
                        var parsed = JsonSerializer.Deserialize<DhlTrackingResponse>(rawBody);
                        var shipment = parsed?.Shipments is { Count: > 0 } ? parsed.Shipments[0] : null;
                        var status = shipment?.Status;

                        if (status == null) return StatusResult(trackingNumber, "Unknown Structure");

                        var description = !string.IsNullOrWhiteSpace(status.Description)
                            ? status.Description.Trim()
                            : (status.StatusCode ?? "Unknown");

                        // Earliest event in the history, if DHL included one — don't assume
                        // array order, take the true minimum by parsed timestamp.
                        DateTime? firstEventTimestamp = shipment?.Events
                            ?.Select(e => ParseTimestamp(e.Timestamp))
                            .Where(t => t.HasValue)
                            .Select(t => t!.Value)
                            .OrderBy(t => t)
                            .Cast<DateTime?>()
                            .FirstOrDefault();

                        var isDelivered = string.Equals(status.StatusCode, "delivered", StringComparison.OrdinalIgnoreCase)
                            || description.Contains("delivered", StringComparison.OrdinalIgnoreCase);

                        return new TrackingResult
                        {
                            TrackingNumber = trackingNumber,
                            Carrier = "DHL",
                            Status = description,
                            LastEventTime = ParseTimestamp(status.Timestamp),
                            FirstEventTime = firstEventTimestamp,
                            IsDelivered = isDelivered
                        };
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to parse DHL response for {TrackingNumber}", trackingNumber);
                        return StatusResult(trackingNumber, "Error");
                    }
                }
            }
        }

        private static TrackingResult StatusResult(string trackingNumber, string status) => new()
        {
            TrackingNumber = trackingNumber,
            Carrier = "DHL",
            Status = status
        };

        /// <summary>Retry-After is either delta-seconds or an HTTP date. Handle both.</summary>
        private static TimeSpan? RetryAfter(HttpResponseMessage response)
        {
            var header = response.Headers.RetryAfter;
            if (header is null) return null;
            if (header.Delta.HasValue) return header.Delta.Value;
            if (header.Date.HasValue)
            {
                var delta = header.Date.Value - DateTimeOffset.UtcNow;
                return delta > TimeSpan.Zero ? delta : TimeSpan.Zero;
            }
            return null;
        }

        // ISO strings with an offset (e.g. 2026-06-22T12:29:00-04:00) -> local time.
        private static DateTime? ParseTimestamp(string? ts)
        {
            if (string.IsNullOrWhiteSpace(ts)) return null;
            if (DateTimeOffset.TryParse(ts, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dto))
            {
                return dto.LocalDateTime;
            }
            return null;
        }
    }

    public class DhlTrackingResponse
    {
        [JsonPropertyName("shipments")]
        public List<DhlShipment>? Shipments { get; set; }
    }

    public class DhlShipment
    {
        [JsonPropertyName("status")]
        public DhlStatus? Status { get; set; }

        // DHL's Unified Tracking API includes the full event history here alongside the
        // current "status" snapshot. Used only to find the earliest event (when the
        // shipment was first booked/picked up) for the "in transit for" display — not
        // verified against a live response since this dev environment has no DHL API key,
        // so it degrades gracefully (null) if the shape turns out to differ.
        [JsonPropertyName("events")]
        public List<DhlStatus>? Events { get; set; }
    }

    public class DhlStatus
    {
        [JsonPropertyName("timestamp")]
        public string? Timestamp { get; set; }

        [JsonPropertyName("statusCode")]
        public string? StatusCode { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }
    }
}
