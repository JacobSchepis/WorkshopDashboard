using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.DataProtection;
using WorkshopDashboard.Services.Accounts;
using WorkshopDashboard.Tests.Fakes;

namespace WorkshopDashboard.Tests.Jms;

// The two stored copies of a user's password: a hash for offline login, and an encrypted
// copy the app can decrypt to log them back into JMS.
public class JmsCredentialStoreTests
{
    [Fact]
    public async Task SavedPassword_IsNeverStoredInPlainText()
    {
        using var db = new TestDb();
        var store = new JmsCredentialStore(db, new EphemeralDataProtectionProvider(), NullLogger<JmsCredentialStore>.Instance);

        await store.SaveVerifiedAsync("alice", "secret-pw");

        using var ctx = db.CreateDbContext();
        var user = ctx.Users.Single();
        Assert.DoesNotContain("secret-pw", user.PasswordHash);
        Assert.DoesNotContain("secret-pw", user.EncryptedJmsPassword);
    }

    [Fact]
    public async Task SavedPassword_CanBeDecryptedForJms()
    {
        using var db = new TestDb();
        var store = new JmsCredentialStore(db, new EphemeralDataProtectionProvider(), NullLogger<JmsCredentialStore>.Instance);

        await store.SaveVerifiedAsync("alice", "secret-pw");

        Assert.Equal("secret-pw", await store.GetJmsPasswordAsync("alice"));
        Assert.Equal("secret-pw", await store.GetJmsPasswordAsync("ALICE"));
    }

    [Fact]
    public async Task SavedPassword_FromDifferentEncryptionKeys_IsTreatedAsMissing()
    {
        using var db = new TestDb();
        await new JmsCredentialStore(db, new EphemeralDataProtectionProvider(), NullLogger<JmsCredentialStore>.Instance).SaveVerifiedAsync("alice", "secret-pw");

        // As if App_Data/keys was lost or the database moved to another machine.
        var withOtherKeys = new JmsCredentialStore(db, new EphemeralDataProtectionProvider(), NullLogger<JmsCredentialStore>.Instance);

        Assert.Null(await withOtherKeys.GetJmsPasswordAsync("alice"));
    }

    [Fact]
    public async Task OfflineCheck_AcceptsOnlyTheSavedPassword()
    {
        using var db = new TestDb();
        var store = new JmsCredentialStore(db, new EphemeralDataProtectionProvider(), NullLogger<JmsCredentialStore>.Instance);
        await store.SaveVerifiedAsync("alice", "secret-pw");

        Assert.Equal("alice", await store.VerifyOfflineAsync("Alice", "secret-pw"));
        Assert.Null(await store.VerifyOfflineAsync("alice", "wrong"));
        Assert.Null(await store.VerifyOfflineAsync("bob", "secret-pw"));
    }

    [Fact]
    public async Task SavingAgain_ReplacesThePassword()
    {
        using var db = new TestDb();
        var store = new JmsCredentialStore(db, new EphemeralDataProtectionProvider(), NullLogger<JmsCredentialStore>.Instance);
        await store.SaveVerifiedAsync("alice", "old-pw");

        await store.SaveVerifiedAsync("alice", "new-pw");

        Assert.Equal("new-pw", await store.GetJmsPasswordAsync("alice"));
        Assert.Null(await store.VerifyOfflineAsync("alice", "old-pw"));
    }
}
