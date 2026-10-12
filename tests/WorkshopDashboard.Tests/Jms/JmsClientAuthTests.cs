using WorkshopDashboard.Services.External.Jms;
using WorkshopDashboard.Tests.Fakes;

namespace WorkshopDashboard.Tests.Jms;

// JmsClient's three-way login answer — everything above it (app login, silent re-login,
// the write queue) decides what to do based on Rejected vs Unreachable.
public class JmsClientAuthTests
{
    [Fact]
    public async Task TryLogin_WithCorrectPassword_Succeeds()
    {
        await using var jms = await FakeJms.StartAsync();
        jms.SetPassword("alice", "pw");
        using var client = new JmsClient(jms.Options);

        Assert.Equal(JmsAuthResult.Success, await client.TryLoginAsync("alice", "pw"));
        Assert.Equal(JmsAuthResult.Success, await client.CheckAuthAsync());
    }

    [Fact]
    public async Task TryLogin_WithWrongPassword_IsRejected()
    {
        await using var jms = await FakeJms.StartAsync();
        jms.SetPassword("alice", "pw");
        using var client = new JmsClient(jms.Options);

        Assert.Equal(JmsAuthResult.Rejected, await client.TryLoginAsync("alice", "wrong"));
    }

    [Fact]
    public async Task TryLogin_WhenJmsIsDown_IsUnreachable()
    {
        await using var jms = await FakeJms.StartAsync();
        jms.SetPassword("alice", "pw");
        await jms.StopAsync();
        using var client = new JmsClient(jms.Options);

        Assert.Equal(JmsAuthResult.Unreachable, await client.TryLoginAsync("alice", "pw"));
    }

    [Fact]
    public async Task CheckAuth_AfterJmsExpiresTheSession_IsRejected()
    {
        await using var jms = await FakeJms.StartAsync();
        jms.SetPassword("alice", "pw");
        using var client = new JmsClient(jms.Options);
        await client.TryLoginAsync("alice", "pw");

        jms.ExpireAllSessions();

        Assert.Equal(JmsAuthResult.Rejected, await client.CheckAuthAsync());
        Assert.False(await client.VerifyAuthenticationAsync());
    }

    [Fact]
    public async Task CheckAuth_WhenJmsIsDown_IsUnreachableNotRejected()
    {
        await using var jms = await FakeJms.StartAsync();
        jms.SetPassword("alice", "pw");
        using var client = new JmsClient(jms.Options);
        await client.TryLoginAsync("alice", "pw");

        await jms.StopAsync();

        Assert.Equal(JmsAuthResult.Unreachable, await client.CheckAuthAsync());
    }
}
