using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using WorkshopDashboard.Models;

namespace WorkshopDashboard.Services.External.Shipping;

/// <summary>
/// Tracks JMLA consignments via the Transvirtual portal. Logs in once, bulk-fetches
/// every recent consignment into a cache, then serves per-connote lookups locally.
/// Because it holds that cache, the instance is long-lived (register as a singleton).
/// Credentials come from JmlaOptions (the Jmla configuration section).
/// </summary>
public class JmlaTrackingService : ICarrierTracker
{
    private const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";

    private readonly HttpClient _http;
    private readonly ILogger<JmlaTrackingService> _logger;
    private readonly string _jmlaUsername;
    private readonly string _jmlaPassword;
    private readonly string _jmlaAccountId;

    // Login once, fetch all rows once, look up locally per connote. Re-primed at most
    // once per Ttl — without this, PrimeAsync (called from OnAfterRenderAsync on every
    // page reload) would log in and refetch the whole portal every single reload.
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(24);
    private string? _jmlaToken;
    private Dictionary<string, JsonNode>? _jmlaCache; // consignmentNumber → cell[]
    private DateTime? _lastPrimedUtc;

    public JmlaTrackingService(HttpClient http, IOptions<JmlaOptions> options, ILogger<JmlaTrackingService> logger)
    {
        _http = http;
        _logger = logger;
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", BrowserUserAgent);

        _jmlaUsername = options.Value.Username;
        _jmlaPassword = options.Value.Password;
        _jmlaAccountId = options.Value.AccountId;

        if (string.IsNullOrWhiteSpace(_jmlaUsername))
            _logger.LogWarning("[JMLA] Jmla:Username isn't set");
        if (string.IsNullOrWhiteSpace(_jmlaPassword))
            _logger.LogWarning("[JMLA] Jmla:Password isn't set");
        if (string.IsNullOrWhiteSpace(_jmlaAccountId))
            _logger.LogWarning("[JMLA] Jmla:AccountId isn't set");
    }

    public string Carrier => "JMLA";

    /// <summary>
    /// Logs in, fetches all recent consignments, and caches them keyed by consignment
    /// number so per-job lookups are instant. Safe to call once at startup.
    /// </summary>
    public async Task PrimeAsync()
    {
        if (_jmlaCache != null && _lastPrimedUtc.HasValue && DateTime.UtcNow - _lastPrimedUtc.Value < Ttl)
        {
            _logger.LogInformation("[JMLA] Cache still fresh (primed {Minutes:0}m ago) — skipping re-prime",
                (DateTime.UtcNow - _lastPrimedUtc.Value).TotalMinutes);
            return;
        }

        try
        {
            _logger.LogInformation("[JMLA] Priming cache — logging in");
            await JmlaLoginAsync();

            if (_jmlaToken == null)
            {
                _logger.LogError("[JMLA] Login failed — no TvAuthToken received. Check the Jmla settings");
                return;
            }

            _logger.LogInformation("[JMLA] Login succeeded, fetching all consignments");
            if (await FetchAndCacheAllJmlaConsignmentsAsync())
                _lastPrimedUtc = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[JMLA] Exception during cache prime");
        }
    }

    // Answered from the cache PrimeCacheAsync fills, so there's nothing to await.
    public Task<TrackingResult> GetTrackingAsync(string trackingNumber) => Task.FromResult(GetTracking(trackingNumber));

    private TrackingResult GetTracking(string trackingNumber)
    {
        var result = new TrackingResult { TrackingNumber = trackingNumber, Carrier = "JMLA" };

        try
        {
            if (_jmlaCache == null)
            {
                _logger.LogWarning("[JMLA] Cache not primed yet for {TrackingNumber} — cache is null", trackingNumber);
                result.Status = "Not ready";
                result.ErrorMessage = "JMLA cache not loaded";
                return result;
            }

            if (!_jmlaCache.TryGetValue(trackingNumber, out var data))
            {
                _logger.LogWarning("[JMLA] {TrackingNumber} not found in cache ({Count} entries)", trackingNumber, _jmlaCache.Count);
                result.Status = "Not found";
                return result;
            }

            _logger.LogInformation("[JMLA] Cache hit for {TrackingNumber}: {Cell}", trackingNumber, data.ToJsonString());

            // cell[] layout — see FetchAndCacheAllJmlaConsignmentsAsync
            var rawStatus = StripHtml(data[6]?.GetValue<string>()) ?? "Unknown";

            result.Status = rawStatus switch
            {
                "PickupReceived" => "Picked Up",
                "InTransit" => "In Transit",
                "OutForDelivery" => "Out for Delivery",
                "Delivered" => "Delivered",
                "DeliveryAttempted" => "Delivery Attempted",
                "Exception" => "Exception",
                _ => rawStatus
            };
            result.IsDelivered = rawStatus.Equals("Delivered", StringComparison.OrdinalIgnoreCase);

            var suburb = StripHtml(data[5]?.GetValue<string>());
            if (!string.IsNullOrWhiteSpace(suburb))
                result.LastLocation = suburb;

            var dateStr = StripHtml(data[2]?.GetValue<string>());
            if (DateTime.TryParseExact(dateStr, "d/MM/yyyy",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var dt))
                result.LastEventTime = dt;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[JMLA] Exception looking up {TrackingNumber}", trackingNumber);
            result.Status = "Error";
            result.ErrorMessage = ex.Message;
        }

        return result;
    }

