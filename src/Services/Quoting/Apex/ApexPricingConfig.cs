namespace WorkshopDashboard.Services.Quoting.Apex;

public class ApexPricingResult
{
    // ── Inputs / conversion ──
    public decimal CostUsd { get; init; }          // Apex's cost price, as quoted (USD)
    public decimal AudUsdRate { get; init; }       // AUD/USD rate used for the conversion
    public decimal CostAud { get; init; }          // CostUsd converted to AUD

    // ── Charge build-up ──
    public decimal BaseCharge { get; init; }       // D13 — CostAud * markup
    public decimal Discount1 { get; init; }        // D16
    public decimal Discount2 { get; init; }        // D17
    public decimal Discount3 { get; init; }        // D18
    public decimal GroupDiscount { get; init; }    // Group 2 extra 10% off base
    public decimal TotalDiscount { get; init; }    // D20

    // ── Totals ──
    public decimal UnroundedTotal { get; init; }   // D22 before tidying
    public decimal RoundingAdjustment { get; init; }
    public decimal Total { get; init; }            // What we actually charge
}

public static class ApexPricingConfig
{
    /// <summary>
    /// Fallback AUD/USD rate — i.e. how many USD one AUD buys. Dividing a USD figure by this
    /// gives AUD. Override per-call (or from config) rather than editing this in a hurry.
    /// </summary>
    public const decimal DefaultAudUsdRate = 0.65m;

    /// <summary>Markup applied to the AUD cost.</summary>
    public const decimal Markup = 1.4m;

    /// <summary>Final totals are rounded up to a multiple of this, to match Dorado's $x95 style pricing.</summary>
    public const decimal RoundToNearest = 5m;

    /// <summary>Extra discount for Group 2 companies, off the base charge (D13) — on top of
    /// whatever the volume discount tiers below already take off.</summary>
    private const decimal Group2DiscountRate = 0.08m;

    /// <summary>
    /// Volume discount tiers applied to the base charge (D13), most-junior tier first. Each
    /// tier discounts the portion of BaseCharge above its Threshold, at Rate, up to Cap (or
    /// uncapped when Cap is null). Mirrors the D16/D17/D18 cells of the original pricing sheet
    /// this formula was ported from.
    /// </summary>
    private static readonly VolumeDiscountTier[] VolumeDiscountTiers =
    {
        new(Threshold: 1200m, Rate: 0.08m, Cap: 200m),   // D16
        new(Threshold: 3500m, Rate: 0.20m, Cap: 500m),   // D17
        new(Threshold: 5500m, Rate: 0.25m, Cap: null),   // D18
    };

    /// <summary>If the D18 tier's discountable excess reaches this much, something's off with
    /// the quote — stop and get a human to check it rather than pricing it automatically.</summary>
    private const decimal D18ExcessSafetyCeiling = 180000m;

    private readonly record struct VolumeDiscountTier(decimal Threshold, decimal Rate, decimal? Cap);

    // Company ID → group (1 = standard, 2 = standard + 8% off base)
    public static readonly Dictionary<int, int> CompanyGroups = new()
    {
        // e.g. { 123, 2 }
    };

    public static int GetGroupForCompany(int companyId) =>
        CompanyGroups.TryGetValue(companyId, out var g) ? g : 1;

    /// <summary>
    /// Converts a USD figure to AUD at the given AUD/USD rate.
    /// </summary>
    public static decimal ConvertUsdToAud(decimal usd, decimal audUsdRate)
    {
        if (audUsdRate <= 0m)
            throw new ArgumentOutOfRangeException(nameof(audUsdRate), "AUD/USD rate must be greater than zero.");
        return usd / audUsdRate;
    }

    /// <summary>
    /// Rounds up to the next multiple of <paramref name="step"/>. Always rounds up so we never
    /// end up charging under the calculated price.
    /// </summary>
    public static decimal RoundUpTo(decimal value, decimal step)
    {
        if (step <= 0m) return Math.Round(value, 2, MidpointRounding.AwayFromZero);
        return Math.Ceiling(value / step) * step;
    }

    /// <summary>Discount for the portion of <paramref name="value"/> above the tier's
    /// Threshold, at its Rate, capped at its Cap. Zero when value doesn't reach the threshold.</summary>
    private static decimal TierDiscount(decimal value, VolumeDiscountTier tier)
    {
        var excess = value - tier.Threshold;
        if (excess <= 0m) return 0m;

        var discount = excess * tier.Rate;
        if (tier.Cap.HasValue)
            discount = Math.Min(discount, tier.Cap.Value);

        return -discount;
    }

    /// <summary>
    /// Calculates our AUD charge to the customer given Apex's USD cost price.
    /// </summary>
    /// <param name="apexCostUsd">Apex's cost price in USD, as it appears on their quote.</param>
    /// <param name="group">1 = standard, 2 = standard + 8% off base.</param>
    /// <param name="audUsdRate">AUD/USD rate. Falls back to <see cref="DefaultAudUsdRate"/>.</param>
    public static ApexPricingResult Calculate(decimal apexCostUsd, int group, decimal? audUsdRate = null)
    {
        var rate = audUsdRate ?? DefaultAudUsdRate;

        // USD → AUD, then markup. (Previously this was a single `/ rate * markup` expression
        // that hid the fact that the divide is a currency conversion, not part of the margin.)
        var costAud = ConvertUsdToAud(apexCostUsd, rate);
        var baseCharge = costAud * Markup;

        var d18Excess = baseCharge - VolumeDiscountTiers[2].Threshold;
        if (d18Excess >= D18ExcessSafetyCeiling)
            throw new Exception("Quote total exceeds threshold — get someone to check it before quoting.");

        var d16 = TierDiscount(baseCharge, VolumeDiscountTiers[0]);
        var d17 = TierDiscount(baseCharge, VolumeDiscountTiers[1]);
        var d18 = TierDiscount(baseCharge, VolumeDiscountTiers[2]);

        // Group 2: extra 8% off the base charge (D13), applied as an additional discount
        decimal groupDiscount = group == 2 ? -(baseCharge * Group2DiscountRate) : 0m;

        var totalDiscount = d16 + d17 + d18 + groupDiscount;
        var unroundedTotal = baseCharge + totalDiscount;

        // Tidy the final figure only — the build-up stays exact so the breakdown still adds up
        // to UnroundedTotal, and the rounding shows as its own line.
        var total = RoundUpTo(unroundedTotal, RoundToNearest);

        return new ApexPricingResult
        {
            CostUsd = Math.Round(apexCostUsd, 2, MidpointRounding.AwayFromZero),
            AudUsdRate = rate,
            CostAud = Math.Round(costAud, 2, MidpointRounding.AwayFromZero),
            BaseCharge = Math.Round(baseCharge, 2, MidpointRounding.AwayFromZero),
            Discount1 = Math.Round(d16, 2, MidpointRounding.AwayFromZero),
            Discount2 = Math.Round(d17, 2, MidpointRounding.AwayFromZero),
            Discount3 = Math.Round(d18, 2, MidpointRounding.AwayFromZero),
            GroupDiscount = Math.Round(groupDiscount, 2, MidpointRounding.AwayFromZero),
            TotalDiscount = Math.Round(totalDiscount, 2, MidpointRounding.AwayFromZero),
            UnroundedTotal = Math.Round(unroundedTotal, 2, MidpointRounding.AwayFromZero),
            RoundingAdjustment = Math.Round(total - unroundedTotal, 2, MidpointRounding.AwayFromZero),
            Total = total,
        };
    }
}
