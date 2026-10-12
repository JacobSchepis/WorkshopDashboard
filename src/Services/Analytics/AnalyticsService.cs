using Microsoft.AspNetCore.Components.Authorization;
using WorkshopDashboard.Models;

namespace WorkshopDashboard.Services.Analytics;

// Thin per-circuit facade over AnalyticsWriter — stamps every event with the
// logged-in username before handing it to the (singleton) writer.
//
// Page views are wired up once, in MainLayout.OnInitializedAsync (MainLayout
// has no @rendermode of its own, so it re-runs fresh on every navigation —
// full reload or Blazor's enhanced navigation — with no double-prerender
// counting risk). Call TrackEventAsync directly from a component's @onclick
// for anything else worth counting, e.g.:
//     @inject AnalyticsService Analytics
//     <Button @onclick='() => Analytics.TrackEventAsync("Export report")'>
public class AnalyticsService
{
    private readonly AnalyticsWriter _writer;
    private readonly AuthenticationStateProvider _authStateProvider;

    public AnalyticsService(AnalyticsWriter writer, AuthenticationStateProvider authStateProvider)
    {
        _writer = writer;
        _authStateProvider = authStateProvider;
    }

    public Task TrackPageViewAsync(string path) => TrackAsync("PageView", path);

    public Task TrackEventAsync(string name, string? detail = null, int? quantity = null) =>
        TrackAsync("Click", name, detail, quantity);

    private async Task TrackAsync(string eventType, string name, string? detail = null, int? quantity = null)
    {
        var auth = await _authStateProvider.GetAuthenticationStateAsync();

        await _writer.AppendAsync(new AnalyticsEvent
        {
            EventType = eventType,
            Name = name,
            Detail = detail,
            Quantity = quantity,
            Username = auth.User.Identity?.Name
        });
    }
}
