using Microsoft.EntityFrameworkCore;
using WorkshopDashboard.Data;
using WorkshopDashboard.Models;

namespace WorkshopDashboard.Services.Stores;

// Records when JMS stops responding and when it comes back, so
// JmsUptimeMonitorService's periodic ping can build a history of outages for the JMS Uptime
// page.
public class JmsUptimeStore
{
    private readonly IDbContextFactory<WorkshopDbContext> _dbFactory;

    public JmsUptimeStore(IDbContextFactory<WorkshopDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<List<JmsOutagePeriod>> LoadAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.JmsOutages.AsNoTracking().OrderBy(p => p.StartedAt).ToListAsync();
    }

    // No-op if an outage is already open, so repeated down-pings during the same outage don't
    // create duplicate periods.
    public async Task StartOutageAsync(DateTime startedAt)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        if (await db.JmsOutages.AnyAsync(p => p.EndedAt == null)) return;

        db.JmsOutages.Add(new JmsOutagePeriod { StartedAt = startedAt });
        await db.SaveChangesAsync();
    }

    public async Task EndOutageAsync(DateTime endedAt)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await db.JmsOutages.Where(p => p.EndedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.EndedAt, endedAt));
    }
}
