using Microsoft.EntityFrameworkCore;
using WorkshopDashboard.Data;
using WorkshopDashboard.Models;

namespace WorkshopDashboard.Services.Stores;

// ═══════════════════════════════════════════════════════════════════════════
//  CustomsInfoStore
//
//  Three cross-referencing tables — manufacturers, FDA 510(k)s, and models
//  (each model referencing one manufacturer by Name and one 510(k) by Number) —
//  used to generate the customs info form attached to a DHL booking.
//  Manufacturers/510(k)s are meant to be created once and reused across many
//  models (several models often share one 510(k)); the manager UI looks one up
//  by name/number as you type and offers to create it inline if it doesn't
//  exist yet, rather than making you pre-create it in a separate step.
//
//  CustomsInfoManager.razor is the CRUD UI for this data. InternationalShipping.razor
//  (Repair Request page, US/Equipment tab) reads it to flag which looked-up
//  equipment has customs info configured, and to generate the customs info PDF.
// ═══════════════════════════════════════════════════════════════════════════

public class CustomsInfoData
{
    public List<CustomsManufacturer> Manufacturers { get; set; } = new();
    public List<CustomsFda510k> Fda510ks { get; set; } = new();
    public List<CustomsModel> Models { get; set; } = new();
}

public class CustomsInfoStore
{
    private readonly IDbContextFactory<WorkshopDbContext> _dbFactory;

    public CustomsInfoStore(IDbContextFactory<WorkshopDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<CustomsInfoData> LoadAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return new CustomsInfoData
        {
            Manufacturers = await db.CustomsManufacturers.AsNoTracking().OrderBy(m => m.Name).ToListAsync(),
            Fda510ks = await db.CustomsFda510ks.AsNoTracking().OrderBy(f => f.Number).ToListAsync(),
            Models = (await db.CustomsModels.AsNoTracking().ToListAsync())
                .OrderBy(m => m.ModelLabel, StringComparer.OrdinalIgnoreCase).ToList()
        };
    }

    /// <summary>Resolves a model_id to its full customs profile — the model plus its
    /// referenced manufacturer and 510(k), if configured.</summary>
    public async Task<(CustomsModel? Model, CustomsManufacturer? Manufacturer, CustomsFda510k? Fda510k)> ResolveAsync(int modelId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var model = await db.CustomsModels.AsNoTracking().FirstOrDefaultAsync(m => m.ModelId == modelId);
        if (model is null) return (null, null, null);

        var manufacturer = await db.CustomsManufacturers.AsNoTracking().FirstOrDefaultAsync(m => m.Name == model.ManufacturerName);
        var fda510k = await db.CustomsFda510ks.AsNoTracking().FirstOrDefaultAsync(f => f.Number == model.Fda510kNumber);
        return (model, manufacturer, fda510k);
    }

    // ── Manufacturers ────────────────────────────────────────────────────

    public async Task AddManufacturerAsync(CustomsManufacturer manufacturer)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        if (await db.CustomsManufacturers.AnyAsync(m => m.Name == manufacturer.Name))
            throw new InvalidOperationException($"A manufacturer named \"{manufacturer.Name}\" already exists.");

        db.CustomsManufacturers.Add(manufacturer);
        await db.SaveChangesAsync();
    }

    public async Task UpdateManufacturerAsync(string originalName, CustomsManufacturer updated)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var existing = await db.CustomsManufacturers.FirstOrDefaultAsync(m => m.Name == originalName)
            ?? throw new InvalidOperationException($"Manufacturer \"{originalName}\" not found.");

        var renamed = !originalName.Equals(updated.Name, StringComparison.OrdinalIgnoreCase);
        if (renamed && await db.CustomsManufacturers.AnyAsync(m => m.Name == updated.Name))
            throw new InvalidOperationException($"A manufacturer named \"{updated.Name}\" already exists.");

        db.Entry(existing).CurrentValues.SetValues(updated);

        // Keep models' references in sync with a rename.
        if (renamed)
            await db.CustomsModels.Where(m => m.ManufacturerName == originalName)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.ManufacturerName, updated.Name));

        await db.SaveChangesAsync();
    }

    public async Task RemoveManufacturerAsync(string name)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await db.CustomsManufacturers.Where(m => m.Name == name).ExecuteDeleteAsync();
    }

    // ── FDA 510(k)s ───────────────────────────────────────────────────────

    public async Task AddFda510kAsync(CustomsFda510k fda510k)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        if (await db.CustomsFda510ks.AnyAsync(f => f.Number == fda510k.Number))
            throw new InvalidOperationException($"A 510(k) numbered \"{fda510k.Number}\" already exists.");

        db.CustomsFda510ks.Add(fda510k);
        await db.SaveChangesAsync();
    }

    public async Task UpdateFda510kAsync(string originalNumber, CustomsFda510k updated)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var existing = await db.CustomsFda510ks.FirstOrDefaultAsync(f => f.Number == originalNumber)
            ?? throw new InvalidOperationException($"510(k) \"{originalNumber}\" not found.");

        var renumbered = !originalNumber.Equals(updated.Number, StringComparison.OrdinalIgnoreCase);
        if (renumbered && await db.CustomsFda510ks.AnyAsync(f => f.Number == updated.Number))
            throw new InvalidOperationException($"A 510(k) numbered \"{updated.Number}\" already exists.");

        db.Entry(existing).CurrentValues.SetValues(updated);

        if (renumbered)
            await db.CustomsModels.Where(m => m.Fda510kNumber == originalNumber)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.Fda510kNumber, updated.Number));

        await db.SaveChangesAsync();
    }

    public async Task RemoveFda510kAsync(string number)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await db.CustomsFda510ks.Where(f => f.Number == number).ExecuteDeleteAsync();
    }

    // ── Models ────────────────────────────────────────────────────────────

    /// <summary>Adds a model, and — since the manager's add flow lets you reference a
    /// manufacturer/510(k) that doesn't exist yet by filling in its details inline —
    /// optionally creates those alongside it in the same save.</summary>
    public async Task AddModelAsync(CustomsModel model, CustomsManufacturer? newManufacturer, CustomsFda510k? newFda510k)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        if (await db.CustomsModels.AnyAsync(m => m.ModelId == model.ModelId))
            throw new InvalidOperationException($"Model {model.ModelId} already has customs info configured.");

        await AddReferencedAsync(db, newManufacturer, newFda510k);
        db.CustomsModels.Add(model);
        await db.SaveChangesAsync();
    }

    public async Task UpdateModelAsync(CustomsModel updated, CustomsManufacturer? newManufacturer, CustomsFda510k? newFda510k)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var existing = await db.CustomsModels.FindAsync(updated.ModelId)
            ?? throw new InvalidOperationException($"Model {updated.ModelId} not found.");

        await AddReferencedAsync(db, newManufacturer, newFda510k);
        db.Entry(existing).CurrentValues.SetValues(updated);
        await db.SaveChangesAsync();
    }

    public async Task RemoveModelAsync(int modelId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await db.CustomsModels.Where(m => m.ModelId == modelId).ExecuteDeleteAsync();
    }

    private static async Task AddReferencedAsync(WorkshopDbContext db, CustomsManufacturer? newManufacturer, CustomsFda510k? newFda510k)
    {
        if (newManufacturer is not null && !await db.CustomsManufacturers.AnyAsync(m => m.Name == newManufacturer.Name))
            db.CustomsManufacturers.Add(newManufacturer);

        if (newFda510k is not null && !await db.CustomsFda510ks.AnyAsync(f => f.Number == newFda510k.Number))
            db.CustomsFda510ks.Add(newFda510k);
    }
}
