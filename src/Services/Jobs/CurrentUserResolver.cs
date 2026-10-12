using Microsoft.AspNetCore.Components.Authorization;

using WorkshopDashboard.Services.External.Jms;
namespace WorkshopDashboard.Services.Jobs;

// ═══════════════════════════════════════════════════════════════════════════
//  CurrentUserResolver
//
//  Resolves the logged-in JMS user's id and display name from the auth
//  username — the same fuzzy "dotted username -> person search" approach
//  WaitingWorkshopProcessing.razor's ResolveCurrentUserAsync and
//  OwnerSection.PrefillOwnerFromUsernameAsync each already implement privately.
//  Pulled out here so JobNotesService (and anything else that needs to know
//  "who is actually doing this" for an UpdateJobService-style call) can
//  share it instead of re-deriving it a third time.
//
//  Scoped (not Singleton) — it caches per circuit via AuthenticationStateProvider,
//  which is itself scoped, so the cache naturally lives for one user's session.
// ═══════════════════════════════════════════════════════════════════════════

public class CurrentUserResolver
{
    private readonly AuthenticationStateProvider _authStateProvider;
    private readonly JmsReadSession _jmsRead;
    private (int Id, string Name)? _cached;

    public CurrentUserResolver(AuthenticationStateProvider authStateProvider, JmsReadSession jmsRead)
    {
        _authStateProvider = authStateProvider;
        _jmsRead = jmsRead;
    }

    public async Task<(int Id, string Name)?> ResolveAsync()
    {
        if (_cached.HasValue) return _cached;

        var jms = await _jmsRead.GetClientAsync();
        if (jms == null) return null;

        var auth = await _authStateProvider.GetAuthenticationStateAsync();
        var username = auth.User.Identity?.Name;
        if (string.IsNullOrWhiteSpace(username)) return null;

        var baseQuery = username.Trim().Replace('.', ' ').Replace('_', ' ').Replace('-', ' ');
        baseQuery = string.Join(" ", baseQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (string.IsNullOrWhiteSpace(baseQuery)) return null;

        var hits = await jms.SearchUsersAsync(baseQuery);
        if (hits.Count == 0)
        {
            var firstWord = baseQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
            hits = await jms.SearchUsersAsync(firstWord);
        }

        if (hits.Count == 0) return null;

        _cached = (hits[0].UserId, hits[0].FullName);
        return _cached;
    }
}
