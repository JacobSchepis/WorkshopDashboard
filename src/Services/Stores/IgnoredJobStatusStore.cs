using Microsoft.EntityFrameworkCore;
using WorkshopDashboard.Data;

namespace WorkshopDashboard.Services.Stores;

// ═══════════════════════════════════════════════════════════════════════════
//  IgnoredJobStatusStore
//
//  Job status ids the Model Watchlist page's "Open opportunities" list should never show,
//  regardless of which models are being watched — e.g. "Waiting Parts" is never worth an
//  offer, so hide it for everyone rather than relying on staff to Ignore each job by hand.
//  Distinct from the per-job Ignore button (JmsQueries.ModelWatchlistIgnoredNotePrefix),
//  which dismisses one specific job via a JMS note.
// ═══════════════════════════════════════════════════════════════════════════

public class IgnoredJobStatusStore
{
    private readonly IDbContextFactory<WorkshopDbContext> _dbFactory;

    public IgnoredJobStatusStore(IDbContextFactory<WorkshopDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<HashSet<int>> LoadAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return (await db.IgnoredJobStatuses.Select(x => x.JobStatusId).ToListAsync()).ToHashSet();
    }

    public async Task AddAsync(int jobStatusId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        if (await db.IgnoredJobStatuses.AnyAsync(x => x.JobStatusId == jobStatusId)) return;
        db.IgnoredJobStatuses.Add(new() { JobStatusId = jobStatusId });
        await db.SaveChangesAsync();
    }

    public async Task RemoveAsync(int jobStatusId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await db.IgnoredJobStatuses.Where(x => x.JobStatusId == jobStatusId).ExecuteDeleteAsync();
    }
}
