using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;

namespace WorkshopDashboard.Components.Helpers;

// Base for pages restricted to a single developer login — the "Dev Tools" nav
// section (Database Test, Usage Analytics, Timesheet Test). Same lock
// DatabaseTest used to carry inline; pulled out here now that a second page
// needs it too. AllowedUsername is public so NavCatalog can hide the section
// from everyone else; Program.cs sets it from DevTools:AllowedUsername in config.
public abstract class DevOnlyComponentBase : JmsComponentBase
{
    // Empty (the default) means nobody gets the Dev Tools pages.
    public static string AllowedUsername { get; set; } = "";

    public static bool IsDeveloper(string? username) =>
        !string.IsNullOrEmpty(AllowedUsername) && username == AllowedUsername;

    [Inject] protected AuthenticationStateProvider AuthStateProvider { get; set; } = default!;

    // NavigateTo doesn't unwind the current method call — a derived
    // OnInitializedAsync that does real work after calling base must check
    // this first, or an unauthorized user's browser would still receive (and
    // briefly render) data before the redirect lands.
    protected bool IsDevAuthorized { get; private set; }

    protected override async Task OnInitializedAsync()
    {
        await base.OnInitializedAsync();

        var auth = await AuthStateProvider.GetAuthenticationStateAsync();
        var username = auth.User.Identity?.Name;

        IsDevAuthorized = IsDeveloper(username);

        if (!IsDevAuthorized)
        {
            Nav.NavigateTo("/logout");
        }
    }
}
