using Microsoft.EntityFrameworkCore;
using WorkshopDashboard.Data;

namespace WorkshopDashboard.Services.Stores;

/// <summary>Read/write for AppSetting rows — the handful of single config values that don't
/// warrant a table of their own. Used by TimesheetSettingsStore
/// rather than directly by pages.</summary>
public class SettingsStore
{
    private readonly IDbContextFactory<WorkshopDbContext> _dbFactory;

    public SettingsStore(IDbContextFactory<WorkshopDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<string?> GetAsync(string key)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return (await db.Settings.FindAsync(key))?.Value;
    }

    public async Task<Dictionary<string, string>> GetByPrefixAsync(string prefix)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.Settings.Where(s => s.Key.StartsWith(prefix)).ToDictionaryAsync(s => s.Key, s => s.Value);
    }

    /// <summary>Null removes the setting.</summary>
    public async Task SetAsync(string key, string? value)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var existing = await db.Settings.FindAsync(key);

        if (value is null)
        {
            if (existing is not null) db.Settings.Remove(existing);
        }
        else if (existing is null)
        {
            db.Settings.Add(new AppSetting { Key = key, Value = value });
        }
        else
        {
            existing.Value = value;
        }

        await db.SaveChangesAsync();
    }
}
