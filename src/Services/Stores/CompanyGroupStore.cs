using Microsoft.EntityFrameworkCore;
using WorkshopDashboard.Data;

namespace WorkshopDashboard.Services.Stores;

/// <summary>
/// "Company ID → saved shipping/pricing group" overrides used by the quote extractor pages.
/// Each page passes its own scope (e.g. "apex_company_pricing_groups") so groups don't collide
/// across repairers. Synchronous because the pages call it from synchronous handlers; each
/// call is a single small query.
/// </summary>
public class CompanyGroupStore
{
    private readonly IDbContextFactory<WorkshopDbContext> _dbFactory;

    public CompanyGroupStore(IDbContextFactory<WorkshopDbContext> dbFactory) => _dbFactory = dbFactory;

    public Dictionary<int, int> LoadSaved(string scope)
    {
        using var db = _dbFactory.CreateDbContext();
        return db.CompanyGroupOverrides.Where(o => o.Scope == scope).ToDictionary(o => o.CompanyId, o => o.GroupId);
    }

    public void Save(string scope, int companyId, int groupId)
    {
        using var db = _dbFactory.CreateDbContext();
        var existing = db.CompanyGroupOverrides.Find(scope, companyId);
        if (existing is null)
            db.CompanyGroupOverrides.Add(new CompanyGroupOverride { Scope = scope, CompanyId = companyId, GroupId = groupId });
        else
            existing.GroupId = groupId;
        db.SaveChanges();
    }
}
