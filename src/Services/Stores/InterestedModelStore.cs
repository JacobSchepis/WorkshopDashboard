using Microsoft.EntityFrameworkCore;
using WorkshopDashboard.Data;

namespace WorkshopDashboard.Services.Stores;

// ═══════════════════════════════════════════════════════════════════════════
//  InterestedModelStore
//
//  Model ids flagged on the Model Watchlist page (/model-watchlist) as worth watching for
//  open, unapproved jobs.
// ═══════════════════════════════════════════════════════════════════════════

public class InterestedModelStore
{
    private readonly IDbContextFactory<WorkshopDbContext> _dbFactory;

    public InterestedModelStore(IDbContextFactory<WorkshopDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<HashSet<int>> LoadAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return (await db.InterestedModels.Select(x => x.ModelId).ToListAsync()).ToHashSet();
    }

    public async Task AddAsync(int modelId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        if (await db.InterestedModels.AnyAsync(x => x.ModelId == modelId)) return;
        db.InterestedModels.Add(new() { ModelId = modelId });
        await db.SaveChangesAsync();
    }

    public async Task RemoveAsync(int modelId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await db.InterestedModels.Where(x => x.ModelId == modelId).ExecuteDeleteAsync();
    }
}
