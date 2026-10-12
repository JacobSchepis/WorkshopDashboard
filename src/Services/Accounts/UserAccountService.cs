using Microsoft.Extensions.Options;
using WorkshopDashboard.Services.External.Jms;

namespace WorkshopDashboard.Services.Accounts;

public enum AppLoginResult
{
    /// <summary>JMS accepted the credentials; the user's JMS session is ready.</summary>
    Success,
    /// <summary>JMS couldn't be reached, but the password matches the one saved at their last
    /// successful login — they're in, and their JMS session will start once JMS is back.</summary>
    SuccessOffline,
    InvalidCredentials,
    /// <summary>JMS couldn't be reached and there's no saved password to fall back on (first
    /// login on this app, or a different password from last time).</summary>
    JmsUnavailable
}

// ═══════════════════════════════════════════════════════════════════════════
//  UserAccountService
//
//  The rules for logging into this app:
//    1. JMS is asked first whenever it can be — it stays the source of truth,
//       so a changed password or a disabled JMS account takes effect here
//       straight away. A success saves the password (JmsCredentialStore) and
//       hands the logged-in JMS client to JmsUserSessionPool.
//    2. Only when JMS can't be reached does the saved hash decide, so people
//       can still use the app during a JMS outage.
//  The first ever login for a user therefore always needs JMS up — that's
//  what proves the account is real.
// ═══════════════════════════════════════════════════════════════════════════

public class UserAccountService
{
    private readonly JmsOptions _jmsOptions;
    private readonly JmsCredentialStore _credentials;
    private readonly JmsUserSessionPool _sessions;
    private readonly JmsTransport? _transport;
    private readonly ILogger<JmsClient> _clientLogger;

    public UserAccountService(IOptions<JmsOptions> jmsOptions, JmsCredentialStore credentials, JmsUserSessionPool sessions, ILogger<JmsClient> clientLogger, JmsTransport? transport = null)
    {
        _transport = transport;
        _clientLogger = clientLogger;
        _jmsOptions = jmsOptions.Value;
        _credentials = credentials;
        _sessions = sessions;
    }

    /// <summary>Returns the result and, on success, the username as stored (use it for the
    /// auth cookie so every session keys the same way).</summary>
    public async Task<(AppLoginResult Result, string? Username)> LoginAsync(string username, string password)
    {
        username = username.Trim();
        var client = new JmsClient(_jmsOptions, _transport, _clientLogger);

        switch (await client.TryLoginAsync(username, password))
        {
            case JmsAuthResult.Success:
                var stored = await _credentials.SaveVerifiedAsync(username, password);
                _sessions.Register(stored, client);
                return (AppLoginResult.Success, stored);

            case JmsAuthResult.Rejected:
                client.Dispose();
                return (AppLoginResult.InvalidCredentials, null);

            default:
                client.Dispose();
                var offline = await _credentials.VerifyOfflineAsync(username, password);
                return offline is null
                    ? (AppLoginResult.JmsUnavailable, null)
                    : (AppLoginResult.SuccessOffline, offline);
        }
    }

    /// <summary>Re-saves a logged-in user's JMS password after JMS rejected the stored one
    /// (changed in JMS) or it couldn't be decrypted.</summary>
    public async Task<JmsAuthResult> ReconnectAsync(string username, string password)
    {
        var client = new JmsClient(_jmsOptions, _transport, _clientLogger);
        var result = await client.TryLoginAsync(username, password);
        if (result != JmsAuthResult.Success)
        {
            client.Dispose();
            return result;
        }

        var stored = await _credentials.SaveVerifiedAsync(username, password);
        _sessions.Register(stored, client);
        return result;
    }

    public void Logout(string username) => _sessions.Remove(username);
}
