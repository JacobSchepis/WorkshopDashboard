using Microsoft.EntityFrameworkCore;
using WorkshopDashboard.Data;
using WorkshopDashboard.Models;

namespace WorkshopDashboard.Services.Stores;

// The WorkshopTask list shown on WorkshopNotifications's "Tasks" panel, in the order they
// were added. See WorkshopTask.cs.
public class WorkshopTaskStore
{
    private readonly IDbContextFactory<WorkshopDbContext> _dbFactory;

    public WorkshopTaskStore(IDbContextFactory<WorkshopDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<List<WorkshopTask>> LoadAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.InPositionOrder<WorkshopTask>().AsNoTracking().ToListAsync();
    }

    public async Task AddAsync(WorkshopTask task)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await db.AddLastAsync(task);
        await db.SaveChangesAsync();
    }

    public async Task SetDoneAsync(Guid id, bool done)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await db.WorkshopTasks.Where(t => t.Id == id).ExecuteUpdateAsync(s => s.SetProperty(t => t.Done, done));
    }

    public async Task RemoveAsync(Guid id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await db.WorkshopTasks.Where(t => t.Id == id).ExecuteDeleteAsync();
    }
}
