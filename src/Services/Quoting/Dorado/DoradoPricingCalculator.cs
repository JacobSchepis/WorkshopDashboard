using WorkshopDashboard.Services.Quoting.Apex;
using WorkshopDashboard.Services.Quoting.Dorado;

public enum DoradoPricingMode
{
    /// <summary>Rigid scope — priced off the level line plus rod lens extras.</summary>
    Level,

    /// <summary>Semi-rigid / specialty scope with no level — the quote total goes through Apex pricing.</summary>
    Apex,
}

public static class DoradoPricingCalculator
{
    public const decimal RodLensUnitPrice = 100.00m;
    public const decimal RodLens4kUnitPrice = 115.00m;

    /// <summary>
    /// Prices a Dorado quote.
    /// Returns null only when the quote has a level repair but the company has no Dorado pricing
    /// group configured — semi-rigid quotes always price, since Apex pricing needs no Dorado group.
    /// </summary>
    public static DoradoQuoteCalculation? Calculate(
        DoradoQuoteExtraction quote,
        DoradoPricingGroup? group,
        int apexGroup = 1,
        decimal? audUsdRate = null)
    {
        // No level line → semi-rigid. Take the quote total straight into Apex pricing.
        if (!DoradoLevels.IsKnown(quote.WorksRequired))
            return CalculateApex(quote, apexGroup, audUsdRate);

        if (group == null) return null;
        return CalculateLevel(quote, group);
    }

    public static DoradoQuoteCalculation? Calculate(
        DoradoQuoteExtraction quote,
        int companyId,
        decimal? audUsdRate = null)
    {
        var group = DoradoPricingConfig.GetGroupForCompany(companyId);
        var apexGroup = ApexPricingConfig.GetGroupForCompany(companyId);
        return Calculate(quote, group, apexGroup, audUsdRate);
    }

    /// <summary>
    /// Semi-rigid / no-level path. The Dorado total already includes rod lenses and any specialty
    /// line, so nothing is added on top — the whole quote becomes a single Apex-priced service.
    /// </summary>
    private static DoradoQuoteCalculation CalculateApex(DoradoQuoteExtraction quote, int apexGroup, decimal? audUsdRate)
    {
        var apex = ApexPricingConfig.Calculate(quote.TotalPrice, apexGroup, audUsdRate);

        return new DoradoQuoteCalculation
        {
            Mode = DoradoPricingMode.Apex,
            HasLevel = false,
            LevelName = string.Empty,
            Apex = apex,
            BaseServicePrice = apex.Total,
            Total = apex.Total,
        };
    }

    /// <summary>
    /// Rigid path — unchanged behaviour: level price from the group, rod lens extras on top,
    /// capped at the group's max price for standard scopes. Dorado's specialty surcharge is double
    /// the specialty price on their quote.
    /// </summary>
    private static DoradoQuoteCalculation CalculateLevel(DoradoQuoteExtraction quote, DoradoPricingGroup group) =>
        CalculateLevel(
            quote.WorksRequired, group,
            specialtySurcharge: quote.IsSpecialtyScope ? quote.SpecialtyScopePrice * 2 : null,
            quote.AdditionalRodLenses, quote.AdditionalRodLenses4k);

    /// <summary>
    /// Prices a rigid level repair from its parts — also used directly by QuoteInHouseRigid,
    /// which has no Dorado quote. A non-null <paramref name="specialtySurcharge"/> marks a
    /// specialty scope: the surcharge is added and the group's max price cap no longer applies.
    /// </summary>
    public static DoradoQuoteCalculation CalculateLevel(
        string level, DoradoPricingGroup group, decimal? specialtySurcharge, int rodLenses, int rodLenses4k)
    {
        var basePrice = group.TryGetLevel(level) ?? 0m;

        // Standard rod lenses ($100 each). The max price cap only applies to standard scopes.
        var rodLensesFullPrice = rodLenses * RodLensUnitPrice;
        decimal rodLensDiscount = 0;

        if (specialtySurcharge == null)
        {
            var uncappedTotal = basePrice + rodLensesFullPrice;
            if (uncappedTotal > group.MaxPrice)
                rodLensDiscount = uncappedTotal - group.MaxPrice;
        }

        var rodLensesCharged = rodLensesFullPrice - rodLensDiscount;

        // 4K rod lenses — no cap applied.
        var rodLenses4kFull = rodLenses4k * RodLens4kUnitPrice;

        var surcharge = specialtySurcharge ?? 0m;

        return new DoradoQuoteCalculation
        {
            Mode = DoradoPricingMode.Level,
            HasLevel = true,
            LevelName = level.Trim(),
            BaseServicePrice = basePrice,
            SpecialtySurcharge = surcharge,
            RodLensesFullPrice = rodLensesFullPrice,
            RodLensDiscount = rodLensDiscount,
            RodLensesChargedPrice = rodLensesCharged,
            RodLenses4kFullPrice = rodLenses4kFull,
            RodLenses4kDiscount = 0,
            RodLenses4kChargedPrice = rodLenses4kFull,
            Total = basePrice + surcharge + rodLensesCharged + rodLenses4kFull,
        };
    }
}

public class DoradoQuoteCalculation
{
    public DoradoPricingMode Mode { get; init; }

    /// <summary>False for semi-rigid quotes priced through Apex.</summary>
    public bool HasLevel { get; init; }
    public string LevelName { get; init; } = string.Empty;

    /// <summary>Populated only in <see cref="DoradoPricingMode.Apex"/> mode.</summary>
    public ApexPricingResult? Apex { get; init; }

    public decimal BaseServicePrice { get; init; }

    // Specialty scope surcharge (level mode only)
    public decimal SpecialtySurcharge { get; init; }

    // Standard rod lenses (level mode only)
    public decimal RodLensesFullPrice { get; init; }
    public decimal RodLensDiscount { get; init; }
    public decimal RodLensesChargedPrice { get; init; }

    // 4K rod lenses (level mode only)
    public decimal RodLenses4kFullPrice { get; init; }
    public decimal RodLenses4kDiscount { get; init; }
    public decimal RodLenses4kChargedPrice { get; init; }

    public decimal Total { get; init; }

    public bool IsEmpty => Total == 0m;
}
