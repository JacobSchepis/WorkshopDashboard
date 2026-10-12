using Microsoft.EntityFrameworkCore;
using WorkshopDashboard.Data;

namespace WorkshopDashboard.Services.Stores;

// ═══════════════════════════════════════════════════════════════════════════
//  HighlightedJobsStore
//
//  Job ids pinned to the Overview page's "Highlighted Jobs" section for quick
//  daily checking.
// ═══════════════════════════════════════════════════════════════════════════

public class HighlightedJobsStore
{
    private readonly IDbContextFactory<WorkshopDbContext> _dbFactory;

    public HighlightedJobsStore(IDbContextFactory<WorkshopDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<HashSet<int>> LoadAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return (await db.HighlightedJobs.Select(x => x.JobId).ToListAsync()).ToHashSet();
    }

    public async Task AddAsync(int jobId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        if (await db.HighlightedJobs.AnyAsync(x => x.JobId == jobId)) return;
        db.HighlightedJobs.Add(new() { JobId = jobId });
        await db.SaveChangesAsync();
    }

    public async Task RemoveAsync(int jobId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await db.HighlightedJobs.Where(x => x.JobId == jobId).ExecuteDeleteAsync();
    }
}
