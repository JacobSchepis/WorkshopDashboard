using Microsoft.EntityFrameworkCore;
using WorkshopDashboard.Data;
using WorkshopDashboard.Models;

namespace WorkshopDashboard.Services.Stores;

// ═══════════════════════════════════════════════════════════════════════════
//  LoanModelSubstituteStore
//
//  Which customer models each loan fleet model can be loaned out in place of
//  (see LoanModelSubstitute), keyed by the loan model's JMS model_id. Stored
//  as one (loan model, substitute model) row per pair and grouped back into
//  LoanModelSubstitute on load, so a loan model with no substitutes left
//  simply has no rows.
//
//  LoanEquipmentManager.razor's "Model Substitutes" tab is the UI for this.
// ═══════════════════════════════════════════════════════════════════════════

public class LoanModelSubstituteStore
{
    private readonly IDbContextFactory<WorkshopDbContext> _dbFactory;

    public LoanModelSubstituteStore(IDbContextFactory<WorkshopDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<List<LoanModelSubstitute>> LoadAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var pairs = await db.LoanModelSubstitutes.AsNoTracking().ToListAsync();

        return pairs
            .GroupBy(p => p.LoanModelId)
            .OrderBy(g => g.Key)
            .Select(g => new LoanModelSubstitute
            {
                LoanModelId = g.Key,
                SubstituteModelIds = g.Select(p => p.SubstituteModelId).ToList()
            })
            .ToList();
    }

    public async Task AddAsync(int loanModelId, int substituteModelId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        if (await db.LoanModelSubstitutes.FindAsync(loanModelId, substituteModelId) is not null) return;

        db.LoanModelSubstitutes.Add(new LoanModelSubstitutePair { LoanModelId = loanModelId, SubstituteModelId = substituteModelId });
        await db.SaveChangesAsync();
    }

    public async Task RemoveAsync(int loanModelId, int substituteModelId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await db.LoanModelSubstitutes
            .Where(p => p.LoanModelId == loanModelId && p.SubstituteModelId == substituteModelId)
            .ExecuteDeleteAsync();
    }
}
