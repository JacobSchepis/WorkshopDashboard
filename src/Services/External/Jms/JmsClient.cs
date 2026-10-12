using Microsoft.Extensions.Logging.Abstractions;
using System.Globalization;
using System.Net;
using System.Text.Json;
using WorkshopDashboard.Abstract;
using WorkshopDashboard.Models;

namespace WorkshopDashboard.Services.External.Jms;

public enum JmsAuthResult
{
    Success,
    Rejected,
    Unreachable
}

// Every JMS address comes from JmsOptions (the Jms configuration section) — nothing in this class, or
// anywhere else in the app, names a JMS page directly. The raw senders are private so
// every call has to go through a named method here.
public sealed partial class JmsClient : IJmsDataClient, IDisposable
{
    private readonly JmsOptions _options;
    private readonly Uri _baseUri;
    private readonly CookieContainer _cookieJar = new();
    private readonly HttpMessageHandler _handler;
    private readonly HttpClient _http;
    private readonly ILogger _logger;

    private JmsEndpoints E => _options.Endpoints;

    public TimeSpan DefaultTimeout { get; set; }

    public JmsClient(JmsOptions options, JmsTransport? transport = null, ILogger<JmsClient>? logger = null)
    {
        _options = options;
        _logger = logger ?? NullLogger<JmsClient>.Instance;
        _baseUri = options.BaseUri;
        DefaultTimeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds);

        _handler = (transport ?? JmsTransport.Network).CreateHandler(_cookieJar);

