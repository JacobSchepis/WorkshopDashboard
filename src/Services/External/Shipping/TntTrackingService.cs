using System.Net.Http;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using WorkshopDashboard.Models;

namespace WorkshopDashboard.Services.External.Shipping;

/// <summary>
/// Tracks TNT consignments via TNT's public shipment API. Stateless — no login,
/// no cache of its own — so <see cref="PrimeAsync"/> is a no-op. Caching (a coordinator
/// concern, not this client's job) lives in TrackingService.
/// </summary>
public class TntTrackingService : ICarrierTracker
{
    private const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";

    private readonly HttpClient _http;
    private readonly ILogger<TntTrackingService> _logger;

    public TntTrackingService(HttpClient http, ILogger<TntTrackingService> logger)
    {
        _http = http;
        _logger = logger;

        // Mimic a browser request so TNT doesn't reject it.
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Referer",
            "https://www.tnt.com/express/en_au/site/shipping-tools/tracking.html");
    }

    public string Carrier => "TNT";

    // Nothing to prime — TNT is looked up live per request.
    public Task PrimeAsync() => Task.CompletedTask;

    public async Task<TrackingResult> GetTrackingAsync(string trackingNumber)
    {
        var result = new TrackingResult { TrackingNumber = trackingNumber, Carrier = "TNT" };

        try
        {
            var url = $"https://www.tnt.com/api/v3/shipment" +
                      $"?con={Uri.EscapeDataString(trackingNumber)}" +
                      $"&searchType=con&respLang=en&respCountry=au";

            var response = await _http.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                result.Status = "Unavailable";
                result.ErrorMessage = $"TNT returned {(int)response.StatusCode}";
                return result;
            }

            var json = await response.Content.ReadAsStringAsync();
            var root = JsonNode.Parse(json);

            // Pick the consignment that originated from our depot (CompanyProfile.DepotSuburb).
            // TNT reuses consignment numbers every few weeks, so there may be
            // multiple results — the one from our depot is always ours.
            var allCons = root?["tracker.output"]?["consignment"]?.AsArray();

            var cons = allCons?
                .FirstOrDefault(c => c?["originAddress"]?["city"]
                    ?.GetValue<string>()
                    ?.Equals(CompanyProfile.DepotSuburb, StringComparison.OrdinalIgnoreCase) == true)
                ?? allCons?.FirstOrDefault(); // fall back to first if none match

            if (cons == null)
            {
                result.Status = "Not found";
                return result;
            }

            // Delivered flags
            var status = cons["status"];
            result.IsDelivered = status?["isDelivered"]?.GetValue<bool>() ?? false;

            // Map groupCode to a human-readable status label
            var groupCode = status?["groupCode"]?.GetValue<string>() ?? "";
            result.Status = groupCode switch
            {
                "BOOKIN" => "Booked",
                "INTRAN" => "In Transit",
                "OUTPCK" => "Out for Delivery",
                "DELRED" => "Delivered",
                "NODELI" => "Delivery Attempted",
                "DELEXT" => "Delivery Exception",
                "CNCLED" => "Cancelled",
                _ => "In Transit"   // sensible default for unknown codes
            };

            // Latest event — kept for the timestamp and location
            var events = cons["events"]?.AsArray();
            var topEvent = events?.FirstOrDefault();

            if (topEvent != null)
            {
                result.LastEvent = topEvent["statusDescription"]?.GetValue<string>();

                // Show the delivery destination address, not the event location
                var destCity = cons["destinationAddress"]?["city"]?.GetValue<string>();
                var destCountry = cons["destinationAddress"]?["country"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(destCity))
                    result.LastLocation = $"{destCity}, {destCountry}";

                if (DateTime.TryParse(topEvent["date"]?.GetValue<string>(), out var dt))
                    result.LastEventTime = dt;
            }

            // Earliest event in the history — used to show how long a non-delivered shipment has
            // been in transit. Don't assume the array's order; take the true minimum by date.
            if (events != null)
            {
                var eventDates = events
                    .Select(e => DateTime.TryParse(e?["date"]?.GetValue<string>(), out var d) ? d : (DateTime?)null)
                    .Where(d => d.HasValue)
                    .Select(d => d!.Value)
                    .ToList();

                if (eventDates.Count > 0)
                    result.FirstEventTime = eventDates.Min();
            }
        }
        catch (Exception ex)
        {
            result.Status = "Error";
            result.ErrorMessage = ex.Message;
        }

        return result;
    }
}
