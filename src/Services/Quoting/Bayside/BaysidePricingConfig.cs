namespace WorkshopDashboard.Services.Quoting.Bayside;

public class BaysidePricingResult
{
    public decimal CostUsd { get; init; }
    public decimal AudUsdRate { get; init; }
    public decimal Total { get; init; }   // (CostUsd / AudUsdRate) * Markup
}

public static class BaysidePricingConfig
{
    /// <summary>AUD/USD rate — dividing a USD figure by this gives AUD.</summary>
    public const decimal DefaultAudUsdRate = 0.69m;

    /// <summary>Markup applied after the currency conversion.</summary>
    public const decimal Markup = 1.4m;

    /// <summary>
    /// =(cost/0.69)*1.4 — same shape as Cobalt/Ember, but with a 1.4 markup instead of 1.35.
    /// </summary>
    public static BaysidePricingResult Calculate(decimal costUsd, decimal? audUsdRate = null)
    {
        var rate = audUsdRate ?? DefaultAudUsdRate;
        if (rate <= 0m)
            throw new ArgumentOutOfRangeException(nameof(audUsdRate), "AUD/USD rate must be greater than zero.");

        var total = (costUsd / rate) * Markup;

        return new BaysidePricingResult
        {
            CostUsd = Math.Round(costUsd, 2, MidpointRounding.AwayFromZero),
            AudUsdRate = rate,
            Total = Math.Round(total, 2, MidpointRounding.AwayFromZero),
        };
    }
}