    /// <summary>
    /// POSTs credentials to Transvirtual and stores the returned TvAuthToken cookie.
    /// </summary>
    private async Task JmlaLoginAsync()
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            { "UserNameLogin", _jmlaUsername },
            { "PasswordLogin", _jmlaPassword },
            { "RememberMe",    "false" }
        });

        // Must disable auto-redirect — HttpClient drops Set-Cookie when following redirects
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var loginHttp = new HttpClient(handler);
        loginHttp.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", BrowserUserAgent);

        var response = await loginHttp.PostAsync(
            "https://portal.transvirtual.com/Public/Home/LoginCommit", form);

        _logger.LogInformation("[JMLA] Login response: {StatusCode}", (int)response.StatusCode);

        if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            foreach (var cookie in cookies)
            {
                var match = Regex.Match(cookie, @"TvAuthToken=([^;]+)");
                if (match.Success)
                {
                    _jmlaToken = match.Groups[1].Value;
                    _logger.LogInformation("[JMLA] TvAuthToken captured successfully");
                    return;
                }
            }
            _logger.LogWarning("[JMLA] Set-Cookie present but no TvAuthToken found. Cookies: {Cookies}",
                string.Join(" | ", cookies));
        }
        else
        {
            _logger.LogWarning("[JMLA] No Set-Cookie in login response. All headers: {Headers}",
                string.Join(", ", response.Headers.Select(h => h.Key)));
        }
    }

    /// <summary>
    /// Fetches all recent consignments in one request and stores them in _jmlaCache.
    ///
    /// cell[] layout (jqGrid positional):
    ///   [0]  row selector (empty)
    ///   [1]  consignment number
    ///   [2]  date (d/MM/yyyy)
    ///   [3]  sender name
    ///   [4]  receiver name
    ///   [5]  destination suburb
    ///   [6]  status (e.g. "PickupReceived", "Delivered")
    ///   [7]  pieces
    ///   [8]  weight
    ///   [9-11] cubic / extras
    /// </summary>
    private async Task<bool> FetchAndCacheAllJmlaConsignmentsAsync()
    {
        var url = "https://portal.transvirtual.com/Portal/Consignment/ConsignmentListGrid" +
                  "?_search=false&rows=1000&page=1&sidx=&sord=desc&dateRange=" +
                  $"&idSearchQueries={Uri.EscapeDataString(_jmlaAccountId)}&ConsignmentNumber2=";

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Cookie", $"TvAuthToken={_jmlaToken}");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");

        var response = await _http.SendAsync(request);

        _logger.LogInformation("[JMLA] ConsignmentListGrid response: {StatusCode}", (int)response.StatusCode);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("[JMLA] Non-success {StatusCode}", (int)response.StatusCode);
            return false;
        }

        var json = await response.Content.ReadAsStringAsync();
        _logger.LogInformation("[JMLA] Response preview: {Json}", json[..Math.Min(json.Length, 300)]);

        var root = JsonNode.Parse(json);
        var rows = root?["rows"]?.AsArray();

        if (rows == null)
        {
            _logger.LogWarning("[JMLA] No 'rows' array in response");
            return false;
        }

        _jmlaCache = new Dictionary<string, JsonNode>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            var cell = row?["cell"];
            var conNum = StripHtml(cell?[1]?.GetValue<string>());
            if (!string.IsNullOrWhiteSpace(conNum) && cell != null)
                _jmlaCache[conNum] = cell;
        }

        _logger.LogInformation("[JMLA] Cache primed with {Count} consignments", _jmlaCache.Count);
        return true;
    }

    /// <summary>Strips HTML tags from a string returned by Transvirtual's jqGrid cells.</summary>
    private static string? StripHtml(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return input;
        return Regex.Replace(input, @"<[^>]+>", "").Trim();
    }
}
