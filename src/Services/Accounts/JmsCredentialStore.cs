using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using WorkshopDashboard.Data;

namespace WorkshopDashboard.Services.Accounts;

// ═══════════════════════════════════════════════════════════════════════════
//  JmsCredentialStore
//
//  Each user's two stored copies of their JMS password (see AppUser):
//    - a one-way hash (ASP.NET Identity's PasswordHasher) for logging into
//      this app while JMS is unreachable;
//    - a Data Protection-encrypted copy the app can decrypt to log them back
//      into JMS when their session expires.
//  The encryption keys live in App_Data/keys (see Program.cs). If they're lost
//  or moved to another machine, GetJmsPasswordAsync returns null and the user
//  is asked for their password once to re-save it.
// ═══════════════════════════════════════════════════════════════════════════

public class JmsCredentialStore
{
    private readonly IDbContextFactory<WorkshopDbContext> _dbFactory;
    private readonly IDataProtector _protector;
    private readonly ILogger<JmsCredentialStore> _logger;
    private readonly PasswordHasher<AppUser> _hasher = new();

    public JmsCredentialStore(IDbContextFactory<WorkshopDbContext> dbFactory, IDataProtectionProvider dataProtection,
        ILogger<JmsCredentialStore> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
        _protector = dataProtection.CreateProtector("WorkshopDashboard.JmsPassword.v1");
    }

    /// <summary>Saves a password JMS has just accepted — creating the user on their first
    /// login. Returns the username as stored, so claims always use one spelling.</summary>
    public async Task<string> SaveVerifiedAsync(string username, string password)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Username == username);
        if (user is null)
        {
            user = new AppUser { Username = username, CreatedAtUtc = DateTime.UtcNow };
            db.Users.Add(user);
        }

        user.PasswordHash = _hasher.HashPassword(user, password);
        user.EncryptedJmsPassword = _protector.Protect(password);
        user.LastLoginAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return user.Username;
    }

    /// <summary>Checks a password against the stored hash, for logging in while JMS can't be
    /// asked. Returns the stored username, or null if there's no such user or it doesn't match.</summary>
    public async Task<string?> VerifyOfflineAsync(string username, string password)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Username == username);
        if (user is null) return null;

        if (_hasher.VerifyHashedPassword(user, user.PasswordHash, password) == PasswordVerificationResult.Failed)
            return null;

        user.LastLoginAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return user.Username;
    }

    /// <summary>The user's JMS password, or null if they've never logged in here or it can't
    /// be decrypted any more (encryption keys lost or from another machine).</summary>
    public async Task<string?> GetJmsPasswordAsync(string username)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var encrypted = await db.Users.Where(u => u.Username == username).Select(u => u.EncryptedJmsPassword).FirstOrDefaultAsync();
        if (string.IsNullOrEmpty(encrypted)) return null;

        try
        {
            return _protector.Unprotect(encrypted);
        }
        catch (CryptographicException ex)
        {
            _logger.LogWarning("Could not decrypt the stored JMS password for {Username}: {Error}", username, ex.Message);
            return null;
        }
    }
}
