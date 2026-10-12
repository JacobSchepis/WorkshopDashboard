using Microsoft.EntityFrameworkCore;
using WorkshopDashboard.Data;
using WorkshopDashboard.Models;

namespace WorkshopDashboard.Services.Stores;

// ═══════════════════════════════════════════════════════════════════════════
//  JobOfferOverlayStore
//
//  Local repair-cost/offer notes for the Model Watchlist page, keyed by JMS job_id — see
//  JobOfferOverlay for what's tracked and why. Upsert-only, same reasoning as
//  LoanEquipmentOverlayStore: an overlay is meaningless without a live job to attach to,
//  and a job that later gets approved/completed just makes its overlay entry stale rather
//  than wrong.
// ═══════════════════════════════════════════════════════════════════════════

public class JobOfferOverlayStore
{
    private readonly IDbContextFactory<WorkshopDbContext> _dbFactory;

    public JobOfferOverlayStore(IDbContextFactory<WorkshopDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<List<JobOfferOverlay>> LoadAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.JobOfferOverlays.AsNoTracking().OrderBy(o => o.JobId).ToListAsync();
    }

    public async Task UpsertAsync(JobOfferOverlay overlay)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var existing = await db.JobOfferOverlays.FindAsync(overlay.JobId);
        if (existing is null) db.JobOfferOverlays.Add(overlay);
        else db.Entry(existing).CurrentValues.SetValues(overlay);
        await db.SaveChangesAsync();
    }
}
