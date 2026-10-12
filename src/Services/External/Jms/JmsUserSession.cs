using Microsoft.AspNetCore.Components.Authorization;
using WorkshopDashboard.Abstract;

namespace WorkshopDashboard.Services.External.Jms;

// ═══════════════════════════════════════════════════════════════════════════
//  JmsUserSession
//
//  The logged-in user's own JMS session, for writes (so JMS records who
//  actually made the change) and the few reads JMS scopes to the session's
//  own user. Plain reads go through the shared JmsReadSession instead.
//
//  Scoped per circuit, but the session itself lives in JmsUserSessionPool,
//  keyed by username — this just looks up who's logged in and asks the pool.
//  An expired JMS session is logged back in there automatically; null only
//  means JMS is unreachable or the user's stored password needs re-entering.
//  Writes that can wait (service lines, status changes) go through WriteAsync
//  instead, which queues them rather than failing when JMS is down.
// ═══════════════════════════════════════════════════════════════════════════

public class JmsUserSession
{
    private readonly AuthenticationStateProvider _authStateProvider;
    private readonly JmsUserSessionPool _pool;
    private readonly JmsWriteQueue _writes;

    public JmsUserSession(AuthenticationStateProvider authStateProvider, JmsUserSessionPool pool, JmsWriteQueue writes)
    {
        _authStateProvider = authStateProvider;
        _pool = pool;
        _writes = writes;
    }

    /// <summary>Sends a write as the logged-in user — straight away when JMS can take it,
    /// otherwise queued to send later (see JmsWriteQueue). Prefer this over calling the client
    /// directly for anything JmsWrite covers.</summary>
    public async Task<JmsWriteResult> WriteAsync(JmsWrite write)
    {
        var username = await GetUsernameAsync();
        return username is null
            ? new JmsWriteResult(JmsWriteStatus.Failed, null)
            : await _writes.SubmitAsync(username, write);
    }

    /// <summary>Null if nobody's logged in, JMS is unreachable, or the user needs to re-enter
    /// their password — callers treat it as "can't do that right now".</summary>
    public async Task<IJmsDataClient?> GetClientAsync()
    {
        var username = await GetUsernameAsync();
        return username is null ? null : (await _pool.GetAsync(username)).Client;
    }

    /// <summary>Re-verifies with JMS rather than trusting the pool's recent check — for the
    /// nav bar's indicator, which needs to notice a session expiring mid-visit.</summary>
    public async Task<JmsUserSessionState> CheckAsync()
    {
        var username = await GetUsernameAsync();
        return username is null
            ? JmsUserSessionState.PasswordNeeded
            : (await _pool.GetAsync(username, forceCheck: true)).State;
    }

    private async Task<string?> GetUsernameAsync()
    {
        var auth = await _authStateProvider.GetAuthenticationStateAsync();
        var name = auth.User.Identity?.IsAuthenticated == true ? auth.User.Identity.Name : null;
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }
}
