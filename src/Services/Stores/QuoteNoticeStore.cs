using Microsoft.EntityFrameworkCore;
using WorkshopDashboard.Data;
using WorkshopDashboard.Models;
using WorkshopDashboard.Services.Quoting;

namespace WorkshopDashboard.Services.Stores;

// ═══════════════════════════════════════════════════════════════════════════
//  QuoteNoticeStore
//
//  Admin-editable disclaimer/advisory text (e.g. Apex's "Leak Advisory") posted
//  as $0 line items on matching quotes by QuoteSubmissionService. Entries are
//  identified by Id. QuoteConfig.razor is the CRUD UI for this list.
// ═══════════════════════════════════════════════════════════════════════════

public class QuoteNoticeStore
{
    private readonly IDbContextFactory<WorkshopDbContext> _dbFactory;

    public QuoteNoticeStore(IDbContextFactory<WorkshopDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<List<QuoteNotice>> LoadAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.QuoteNotices.AsNoTracking().OrderBy(n => n.Order).ToListAsync();
    }

    /// <summary>Notices applying to this quote flow (global, or explicitly targeted), sorted by
    /// Order — the order QuoteSubmissionService will post them in, so it's also the order to
    /// show them for tick/untick on the quote page.</summary>
    public async Task<List<QuoteNotice>> GetApplicableAsync(QuoteNoticeTarget target)
    {
        var list = await LoadAsync();
        return list.Where(n => n.IsGlobal || n.Targets.Contains(target)).ToList();
    }

    /// <summary>Default Order for a newly-created notice — appends after every existing one.</summary>
    public async Task<int> GetNextOrderAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var max = await db.QuoteNotices.MaxAsync(n => (int?)n.Order);
        return max is null ? 200 : max.Value + 10;
    }

    public async Task AddAsync(QuoteNotice notice)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        db.QuoteNotices.Add(notice);
        await db.SaveChangesAsync();
    }

    public async Task UpdateAsync(Guid id, QuoteNotice updated)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var existing = await db.QuoteNotices.FindAsync(id)
            ?? throw new InvalidOperationException($"Notice \"{id}\" not found.");

        updated.Id = id; // the key can't change — SetValues would throw if it differed
        db.Entry(existing).CurrentValues.SetValues(updated);
        await db.SaveChangesAsync();
    }

    public async Task RemoveAsync(Guid id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await db.QuoteNotices.Where(n => n.Id == id).ExecuteDeleteAsync();
    }
}
