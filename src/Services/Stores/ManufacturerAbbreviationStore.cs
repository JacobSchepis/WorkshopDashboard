using Microsoft.EntityFrameworkCore;
using WorkshopDashboard.Data;

namespace WorkshopDashboard.Services.Stores;

/// <summary>Preferred short manufacturer names for equipment labels (manufacturer company_id →
/// e.g. "Olympus"), used by WaitingQuoteDetail and LoanEquipmentManager. Keyed by the id as a
/// string because that's how WaitingQuoteReport.AbbreviateEquipment looks them up. There's no
/// editing UI — edit the ManufacturerAbbreviations table directly.</summary>
public class ManufacturerAbbreviationStore
{
    private readonly IDbContextFactory<WorkshopDbContext> _dbFactory;

    public ManufacturerAbbreviationStore(IDbContextFactory<WorkshopDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Dictionary<string, string>> LoadAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.ManufacturerAbbreviations.ToDictionaryAsync(a => a.ManufacturerCompanyId.ToString(), a => a.Abbreviation);
    }
}
