using Microsoft.EntityFrameworkCore;
using WorkshopDashboard.Data;
using WorkshopDashboard.Models;

namespace WorkshopDashboard.Services.Stores;

// ═══════════════════════════════════════════════════════════════════════════
//  RepairerStore
//
//  The list of repairers jobs get assigned to, for the equipment/repair-timeline
//  reporting pages. Entries are identified by Name, unique case-insensitively.
//  A job is "assigned" to a repairer when either the model id of its attached
//  equipment is in that repairer's ModelIds, or the job's own id is in that
//  repairer's JobIds (the override path for repairers whose work doesn't get
//  individual job_equipment rows). RepairerManager.razor is both the CRUD UI
//  for this list and the page that surfaces jobs matching neither.
// ═══════════════════════════════════════════════════════════════════════════

public class RepairerStore
{
    private readonly IDbContextFactory<WorkshopDbContext> _dbFactory;

    public RepairerStore(IDbContextFactory<WorkshopDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<List<Repairer>> LoadAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.Repairers.AsNoTracking().OrderBy(r => r.Name).ToListAsync();
    }

    public async Task AddAsync(Repairer repairer)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        if (await db.Repairers.AnyAsync(r => r.Name == repairer.Name))
            throw new InvalidOperationException($"A repairer named \"{repairer.Name}\" already exists.");

        db.Repairers.Add(repairer);
        await db.SaveChangesAsync();
    }

    public async Task UpdateAsync(string originalName, Repairer updated)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var existing = await FindAsync(db, originalName);

        if (!originalName.Equals(updated.Name, StringComparison.OrdinalIgnoreCase)
            && await db.Repairers.AnyAsync(r => r.Name == updated.Name))
            throw new InvalidOperationException($"A repairer named \"{updated.Name}\" already exists.");

        db.Entry(existing).CurrentValues.SetValues(updated);
        await db.SaveChangesAsync();
    }

    public async Task RemoveAsync(string name)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await db.Repairers.Where(r => r.Name == name).ExecuteDeleteAsync();
    }

    // ── Quick-assign convenience (RepairerManager.razor's unassigned-jobs list) ──

    public Task AddModelAsync(string repairerName, int modelId) =>
        MutateAsync(repairerName, r => { if (!r.ModelIds.Contains(modelId)) r.ModelIds.Add(modelId); });

    public Task RemoveModelAsync(string repairerName, int modelId) =>
        MutateAsync(repairerName, r => r.ModelIds.Remove(modelId));

    public Task AddJobAsync(string repairerName, int jobId) =>
        MutateAsync(repairerName, r => { if (!r.JobIds.Contains(jobId)) r.JobIds.Add(jobId); });

    public Task RemoveJobAsync(string repairerName, int jobId) =>
        MutateAsync(repairerName, r => r.JobIds.Remove(jobId));

    private async Task MutateAsync(string repairerName, Action<Repairer> mutate)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        mutate(await FindAsync(db, repairerName));
        await db.SaveChangesAsync();
    }

    private static async Task<Repairer> FindAsync(WorkshopDbContext db, string name) =>
        await db.Repairers.FirstOrDefaultAsync(r => r.Name == name)
        ?? throw new InvalidOperationException($"Repairer \"{name}\" not found.");
}
