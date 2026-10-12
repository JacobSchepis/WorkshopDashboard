using Microsoft.EntityFrameworkCore;
using WorkshopDashboard.Data;
using WorkshopDashboard.Models;

namespace WorkshopDashboard.Services.Stores;

// ═══════════════════════════════════════════════════════════════════════════
//  LoanEquipmentOverlayStore
//
//  Local annotations (workflow status, company links, buy price, covering
//  jobs) for units on the live loan fleet, keyed by JMS equipment_id — see
//  LoanEquipmentOverlay for what's tracked here vs. read live from JMS.
//  There's no "add" (an overlay is meaningless without a live equipment row
//  to attach to) or "delete" (an equipment_id that drops out of the live
//  LOAN-UNIT list just makes its overlay entry inert, not wrong) — only upsert.
//
//  LoanEquipmentManager.razor is the UI for this data.
// ═══════════════════════════════════════════════════════════════════════════

public class LoanEquipmentOverlayStore
{
    private readonly IDbContextFactory<WorkshopDbContext> _dbFactory;

    public LoanEquipmentOverlayStore(IDbContextFactory<WorkshopDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<List<LoanEquipmentOverlay>> LoadAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.LoanEquipmentOverlays.AsNoTracking().OrderBy(o => o.EquipmentId).ToListAsync();
    }

    public async Task UpsertAsync(LoanEquipmentOverlay overlay)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var existing = await db.LoanEquipmentOverlays.FindAsync(overlay.EquipmentId);
        if (existing is null) db.LoanEquipmentOverlays.Add(overlay);
        else db.Entry(existing).CurrentValues.SetValues(overlay);
        await db.SaveChangesAsync();
    }
}
