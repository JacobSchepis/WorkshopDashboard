using Microsoft.EntityFrameworkCore;
using WorkshopDashboard.Data;
using WorkshopDashboard.Models;

namespace WorkshopDashboard.Services.Stores;

// The TurnaroundStat list shown on WorkshopStatus's "Average turnaround" panel, in the
// order they were added. See TurnaroundStat.cs.
public class TurnaroundStatStore
{
    private readonly IDbContextFactory<WorkshopDbContext> _dbFactory;

    public TurnaroundStatStore(IDbContextFactory<WorkshopDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<List<TurnaroundStat>> LoadAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.InPositionOrder<TurnaroundStat>().AsNoTracking().ToListAsync();
    }

    public async Task AddAsync(TurnaroundStat stat)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await db.AddLastAsync(stat);
        await db.SaveChangesAsync();
    }

    public async Task UpdateAsync(TurnaroundStat updated)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var existing = await db.TurnaroundStats.FindAsync(updated.Id)
            ?? throw new InvalidOperationException("Stat not found.");

        db.Entry(existing).CurrentValues.SetValues(updated);
        await db.SaveChangesAsync();
    }

    public async Task RemoveAsync(Guid id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await db.TurnaroundStats.Where(s => s.Id == id).ExecuteDeleteAsync();
    }
}
