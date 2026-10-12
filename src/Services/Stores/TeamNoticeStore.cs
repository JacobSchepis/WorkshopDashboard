using Microsoft.EntityFrameworkCore;
using WorkshopDashboard.Data;
using WorkshopDashboard.Models;

namespace WorkshopDashboard.Services.Stores;

// The TeamNotice list shown on WorkshopNotifications's "Team notices" panel, newest first.
// See TeamNotice.cs.
public class TeamNoticeStore
{
    private readonly IDbContextFactory<WorkshopDbContext> _dbFactory;

    public TeamNoticeStore(IDbContextFactory<WorkshopDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<List<TeamNotice>> LoadAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.TeamNotices.AsNoTracking().OrderByDescending(n => n.CreatedAt).ToListAsync();
    }

    public async Task AddAsync(TeamNotice notice)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        db.TeamNotices.Add(notice);
        await db.SaveChangesAsync();
    }

    public async Task RemoveAsync(Guid id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await db.TeamNotices.Where(n => n.Id == id).ExecuteDeleteAsync();
    }
}
