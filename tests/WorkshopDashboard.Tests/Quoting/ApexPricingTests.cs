using WorkshopDashboard.Services.Quoting.Apex;

namespace WorkshopDashboard.Tests.Quoting;

// Apex: USD cost → AUD → 1.4x markup → volume discount tiers → group discount → round up to $5.
// Expected figures are worked by hand from the tier table in ApexPricingConfig.
public class ApexPricingTests
{
    [Fact]
    public void BelowTheFirstTier_IsJustConvertedAndMarkedUp()
    {
        // 850 USD at rate 1 → 850 AUD → 1190 base, under the 1200 threshold.
        var result = ApexPricingConfig.Calculate(850m, group: 1, audUsdRate: 1m);

        Assert.Equal(1190m, result.BaseCharge);
        Assert.Equal(0m, result.TotalDiscount);
        Assert.Equal(1190m, result.Total);
    }

    [Fact]
    public void DefaultRate_ConvertsUsdToAudBeforeTheMarkup()
    {
        // 1000 USD / 0.65 = 1538.46 AUD → 2153.85 base → first tier takes 8% of the 953.85
        // over 1200 (76.31) → 2077.54 → rounded up to 2080.
        var result = ApexPricingConfig.Calculate(1000m, group: 1);

        Assert.Equal(0.65m, result.AudUsdRate);
        Assert.Equal(1538.46m, result.CostAud);
        Assert.Equal(2153.85m, result.BaseCharge);
        Assert.Equal(-76.31m, result.Discount1);
        Assert.Equal(2080m, result.Total);
    }

    [Fact]
    public void LargeQuote_HitsEveryTier_WithTheFirstTwoCapped()
    {
        // 14000 base: tier 1 capped at 200, tier 2 capped at 500, tier 3 uncapped 25% of 8500.
        var result = ApexPricingConfig.Calculate(10000m, group: 1, audUsdRate: 1m);

        Assert.Equal(-200m, result.Discount1);
        Assert.Equal(-500m, result.Discount2);
        Assert.Equal(-2125m, result.Discount3);
        Assert.Equal(11175m, result.Total);
    }

    [Fact]
    public void GroupTwo_GetsAnExtraEightPercentOffTheBase()
    {
        var standard = ApexPricingConfig.Calculate(1000m, group: 1);
        var group2 = ApexPricingConfig.Calculate(1000m, group: 2);

        Assert.Equal(-172.31m, group2.GroupDiscount);
        Assert.Equal(0m, standard.GroupDiscount);
        Assert.Equal(1910m, group2.Total);
    }

    [Fact]
    public void Total_IsAlwaysRoundedUp_ToTheNextFiveDollars()
    {
        var result = ApexPricingConfig.Calculate(851m, group: 1, audUsdRate: 1m); // 1191.40 base

        Assert.Equal(1191.40m, result.UnroundedTotal);
        Assert.Equal(3.60m, result.RoundingAdjustment);
        Assert.Equal(1195m, result.Total);
    }

    [Fact]
    public void Breakdown_AddsUpToTheTotal()
    {
        var r = ApexPricingConfig.Calculate(4321m, group: 2);

        // Each line is rounded to the cent on its own, so the shown lines can be a cent out
        // from the shown totals — but never more.
        Assert.InRange(Math.Abs(r.UnroundedTotal - (r.BaseCharge + r.TotalDiscount)), 0m, 0.01m);
        Assert.InRange(Math.Abs(r.TotalDiscount - (r.Discount1 + r.Discount2 + r.Discount3 + r.GroupDiscount)), 0m, 0.02m);
        Assert.Equal(r.Total, r.UnroundedTotal + r.RoundingAdjustment);
    }

    [Fact]
    public void AbsurdlyLargeQuote_IsRefusedForAHumanToCheck()
    {
        // 133000 USD at rate 1 → 186200 base, 180700 over the top tier threshold.
        Assert.ThrowsAny<Exception>(() => ApexPricingConfig.Calculate(133000m, group: 1, audUsdRate: 1m));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-0.5)]
    public void ZeroOrNegativeRate_IsRejected(double rate)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ApexPricingConfig.Calculate(100m, 1, (decimal)rate));
    }

    [Theory]
    [InlineData(100, 5, 100)]
    [InlineData(100.01, 5, 105)]
    [InlineData(1.234, 0, 1.23)]
    public void RoundUpTo_NeverRoundsDown(double value, double step, double expected)
    {
        Assert.Equal((decimal)expected, ApexPricingConfig.RoundUpTo((decimal)value, (decimal)step));
    }

    [Fact]
    public void CompaniesWithoutAGroup_DefaultToGroupOne()
    {
        Assert.Equal(1, ApexPricingConfig.GetGroupForCompany(-1));
    }
}
