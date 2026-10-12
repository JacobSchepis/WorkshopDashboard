using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using WorkshopDashboard.Abstract;
using WorkshopDashboard.Services.Accounts;

namespace WorkshopDashboard.Services.External.Jms;

public enum JmsUserSessionState
{
    Connected,
    /// <summary>JMS couldn't be reached — nothing the user can do but wait.</summary>
    JmsUnavailable,
    /// <summary>No usable stored password: never saved, rejected by JMS (changed there), or
    /// can't be decrypted. The user needs to enter it again (/jms-reconnect).</summary>
    PasswordNeeded
}

// ═══════════════════════════════════════════════════════════════════════════
//  JmsUserSessionPool
//
//  One JMS session per logged-in user, shared by all their tabs/circuits and
//  keyed by username. Replaces the old PHPSESSID-keyed JmsClientManager: the
//  session is no longer tied to a cookie claim, so when JMS expires it, the
//  pool logs the same client back in using the user's stored password
//  (JmsCredentialStore) instead of logging them out of the app.
//
//  Same shape as JmsReadSession: a verified session is trusted for a few
//  minutes before being checked again, and each user's check/re-login is
//  serialized so several page loads don't all log in at once.
// ═══════════════════════════════════════════════════════════════════════════

public sealed class JmsUserSessionPool
{
    private static readonly TimeSpan ReverifyAfter = TimeSpan.FromMinutes(5);

    private sealed class Entry
    {
        public required JmsClient Client { get; set; }
        public SemaphoreSlim Lock { get; } = new(1, 1);
        public DateTime VerifiedAtUtc { get; set; } = DateTime.MinValue;
    }

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly JmsOptions _options;
    private readonly JmsCredentialStore _credentials;
    private readonly JmsTransport? _transport;
    private readonly ILogger<JmsClient> _clientLogger;

    public JmsUserSessionPool(IOptions<JmsOptions> options, JmsCredentialStore credentials, ILogger<JmsClient> clientLogger, JmsTransport? transport = null)
    {
        _options = options.Value;
        _credentials = credentials;
        _transport = transport;
        _clientLogger = clientLogger;
    }

    /// <summary>Hands over a client that has just logged in (at app login or reconnect), so
    /// the first page doesn't log in a second time.</summary>
    public void Register(string username, JmsClient client)
    {
        var entry = _entries.GetOrAdd(username, _ => new Entry { Client = client });
        entry.Client = client;
        entry.VerifiedAtUtc = DateTime.UtcNow;
    }

    public void Remove(string username) => _entries.TryRemove(username, out _);

    /// <summary>The user's JMS client, logged in — re-logging in with their stored password if
    /// JMS has expired the session. <paramref name="forceCheck"/> skips the few-minute trust
    /// window (the nav bar's indicator uses it to notice an expiry promptly).</summary>
    public async Task<(IJmsDataClient? Client, JmsUserSessionState State)> GetAsync(string username, bool forceCheck = false)
    {
        var entry = _entries.GetOrAdd(username, _ => new Entry { Client = new JmsClient(_options, _transport, _clientLogger) });
        if (!forceCheck && IsFresh(entry)) return (entry.Client, JmsUserSessionState.Connected);

        await entry.Lock.WaitAsync();
        try
        {
            if (!forceCheck && IsFresh(entry)) return (entry.Client, JmsUserSessionState.Connected);

            var state = await EnsureLoggedInAsync(username, entry.Client);
            if (state == JmsUserSessionState.Connected)
            {
                entry.VerifiedAtUtc = DateTime.UtcNow;
                return (entry.Client, state);
            }

            entry.VerifiedAtUtc = DateTime.MinValue;
            return (null, state);
        }
        finally
        {
            entry.Lock.Release();
        }
    }

    private async Task<JmsUserSessionState> EnsureLoggedInAsync(string username, JmsClient client)
    {
        switch (await client.CheckAuthAsync())
        {
            case JmsAuthResult.Success: return JmsUserSessionState.Connected;
            case JmsAuthResult.Unreachable: return JmsUserSessionState.JmsUnavailable;
        }

        // Session expired — log back in with the stored password.
        var password = await _credentials.GetJmsPasswordAsync(username);
        if (password is null) return JmsUserSessionState.PasswordNeeded;

        return await client.TryLoginAsync(username, password) switch
        {
            JmsAuthResult.Success => JmsUserSessionState.Connected,
            JmsAuthResult.Unreachable => JmsUserSessionState.JmsUnavailable,
            _ => JmsUserSessionState.PasswordNeeded // password changed in JMS since it was saved
        };
    }

    private static bool IsFresh(Entry entry) => DateTime.UtcNow - entry.VerifiedAtUtc < ReverifyAfter;
}
