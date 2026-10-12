using Microsoft.Extensions.Options;
using WorkshopDashboard.Abstract;

namespace WorkshopDashboard.Services.External.Jms;

// ═══════════════════════════════════════════════════════════════════════════
//  JmsReadSession
//
//  The one app-wide JMS session every read goes through, logged in as the
//  service account from JmsOptions. Singleton, so reads don't depend on
//  whoever happens to be logged in — kiosks and background services use it
//  too. Writes never go through here; they run as the user (JmsUserSession).
//
//  Callers get a CachedJmsReader, which falls back to the last good result of
//  a query when JMS can't answer (see that class). Behind it, the live client
//  object never changes: when JMS expires the session, the same client logs in
//  again in place. The session is re-verified at most every few minutes rather
//  than on every call, and after a failed login it backs off briefly so a JMS
//  outage doesn't make every read wait out its own login timeout.
// ═══════════════════════════════════════════════════════════════════════════

public sealed class JmsReadSession : IDisposable
{
    private static readonly TimeSpan ReverifyAfter = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromSeconds(30);

    private readonly JmsServiceAccount _account;
    private readonly JmsClient _client;
    private readonly JmsStatus _status;
    private readonly CachedJmsReader _reader;
    private readonly ILogger<JmsReadSession> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private DateTime _verifiedAtUtc = DateTime.MinValue;
    private DateTime _failedAtUtc = DateTime.MinValue;

    public JmsReadSession(IOptions<JmsOptions> options, JmsStatus status, ILoggerFactory loggers, JmsTransport? transport = null)
    {
        _account = options.Value.ServiceAccount;
        _client = new JmsClient(options.Value, transport, loggers.CreateLogger<JmsClient>());
        _status = status;
        _reader = new CachedJmsReader(GetLiveClientAsync, status, loggers.CreateLogger<CachedJmsReader>());
        _logger = loggers.CreateLogger<JmsReadSession>();
    }

    private bool IsFresh => DateTime.UtcNow - _verifiedAtUtc < ReverifyAfter;
    private bool RecentlyFailed => DateTime.UtcNow - _failedAtUtc < RetryAfterFailure;

    /// <summary>The reader every read goes through. Always available — with JMS down it serves
    /// the last good result of each query, or empty results for anything it hasn't seen.</summary>
    public Task<IJmsReader?> GetClientAsync(CancellationToken ct = default) => Task.FromResult<IJmsReader?>(_reader);

    /// <summary>The live, logged-in client, or null if the service account can't get a session
    /// right now (JMS down, or the configured service account password is out of date).</summary>
    private async Task<IJmsReader?> GetLiveClientAsync(CancellationToken ct)
    {
        if (_status.IsUp == false) return null;
        if (IsFresh) return _client;
        if (RecentlyFailed) return null;

        await _lock.WaitAsync(ct);
        try
        {
            if (IsFresh) return _client;
            if (RecentlyFailed) return null;

            if (await _client.VerifyAuthenticationAsync(ct) || await TryLoginAsync(ct))
            {
                _verifiedAtUtc = DateTime.UtcNow;
                return _client;
            }

            _failedAtUtc = DateTime.UtcNow;
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<bool> TryLoginAsync(CancellationToken ct)
    {
        switch (await _client.TryLoginAsync(_account.Username, _account.Password, ct))
        {
            case JmsAuthResult.Success:
                return true;
            case JmsAuthResult.Rejected:
                _logger.LogError("Service account login was rejected — check the Jms:ServiceAccount settings");
                return false;
            default:
                _logger.LogWarning("Service account login failed — JMS unreachable");
                return false;
        }
    }

    public void Dispose()
    {
        _reader.Dispose();
        _client.Dispose();
        _lock.Dispose();
    }
}
