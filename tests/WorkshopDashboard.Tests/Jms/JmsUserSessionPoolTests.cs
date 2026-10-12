using WorkshopDashboard.Services.External.Jms;
using WorkshopDashboard.Tests.Fakes;

namespace WorkshopDashboard.Tests.Jms;

// Each user's own JMS session: re-established silently from the saved password, and a clear
// state when that isn't possible.
public class JmsUserSessionPoolTests
{
    [Fact]
    public async Task ExpiredSession_IsLoggedBackInSilently()
    {
        await using var h = await JmsHarness.StartAsync();
        await h.LoginAsync();

        h.Jms.ExpireAllSessions();
        var (client, state) = await h.Sessions.GetAsync(JmsHarness.User, forceCheck: true);

        Assert.NotNull(client);
        Assert.Equal(JmsUserSessionState.Connected, state);
        Assert.Equal(2, h.Jms.LoginCount(JmsHarness.User));
    }

    [Fact]
    public async Task PasswordChangedInJms_NeedsThePasswordAgain()
    {
        await using var h = await JmsHarness.StartAsync();
        await h.LoginAsync();

        h.Jms.SetPassword(JmsHarness.User, "new-pw");
        h.Jms.ExpireAllSessions();
        var (client, state) = await h.Sessions.GetAsync(JmsHarness.User, forceCheck: true);

        Assert.Null(client);
        Assert.Equal(JmsUserSessionState.PasswordNeeded, state);
    }

    [Fact]
    public async Task JmsDown_IsUnavailable_NotPasswordNeeded()
    {
        await using var h = await JmsHarness.StartAsync();
        await h.LoginAsync();

        await h.Jms.StopAsync();
        var (client, state) = await h.Sessions.GetAsync(JmsHarness.User, forceCheck: true);

        Assert.Null(client);
        Assert.Equal(JmsUserSessionState.JmsUnavailable, state);
    }

    [Fact]
    public async Task UserWhoNeverLoggedIn_NeedsThePassword()
    {
        await using var h = await JmsHarness.StartAsync();

        var (client, state) = await h.Sessions.GetAsync("nobody");

        Assert.Null(client);
        Assert.Equal(JmsUserSessionState.PasswordNeeded, state);
    }

    [Fact]
    public async Task RecentlyVerifiedSession_IsNotRecheckedOnEveryCall()
    {
        await using var h = await JmsHarness.StartAsync();
        await h.LoginAsync();

        h.Jms.ExpireAllSessions();
        var (client, state) = await h.Sessions.GetAsync(JmsHarness.User);

        // Trusted for a few minutes after login, so no check and no second login yet.
        Assert.NotNull(client);
        Assert.Equal(JmsUserSessionState.Connected, state);
        Assert.Equal(1, h.Jms.LoginCount(JmsHarness.User));
    }

    [Fact]
    public async Task Logout_DropsTheSession_SoTheNextUseLogsInAgain()
    {
        await using var h = await JmsHarness.StartAsync();
        await h.LoginAsync();

        h.Accounts.Logout(JmsHarness.User);
        h.Jms.ExpireAllSessions();
        var (_, state) = await h.Sessions.GetAsync(JmsHarness.User);

        Assert.Equal(JmsUserSessionState.Connected, state);
        Assert.Equal(2, h.Jms.LoginCount(JmsHarness.User));
    }
}
