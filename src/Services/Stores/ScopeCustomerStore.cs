using Microsoft.EntityFrameworkCore;
using WorkshopDashboard.Data;
using WorkshopDashboard.Models;

namespace WorkshopDashboard.Services.Stores;

// ═══════════════════════════════════════════════════════════════════════════
//  ScopeCustomerStore
//
//  The list of customers tracked for the Waiting Quote dashboard (and later,
//  bulk customer emails). Entries are identified by DisplayName, unique
//  case-insensitively (enforced by the database as well as checked here so
//  the page gets a readable message).
// ═══════════════════════════════════════════════════════════════════════════

public class ScopeCustomerStore
{
    private readonly IDbContextFactory<WorkshopDbContext> _dbFactory;

    public ScopeCustomerStore(IDbContextFactory<WorkshopDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<List<ScopeCustomer>> LoadAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.ScopeCustomers.AsNoTracking().OrderBy(c => c.DisplayName).ToListAsync();
    }

    public async Task AddAsync(ScopeCustomer customer)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        if (await db.ScopeCustomers.AnyAsync(c => c.DisplayName == customer.DisplayName))
            throw new InvalidOperationException($"A customer named \"{customer.DisplayName}\" already exists.");

        db.ScopeCustomers.Add(customer);
        await db.SaveChangesAsync();
    }

    public async Task UpdateAsync(string originalName, ScopeCustomer updated)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var existing = await db.ScopeCustomers.FirstOrDefaultAsync(c => c.DisplayName == originalName)
            ?? throw new InvalidOperationException($"Customer \"{originalName}\" not found.");

        if (!originalName.Equals(updated.DisplayName, StringComparison.OrdinalIgnoreCase)
            && await db.ScopeCustomers.AnyAsync(c => c.DisplayName == updated.DisplayName))
            throw new InvalidOperationException($"A customer named \"{updated.DisplayName}\" already exists.");

        db.Entry(existing).CurrentValues.SetValues(updated);
        await db.SaveChangesAsync();
    }

    public async Task RemoveAsync(string displayName)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await db.ScopeCustomers.Where(c => c.DisplayName == displayName).ExecuteDeleteAsync();
    }
}
