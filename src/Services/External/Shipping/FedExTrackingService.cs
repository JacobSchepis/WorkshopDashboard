using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using WorkshopDashboard.Models;

namespace WorkshopDashboard.Services.External.Shipping;

/// <summary>
/// Tracks FedEx shipments via the Track API v1 (OAuth2 client-credentials auth, one bearer
/// token per project, valid ~60 minutes — see FedEx's Authorization API). Credentials come
/// from FedExOptions (the FedEx configuration section).
/// ClientId/ClientSecret are the "API Key"/"Secret Key" shown on a project's Overview page at
/// developer.fedex.com (create a project there, add the Track API to it). UseSandbox picks
/// apis-sandbox.fedex.com vs apis.fedex.com for both the token and tracking calls — leave it
/// true until the account has production access approved.
///
/// Built from FedEx's public API docs, not yet exercised against a live account (no
/// credentials existed when this was written) — GetTrackingAsync logs the raw response body
/// at Info level on anything it can't parse into a known shape ("Unknown Structure"), so a
/// real sandbox response can be diffed against this quickly if a field name is off.
/// </summary>
public class FedExTrackingService : ICarrierTracker
{
    private readonly HttpClient _http;
    private readonly ILogger<FedExTrackingService> _logger;
    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly string _baseUrl;

    // OAuth token cache — one per process, refreshed a minute before FedEx's own ~60 minute
    // expiry so a lookup never fires on a token that's about to lapse mid-request.
    private static readonly TimeSpan ExpiryBuffer = TimeSpan.FromMinutes(1);
    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private string? _accessToken;
    private DateTime _tokenExpiresUtc = DateTime.MinValue;

    public FedExTrackingService(HttpClient http, IOptions<FedExOptions> options, ILogger<FedExTrackingService> logger)
    {
        _http = http;
        _logger = logger;

        var creds = options.Value;
        _clientId = creds.ClientId;
        _clientSecret = creds.ClientSecret;
        _baseUrl = creds.UseSandbox ? "https://apis-sandbox.fedex.com" : "https://apis.fedex.com";

        if (string.IsNullOrWhiteSpace(_clientId) || string.IsNullOrWhiteSpace(_clientSecret))
            _logger.LogWarning("[FedEx] FedEx:ClientId/ClientSecret aren't set");
    }

    public string Carrier => "FedEx";

    // Fetches the first OAuth token up front so the first real lookup isn't the one paying
    // for it — ShippingChecks-style pages fire many lookups in parallel right after this runs.
    public async Task PrimeAsync()
    {
        try
        {
            await EnsureTokenAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[FedEx] Exception priming OAuth token");
        }
    }