        // Every send sets its own deadline (DefaultTimeout, or longer for slow reports), so
        // HttpClient's own 100-second cap mustn't cut those short.
        _http = new HttpClient(_handler)
        {
            BaseAddress = _baseUri,
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    public void Dispose()
    {
        _http.Dispose();
        _handler.Dispose();
    }

    // ---------------------------
    // Auth / session
    // ---------------------------

    public async Task<bool> Login(string username, string password, CancellationToken ct = default)
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["user_name"] = username,
            ["password"] = password,
            ["user_login_submit"] = "Login",
            ["latitude"] = _options.LoginLatitude,
            ["longitude"] = _options.LoginLongitude
        });

        await SendAsync(HttpMethod.Post, E.Login, form, ct);

        return !string.IsNullOrWhiteSpace(GetPhpSessionId());
    }

    public async Task<bool> VerifyAuthenticationAsync(CancellationToken ct = default) =>
        await CheckAuthAsync(ct) == JmsAuthResult.Success;

    /// <summary>Whether this client's session is logged in — telling "JMS says no" apart from
    /// "couldn't reach JMS", which a plain bool can't.</summary>
    public async Task<JmsAuthResult> CheckAuthAsync(CancellationToken ct = default)
    {
        try
        {
            var res = await SendAsync(HttpMethod.Get, E.AuthCheck, content: null, ct);
            _logger.LogDebug("Auth check returned {StatusCode} {ReasonPhrase}, {ContentLength} bytes", res.StatusCode, res.ReasonPhrase, res.ContentLength);

            // JMS bounces an unauthenticated session to its login page.
            if ((res.Url ?? "").Contains(E.Login, StringComparison.OrdinalIgnoreCase))
                return JmsAuthResult.Rejected;

            return res.Success ? JmsAuthResult.Success : JmsAuthResult.Unreachable;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("JMS auth check timed out after {Seconds}s or was cancelled", DefaultTimeout.TotalSeconds);
            return JmsAuthResult.Unreachable;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("JMS auth check failed: {Error}", ex.Message);
            return JmsAuthResult.Unreachable;
        }
    }

    /// <summary>Logs in and confirms it took. Rejected means JMS answered and refused the
    /// credentials; Unreachable means JMS couldn't be asked.</summary>
    public async Task<JmsAuthResult> TryLoginAsync(string username, string password, CancellationToken ct = default)
    {
        try
        {
            await Login(username, password, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            _logger.LogWarning("JMS login request failed: {Error}", ex.Message);
            return JmsAuthResult.Unreachable;
        }

        return await CheckAuthAsync(ct);
    }

    public string GetPhpSessionId()
    {
        var cookies = _cookieJar.GetCookies(_baseUri).Cast<Cookie>();
        return cookies.FirstOrDefault(c => c.Name.Equals("PHPSESSID", StringComparison.OrdinalIgnoreCase))?.Value ?? "";
    }

    public void SetPhpSessionId(string phpSessionId)
    {
        if (string.IsNullOrWhiteSpace(phpSessionId)) return;
        _cookieJar.Add(_baseUri, new Cookie("PHPSESSID", phpSessionId) { Path = "/" });
    }

    // ---------------------------
    // SQL query endpoint
    // ---------------------------

    /// <summary>The client itself never caches, so this is the same call.</summary>
    public Task<HtmlResponse> PostUncachedQueryForCsvAsync(string query) => PostQueryForCsvAsync(query);

    public async Task<HtmlResponse> PostQueryForCsvAsync(string query)
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            { "export_equipment", "Export" },
            { "query", query }
        });

        var res = await SendAsync(HttpMethod.Post, E.Query, form, CancellationToken.None);
        // The one place failed queries are logged — callers just fall back to an empty result.
        if (!res.Success)
            _logger.LogWarning("JMS query failed: {StatusCode} {ReasonPhrase}. Query: {Query}", res.StatusCode, res.ReasonPhrase, Abbreviate(query));
        return res;
    }

    private static string Abbreviate(string query)
    {
        var oneLine = string.Join(' ', query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return oneLine.Length <= 200 ? oneLine : oneLine[..200] + "…";
    }

    public async Task ExecuteStatementAsync(string statement) =>
        await PostQueryForCsvAsync(statement);

    /// <summary>
    /// Fetch a document PDF by POSTing its server path to the OpenFile page, the
    /// same way the site's "download quote" form does. Uses SendForBytesAsync
    /// so the session cookie is carried automatically.
    ///
    /// Returns null when the response doesn't look like a PDF — e.g. the
    /// session has expired and JMS returned an HTML login page with
    /// a 200 status — so callers never attach junk bytes as a .pdf.
    /// </summary>
    /// <param name="fileLocation">e.g. "/media/data/quote/quote-140105-1102.pdf"</param>
    /// <param name="type">OpenFile document type; "quote" for job quotes.</param>
    public async Task<byte[]?> GetQuotePdfAsync(string fileLocation, string type = "quote", CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(fileLocation))
            return null;

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["filename"] = fileLocation,
            ["type"] = type
        });

        var bytes = await SendForBytesAsync(HttpMethod.Post, E.OpenFile, form, ct);

        return LooksLikePdf(bytes) ? bytes : null;
    }

    /// <summary>True if the bytes begin with the PDF magic number "%PDF".</summary>
    private static bool LooksLikePdf(byte[] bytes) =>
        bytes.Length >= 4 && bytes[0] == 0x25 && bytes[1] == 0x50 && bytes[2] == 0x44 && bytes[3] == 0x46;

    public async Task<List<JmsUser>> SearchUsersAsync(string q, CancellationToken ct = default)
    {
        var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("q", q),
            new KeyValuePair<string, string>("table_value", "user")
        });

        var res = await SendAsync(HttpMethod.Post, E.UserSearch, form, ct);

        if (string.IsNullOrWhiteSpace(res.Content))
            return new List<JmsUser>();

        try
        {
            var users = JsonSerializer.Deserialize<List<JmsUserDto>>(
                res.Content,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (users is null) return new List<JmsUser>();

            return users
                .Where(u => u.user_id > 0)
                .Select(u => new JmsUser(u.user_id, u.first_name ?? "", u.last_name ?? ""))
                .Where(u => !string.IsNullOrWhiteSpace(u.FirstName) || !string.IsNullOrWhiteSpace(u.LastName))
                .ToList();
        }
        catch
        {
            return new List<JmsUser>();
        }
    }

    /// <summary>The next default labour start time for a given day — the exact same
    /// LabourConflictCheck ?action=get_last_time call the AddJobTimesheet page's own JS makes
    /// (fetchLastEndTime) to prefill its start-time picker. Deliberately used instead of
    /// reading labour.start_date/end_date directly via SQL: those columns come back from
    /// the query endpoint in a different timezone than what the site displays (10 hours off was
    /// observed — likely UTC vs AEST/AEDT storage), while this endpoint returns a value PHP
    /// itself already resolved to whatever the UI shows, so there's no offset to get wrong.
    /// Returns null if JMS reports no existing entry that day (caller should fall back to a
    /// sensible start-of-day default) or the call fails.</summary>
    public async Task<string?> GetLastLabourEndTimeAsync(DateTime date, CancellationToken ct = default)
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["action"] = "get_last_time",
            ["date"] = date.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture)
        });

        var res = await SendAsync(HttpMethod.Post, E.LabourConflictCheck, form, ct);
        if (!res.Success || string.IsNullOrWhiteSpace(res.Content)) return null;

        try
        {
            var payload = JsonSerializer.Deserialize<GetLastTimeResponse>(res.Content,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return payload?.Status == "success" ? payload.Time : null;
        }
        catch
        {
            return null;
        }
    }

    private sealed class GetLastTimeResponse
    {
        public string? Status { get; set; }
        public string? Time { get; set; }
    }

    // ---------------------------
    // Core sender
    // ---------------------------

    private async Task<byte[]> SendForBytesAsync(
        HttpMethod method,
        string endpoint,
        HttpContent? content,
        CancellationToken ct = default,
        TimeSpan? timeout = null)
    {
        endpoint = endpoint.TrimStart('/');
        using var req = new HttpRequestMessage(method, endpoint) { Content = content };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? DefaultTimeout);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsByteArrayAsync(cts.Token);
    }

    private async Task<HtmlResponse> SendAsync(
        HttpMethod method,
        string endpoint,
        HttpContent? content,
        CancellationToken ct = default)
    {
        endpoint = endpoint.TrimStart('/');

        using var req = new HttpRequestMessage(method, endpoint) { Content = content };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(DefaultTimeout);

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in resp.Headers) headers[h.Key] = string.Join(", ", h.Value);
        foreach (var h in resp.Content.Headers) headers[h.Key] = string.Join(", ", h.Value);

        var body = await resp.Content.ReadAsStringAsync(cts.Token);

        return new HtmlResponse(
            Success: resp.IsSuccessStatusCode,
            StatusCode: (int)resp.StatusCode,
            ReasonPhrase: resp.ReasonPhrase,
            Content: body,
            Url: resp.RequestMessage?.RequestUri?.ToString(),
            ContentType: resp.Content.Headers.ContentType?.ToString(),
            ContentLength: resp.Content.Headers.ContentLength,
            Headers: headers
        );
    }

    // ---------------------------
    // Models
    // ---------------------------

    private sealed class JmsUserDto
    {
        public string? first_name { get; set; }
        public string? last_name { get; set; }
        public int user_id { get; set; }
    }
}
