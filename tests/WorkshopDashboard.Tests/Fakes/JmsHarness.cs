using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using WorkshopDashboard.Services.Accounts;
using WorkshopDashboard.Services.External.Jms;

namespace WorkshopDashboard.Tests.Fakes;

/// <summary>
/// The app's real JMS services, wired together the way Program.cs does, but pointed at a
/// <see cref="FakeJms"/> and a <see cref="TestDb"/>. One per test:
/// <code>await using var h = await JmsHarness.StartAsync();</code>
/// </summary>
public sealed class JmsHarness : IAsyncDisposable
{
    public const string User = "alice";
    public const string Password = "alice-pw";

    public FakeJms Jms { get; }
    public TestDb Db { get; } = new();
    public JmsStatus Status { get; } = new();
    public IDataProtectionProvider DataProtection { get; } = new EphemeralDataProtectionProvider();

    public JmsReadSession Reads { get; }
    public JmsCredentialStore Credentials { get; }
    public JmsUserSessionPool Sessions { get; }
    public UserAccountService Accounts { get; }
    public JmsWriteQueue Writes { get; }

    private JmsHarness(FakeJms jms)
    {
        Jms = jms;
        var options = Microsoft.Extensions.Options.Options.Create(jms.Options);

        Reads = new JmsReadSession(options, Status, NullLoggerFactory.Instance);
        Credentials = new JmsCredentialStore(Db, DataProtection, NullLogger<JmsCredentialStore>.Instance);
        Sessions = new JmsUserSessionPool(options, Credentials, NullLogger<JmsClient>.Instance);
        Accounts = new UserAccountService(options, Credentials, Sessions, NullLogger<JmsClient>.Instance);
        Writes = new JmsWriteQueue(Db, Sessions, Reads, Status, NullLogger<JmsWriteQueue>.Instance);

        jms.SetPassword(User, Password);
    }

    public static async Task<JmsHarness> StartAsync() => new(await FakeJms.StartAsync());

    /// <summary>Logs the test user into the app (and so into JMS), as the login page would.</summary>
    public async Task LoginAsync()
    {
        var (result, _) = await Accounts.LoginAsync(User, Password);
        Assert.Equal(AppLoginResult.Success, result);
    }

    /// <summary>What the uptime monitor would report once it noticed JMS stop.</summary>
    public async Task TakeJmsDownAsync()
    {
        await Jms.StopAsync();
        Status.Report(false, DateTime.Now);
    }

    public async ValueTask DisposeAsync()
    {
        Reads.Dispose();
        Db.Dispose();
        await Jms.DisposeAsync();
    }
}