    public async Task<TrackingResult> GetTrackingAsync(string trackingNumber)
    {
        var result = new TrackingResult { TrackingNumber = trackingNumber, Carrier = "FedEx" };

        if (string.IsNullOrWhiteSpace(trackingNumber))
        {
            result.Status = "N/A";
            return result;
        }

        if (string.IsNullOrWhiteSpace(_clientId) || string.IsNullOrWhiteSpace(_clientSecret))
        {
            result.Status = "Missing API Key";
            return result;
        }

        string? rawBody = null;
        try
        {
            var token = await EnsureTokenAsync();
            if (token is null)
            {
                result.Status = "Unauthorized";
                result.ErrorMessage = "Could not obtain a FedEx OAuth token — check the FedEx settings";
                return result;
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/track/v1/trackingnumbers");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.TryAddWithoutValidation("X-locale", "en_US");

            // Tracking numbers are often typed/pasted with FedEx's own display grouping
            // (e.g. "1234 5678 9012") — the API wants the bare digits, not that formatting.
            var cleanedNumber = new string(trackingNumber.Where(c => !char.IsWhiteSpace(c)).ToArray());

            var body = new JsonObject
            {
                ["trackingInfo"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["trackingNumberInfo"] = new JsonObject { ["trackingNumber"] = cleanedNumber },
                        ["includeDetailedScans"] = true
                    }
                }
            };
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

            var response = await _http.SendAsync(request);
            rawBody = await response.Content.ReadAsStringAsync();

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                // Token might have been revoked/expired early server-side — drop the cache so
                // the next lookup fetches a fresh one instead of repeating the same failure.
                _accessToken = null;
                _logger.LogWarning("[FedEx] {TrackingNumber}: HTTP 401 from tracking call: {Body}", trackingNumber, rawBody);
                result.Status = "Unauthorized";
                result.ErrorMessage = rawBody;
                return result;
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("[FedEx] {TrackingNumber}: HTTP {StatusCode}: {Body}",
                    trackingNumber, (int)response.StatusCode, rawBody);
                result.Status = $"HTTP {(int)response.StatusCode}";
                return result;
            }

            var root = JsonNode.Parse(rawBody);
            var completeResult = AsNodeList(root?["output"]?["completeTrackResults"]).FirstOrDefault();
            var trackResult = AsNodeList(completeResult?["trackResults"]).FirstOrDefault();

            // FedEx doesn't consistently wrap a lone error in an array — a single tracking
            // number's "not found" comes back as a bare object, not [{ }] — so this reads
            // either shape instead of assuming one.
            var topLevelError = AsNodeList(root?["errors"]).FirstOrDefault();
            var resultLevelError = AsNodeList(trackResult?["error"] ?? completeResult?["error"]).FirstOrDefault();
            var anyError = resultLevelError ?? topLevelError;
            if (anyError != null)
            {
                result.Status = "Not Found";
                result.ErrorMessage = anyError["message"]?.GetValue<string>();
                return result;
            }

            if (trackResult == null)
            {
                _logger.LogInformation("[FedEx] {TrackingNumber} — unrecognised response shape: {Body}",
                    trackingNumber, rawBody);
                result.Status = "Unknown Structure";
                return result;
            }

            var statusDetail = trackResult["latestStatusDetail"];
            var statusCode = statusDetail?["code"]?.GetValue<string>();
            var statusDescription = statusDetail?["description"]?.GetValue<string>();

            result.Status = !string.IsNullOrWhiteSpace(statusDescription) ? statusDescription
                : !string.IsNullOrWhiteSpace(statusCode) ? statusCode
                : "Unknown";

            result.IsDelivered = string.Equals(statusCode, "DL", StringComparison.OrdinalIgnoreCase)
                || (statusDescription?.Contains("Delivered", StringComparison.OrdinalIgnoreCase) ?? false);

            // scanEvents isn't documented as ordered, so sort explicitly rather than assume [0]
            // is newest — same reasoning DHL/TNT already apply to their own event arrays.
            var scans = AsNodeList(trackResult["scanEvents"])
                .Select(e => (Node: (JsonNode?)e, Time: ParseScanTimestamp(e)))
                .Where(x => x.Time.HasValue)
                .OrderByDescending(x => x.Time)
                .ToList();

            var latestScan = scans.FirstOrDefault();
            if (latestScan.Node != null)
            {
                result.LastEvent = latestScan.Node["eventDescription"]?.GetValue<string>();
                result.LastEventTime = latestScan.Time;

                var loc = latestScan.Node["scanLocation"];
                var city = loc?["city"]?.GetValue<string>();
                var state = loc?["stateOrProvinceCode"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(city))
                    result.LastLocation = string.IsNullOrWhiteSpace(state) ? city : $"{city}, {state}";
            }

            if (scans.Count > 0)
                result.FirstEventTime = scans.Min(x => x.Time);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[FedEx] Exception looking up {TrackingNumber}: {Body}", trackingNumber, rawBody);
            result.Status = "Error";
            result.ErrorMessage = ex.Message;
        }

        return result;
    }

    // Wraps FedEx's inconsistent array-vs-bare-object fields (seen so far on "error";
    // may show up elsewhere) into a uniform list instead of assuming either shape and
    // throwing on the other.
    private static List<JsonNode> AsNodeList(JsonNode? node) => node switch
    {
        null => new List<JsonNode>(),
        JsonArray arr => arr.Where(n => n != null).Select(n => n!).ToList(),
        _ => new List<JsonNode> { node }
    };

    // ── OAuth token ──────────────────────────────────────────────────────────

    private async Task<string?> EnsureTokenAsync()
    {
        if (_accessToken != null && DateTime.UtcNow < _tokenExpiresUtc - ExpiryBuffer)
            return _accessToken;

        await _tokenGate.WaitAsync();
        try
        {
            // Another caller may have already refreshed it while this one waited on the gate.
            if (_accessToken != null && DateTime.UtcNow < _tokenExpiresUtc - ExpiryBuffer)
                return _accessToken;

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/oauth/token")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    { "grant_type", "client_credentials" },
                    { "client_id", _clientId },
                    { "client_secret", _clientSecret }
                })
            };

            var response = await _http.SendAsync(request);
            var rawBody = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("[FedEx] OAuth token request failed: HTTP {StatusCode}: {Body}",
                    (int)response.StatusCode, rawBody);
                return null;
            }

            var parsed = JsonNode.Parse(rawBody);
            var token = parsed?["access_token"]?.GetValue<string>();
            var expiresIn = parsed?["expires_in"]?.GetValue<int>() ?? 0;

            if (string.IsNullOrWhiteSpace(token))
            {
                _logger.LogError("[FedEx] OAuth response had no access_token: {Body}", rawBody);
                return null;
            }

            _accessToken = token;
            _tokenExpiresUtc = DateTime.UtcNow.AddSeconds(expiresIn > 0 ? expiresIn : 3600);
            _logger.LogInformation("[FedEx] OAuth token acquired, expires {ExpiresUtc:u}", _tokenExpiresUtc);

            return _accessToken;
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    private static DateTime? ParseScanTimestamp(JsonNode? scan)
    {
        var date = scan?["date"]?.GetValue<string>();
        var time = scan?["time"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(date)) return null;

        var combined = string.IsNullOrWhiteSpace(time) ? date : $"{date} {time}";
        return DateTime.TryParse(combined, out var dt) ? dt : null;
    }
}
