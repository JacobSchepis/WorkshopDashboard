using Microsoft.EntityFrameworkCore;
using WorkshopDashboard.Data;

namespace WorkshopDashboard.Services.Stores;

/// <summary>Minutes saved per tracked action, set on UsageTimeSaved.razor.</summary>
public class ActionTimeSavingStore
{
    private readonly IDbContextFactory<WorkshopDbContext> _dbFactory;

    public ActionTimeSavingStore(IDbContextFactory<WorkshopDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Dictionary<string, ActionTimeSaving>> LoadAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return (await db.ActionTimeSavings.AsNoTracking().ToListAsync())
            .ToDictionary(s => s.ActionName, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Sets an action's saving. Zero (or less) minutes clears it.</summary>
    public async Task SetAsync(string actionName, double minutes, bool perItem)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.ActionTimeSavings.FindAsync(actionName);
        if (minutes <= 0)
        {
            if (row != null) db.ActionTimeSavings.Remove(row);
        }
        else
        {
            if (row == null) db.ActionTimeSavings.Add(row = new ActionTimeSaving { ActionName = actionName });
            row.Minutes = minutes;
            row.PerItem = perItem;
        }
        await db.SaveChangesAsync();
    }
}
