using Microsoft.EntityFrameworkCore;
using WorkshopDashboard.Data;
using WorkshopDashboard.Models;

namespace WorkshopDashboard.Services.Stores;

// ═══════════════════════════════════════════════════════════════════════════
//  LoanEquipmentStatusStore
//
//  The configurable list of workflow statuses a loan unit can be in — see
//  LoanEquipmentManager.razor's "Manage Statuses" panel. Replaces what used to
//  be a fixed LoanEquipmentStatus enum. LoanEquipmentOverlay.Status stores a
//  status's Id (not its Label), so renaming a status here doesn't orphan
//  units already set to it.
//
//  Falls back to the original six statuses (with the same Ids as the old
//  enum's member names, so existing overlay data keeps matching) while the
//  table is empty; the first change saves the full list.
//
//  Deleting a status while any unit still has it is refused by
//  LoanEquipmentManager.razor before it ever calls RemoveAsync — this store
//  doesn't know about LoanEquipmentOverlayStore, so it just does what it's
//  told.
// ═══════════════════════════════════════════════════════════════════════════

public class LoanEquipmentStatusStore
{
    private readonly IDbContextFactory<WorkshopDbContext> _dbFactory;

    public LoanEquipmentStatusStore(IDbContextFactory<WorkshopDbContext> dbFactory) => _dbFactory = dbFactory;

    public static IReadOnlyList<LoanEquipmentStatusOption> DefaultStatuses => new List<LoanEquipmentStatusOption>
    {
        new() { Id = "Ready", Label = "Ready", Color = "Success", Order = 0 },
        new() { Id = "PreparingLoan", Label = "Preparing Loan", Color = "Info", Order = 1 },
        new() { Id = "Loaned", Label = "Loaned", Color = "Primary", Order = 2 },
        new() { Id = "Repairing", Label = "Repairing", Color = "Warning", Order = 3 },
        new() { Id = "Damaged", Label = "Damaged", Color = "Danger", Order = 4 },
        new() { Id = "SellToPmr", Label = "Sell to PMR", Color = "Secondary", Order = 5 },
    };

    public async Task<List<LoanEquipmentStatusOption>> LoadAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var list = await db.LoanEquipmentStatuses.AsNoTracking().OrderBy(s => s.Order).ToListAsync();
        return list.Count > 0 ? list : CloneDefaults();
    }

    public async Task AddAsync(string label, string color)
    {
        await using var db = await OpenWithDefaultsAsync();
        if (await db.LoanEquipmentStatuses.AnyAsync(s => s.Label == label))
            throw new InvalidOperationException($"A status named \"{label}\" already exists.");

        var maxOrder = await db.LoanEquipmentStatuses.MaxAsync(s => (int?)s.Order);
        db.LoanEquipmentStatuses.Add(new LoanEquipmentStatusOption
        {
            Id = Guid.NewGuid().ToString("N"),
            Label = label,
            Color = color,
            Order = maxOrder is null ? 0 : maxOrder.Value + 1
        });
        await db.SaveChangesAsync();
    }

    public async Task UpdateAsync(string id, string label, string color)
    {
        await using var db = await OpenWithDefaultsAsync();
        var status = await db.LoanEquipmentStatuses.FindAsync(id)
            ?? throw new InvalidOperationException("Status not found.");

        if (!status.Label.Equals(label, StringComparison.OrdinalIgnoreCase)
            && await db.LoanEquipmentStatuses.AnyAsync(s => s.Id != id && s.Label == label))
            throw new InvalidOperationException($"A status named \"{label}\" already exists.");

        status.Label = label;
        status.Color = color;
        await db.SaveChangesAsync();
    }

    public async Task RemoveAsync(string id)
    {
        await using var db = await OpenWithDefaultsAsync();
        await db.LoanEquipmentStatuses.Where(s => s.Id == id).ExecuteDeleteAsync();
    }

    /// <summary>Swaps a status with its neighbour one place up (direction -1) or down
    /// (direction +1) in display order.</summary>
    public async Task MoveAsync(string id, int direction)
    {
        await using var db = await OpenWithDefaultsAsync();
        var ordered = await db.LoanEquipmentStatuses.OrderBy(s => s.Order).ToListAsync();
        var index = ordered.FindIndex(s => s.Id == id);
        var targetIndex = index + direction;
        if (index < 0 || targetIndex < 0 || targetIndex >= ordered.Count) return;

        (ordered[index], ordered[targetIndex]) = (ordered[targetIndex], ordered[index]);
        for (var i = 0; i < ordered.Count; i++) ordered[i].Order = i;

        await db.SaveChangesAsync();
    }

    // Any change is made against the full list, so the defaults get saved the first time
    // someone edits the (still default) list.
    private async Task<WorkshopDbContext> OpenWithDefaultsAsync()
    {
        var db = await _dbFactory.CreateDbContextAsync();
        if (!await db.LoanEquipmentStatuses.AnyAsync())
        {
            db.LoanEquipmentStatuses.AddRange(CloneDefaults());
            await db.SaveChangesAsync();
        }
        return db;
    }

    private static List<LoanEquipmentStatusOption> CloneDefaults() => DefaultStatuses
        .Select(s => new LoanEquipmentStatusOption { Id = s.Id, Label = s.Label, Color = s.Color, Order = s.Order })
        .ToList();
}
