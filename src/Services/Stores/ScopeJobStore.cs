using Microsoft.EntityFrameworkCore;
using WorkshopDashboard.Data;

namespace WorkshopDashboard.Services.Stores;

// ═══════════════════════════════════════════════════════════════════════════
//  ScopeJobStore
//
//  The single definition of "what is a scope job" (i.e. a workshop job every
//  StaleJobs-style page should be counting). A job is in scope when its
//  history includes status 34 or 16 (see JmsQueries.WorkshopStatusIds) AND it
//  isn't excluded by job id, owner company id, or equipment model id.
//
//  Exclusions are ScopeExclusion rows, one per (kind, id). Pages call
//  LoadAsync() once per page load and pass the result straight into whichever
//  JmsQueries.*Query method they need; that method calls
//  JmsQueries.ScopeJobClause internally, so there is exactly one place that
//  knows what "in scope" means.
// ═══════════════════════════════════════════════════════════════════════════

public class ScopeJobExclusions
{
    public HashSet<int> JobIds { get; init; } = new();
    public HashSet<int> CompanyIds { get; init; } = new();
    public HashSet<int> ModelIds { get; init; } = new();
}

public class ScopeJobStore
{
    private readonly IDbContextFactory<WorkshopDbContext> _dbFactory;

    public ScopeJobStore(IDbContextFactory<WorkshopDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<ScopeJobExclusions> LoadAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var rows = await db.ScopeExclusions.ToListAsync();

        HashSet<int> IdsOf(ScopeExclusionKind kind) => rows.Where(r => r.Kind == kind).Select(r => r.Id).ToHashSet();

        return new()
        {
            JobIds = IdsOf(ScopeExclusionKind.Job),
            CompanyIds = IdsOf(ScopeExclusionKind.Company),
            ModelIds = IdsOf(ScopeExclusionKind.Model)
        };
    }

    public Task AddJobAsync(int jobId) => AddAsync(ScopeExclusionKind.Job, jobId);
    public Task RemoveJobAsync(int jobId) => RemoveAsync(ScopeExclusionKind.Job, jobId);

    public Task AddCompanyAsync(int companyId) => AddAsync(ScopeExclusionKind.Company, companyId);
    public Task RemoveCompanyAsync(int companyId) => RemoveAsync(ScopeExclusionKind.Company, companyId);

    public Task AddModelAsync(int modelId) => AddAsync(ScopeExclusionKind.Model, modelId);
    public Task RemoveModelAsync(int modelId) => RemoveAsync(ScopeExclusionKind.Model, modelId);

    private async Task AddAsync(ScopeExclusionKind kind, int id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        if (await db.ScopeExclusions.AnyAsync(x => x.Kind == kind && x.Id == id)) return;
        db.ScopeExclusions.Add(new ScopeExclusion { Kind = kind, Id = id });
        await db.SaveChangesAsync();
    }

    private async Task RemoveAsync(ScopeExclusionKind kind, int id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await db.ScopeExclusions.Where(x => x.Kind == kind && x.Id == id).ExecuteDeleteAsync();
    }
}
