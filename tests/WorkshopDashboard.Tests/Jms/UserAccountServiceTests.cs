using WorkshopDashboard.Services.Accounts;
using WorkshopDashboard.Services.External.Jms;
using WorkshopDashboard.Tests.Fakes;

namespace WorkshopDashboard.Tests.Jms;

// The app login rules: JMS decides whenever it's reachable; the saved password only counts
// when it isn't, and only for someone who has logged in before.
public class UserAccountServiceTests
{
    [Fact]
    public async Task FirstLogin_WhenJmsAccepts_CreatesTheUser()
    {
        await using var h = await JmsHarness.StartAsync();

        var (result, username) = await h.Accounts.LoginAsync(JmsHarness.User, JmsHarness.Password);

        Assert.Equal(AppLoginResult.Success, result);
        Assert.Equal(JmsHarness.User, username);
        using var db = h.Db.CreateDbContext();
        Assert.Single(db.Users);
    }

    [Fact]
    public async Task Login_HandsItsJmsSessionOn_SoTheFirstWriteDoesNotLogInAgain()
    {
        await using var h = await JmsHarness.StartAsync();
        await h.LoginAsync();

        var (client, state) = await h.Sessions.GetAsync(JmsHarness.User);

        Assert.NotNull(client);
        Assert.Equal(JmsUserSessionState.Connected, state);
        Assert.Equal(1, h.Jms.LoginCount(JmsHarness.User));
    }

    [Fact]
    public async Task Login_WithWrongPassword_IsInvalid()
    {
        await using var h = await JmsHarness.StartAsync();

        var (result, username) = await h.Accounts.LoginAsync(JmsHarness.User, "wrong");

        Assert.Equal(AppLoginResult.InvalidCredentials, result);
        Assert.Null(username);
    }

    [Fact]
    public async Task Login_WithTheOldPassword_AfterItChangedInJms_IsInvalid()
    {
        await using var h = await JmsHarness.StartAsync();
        await h.LoginAsync();

        h.Jms.SetPassword(JmsHarness.User, "new-pw");
        var (result, _) = await h.Accounts.LoginAsync(JmsHarness.User, JmsHarness.Password);

        // The saved hash still matches the old password, but JMS is reachable, so JMS decides.
        Assert.Equal(AppLoginResult.InvalidCredentials, result);
    }

    [Fact]
    public async Task Login_WithTheNewPassword_AfterItChangedInJms_Succeeds()
    {
        await using var h = await JmsHarness.StartAsync();
        await h.LoginAsync();

        h.Jms.SetPassword(JmsHarness.User, "new-pw");
        var (result, _) = await h.Accounts.LoginAsync(JmsHarness.User, "new-pw");

        Assert.Equal(AppLoginResult.Success, result);
    }

    [Fact]
    public async Task Login_WhenJmsIsDown_WithTheSavedPassword_SucceedsOffline()
    {
        await using var h = await JmsHarness.StartAsync();
        await h.LoginAsync();

        await h.TakeJmsDownAsync();
        var (result, username) = await h.Accounts.LoginAsync(JmsHarness.User, JmsHarness.Password);

        Assert.Equal(AppLoginResult.SuccessOffline, result);
        Assert.Equal(JmsHarness.User, username);
    }

    [Fact]
    public async Task Login_WhenJmsIsDown_WithAWrongPassword_IsRefused()
    {
        await using var h = await JmsHarness.StartAsync();
        await h.LoginAsync();

        await h.TakeJmsDownAsync();
        var (result, _) = await h.Accounts.LoginAsync(JmsHarness.User, "wrong");

        Assert.Equal(AppLoginResult.JmsUnavailable, result);
    }

    [Fact]
    public async Task Login_WhenJmsIsDown_ForSomeoneWhoHasNeverLoggedIn_IsRefused()
    {
        await using var h = await JmsHarness.StartAsync();

        await h.TakeJmsDownAsync();
        var (result, _) = await h.Accounts.LoginAsync(JmsHarness.User, JmsHarness.Password);

        Assert.Equal(AppLoginResult.JmsUnavailable, result);
    }

    [Fact]
    public async Task Login_UsernameCase_ReturnsTheSpellingFirstStored()
    {
        await using var h = await JmsHarness.StartAsync();
        await h.LoginAsync(); // stored as "alice"

        var (_, username) = await h.Accounts.LoginAsync("ALICE", JmsHarness.Password);

        Assert.Equal(JmsHarness.User, username);
        using var db = h.Db.CreateDbContext();
        Assert.Single(db.Users);
    }

    [Fact]
    public async Task Reconnect_WithTheCurrentJmsPassword_SucceedsAndSavesIt()
    {
        await using var h = await JmsHarness.StartAsync();
        await h.LoginAsync();
        h.Jms.SetPassword(JmsHarness.User, "new-pw");

        var result = await h.Accounts.ReconnectAsync(JmsHarness.User, "new-pw");

        Assert.Equal(JmsAuthResult.Success, result);
        Assert.Equal("new-pw", await h.Credentials.GetJmsPasswordAsync(JmsHarness.User));
    }

    [Fact]
    public async Task Reconnect_WithAWrongPassword_IsRejectedAndSavesNothing()
    {
        await using var h = await JmsHarness.StartAsync();
        await h.LoginAsync();

        var result = await h.Accounts.ReconnectAsync(JmsHarness.User, "wrong");

        Assert.Equal(JmsAuthResult.Rejected, result);
        Assert.Equal(JmsHarness.Password, await h.Credentials.GetJmsPasswordAsync(JmsHarness.User));
    }
}
