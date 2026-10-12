namespace WorkshopDashboard.Services.Quoting.Cobalt;

public class CobaltPricingResult
{
    public decimal CostUsd { get; init; }
    public decimal AudUsdRate { get; init; }
    public decimal Total { get; init; }   // (CostUsd / AudUsdRate) * Markup
}

public static class CobaltPricingConfig
{
    /// <summary>AUD/USD rate — dividing a USD figure by this gives AUD.</summary>
    public const decimal DefaultAudUsdRate = 0.69m;

    /// <summary>Markup applied after the currency conversion.</summary>
    public const decimal Markup = 1.35m;

    /// <summary>
    /// =(cost/0.69)*1.35 — converts a Cobalt USD cost to our AUD charge to the customer.
    /// </summary>
    public static CobaltPricingResult Calculate(decimal costUsd, decimal? audUsdRate = null)
    {
        var rate = audUsdRate ?? DefaultAudUsdRate;
        if (rate <= 0m)
            throw new ArgumentOutOfRangeException(nameof(audUsdRate), "AUD/USD rate must be greater than zero.");

        var total = (costUsd / rate) * Markup;

        return new CobaltPricingResult
        {
            CostUsd = Math.Round(costUsd, 2, MidpointRounding.AwayFromZero),
            AudUsdRate = rate,
            Total = Math.Round(total, 2, MidpointRounding.AwayFromZero),
        };
    }
}
