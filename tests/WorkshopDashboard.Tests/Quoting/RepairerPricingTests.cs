using WorkshopDashboard.Services.Quoting.Bayside;
using WorkshopDashboard.Services.Quoting.Cobalt;
using WorkshopDashboard.Services.Quoting.Ember;
using WorkshopDashboard.Services.Quoting.Falcon;

namespace WorkshopDashboard.Tests.Quoting;

// The simpler repairers: straight convert-and-markup (Bayside, Cobalt, Ember), and Falcon's
// cost → charge lookup table.
public class RepairerPricingTests
{
    // ── Convert and mark up: (USD / 0.69) × markup ──

    [Fact]
    public void Bayside_ConvertsAndMarksUpByOnePointFour()
    {
        Assert.Equal(1400m, BaysidePricingConfig.Calculate(690m).Total);
    }

    [Fact]
    public void Cobalt_ConvertsAndMarksUpByOnePointThreeFive()
    {
        Assert.Equal(1350m, CobaltPricingConfig.Calculate(690m).Total);
    }

    [Fact]
    public void Ember_UsesTheSameFormulaAsCobalt()
    {
        Assert.Equal(CobaltPricingConfig.Calculate(123.45m).Total, EmberPricingConfig.Calculate(123.45m).Total);
    }

    [Fact]
    public void ConvertAndMarkUp_RoundsToTheCent()
    {
        Assert.Equal(195.65m, CobaltPricingConfig.Calculate(100m).Total); // 100 / 0.69 × 1.35 = 195.6521...
    }

    [Fact]
    public void ConvertAndMarkUp_RejectsAZeroRate()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BaysidePricingConfig.Calculate(100m, 0m));
        Assert.Throws<ArgumentOutOfRangeException>(() => CobaltPricingConfig.Calculate(100m, 0m));
        Assert.Throws<ArgumentOutOfRangeException>(() => EmberPricingConfig.Calculate(100m, 0m));
    }

    // ── Falcon lookup table ──

    [Theory]
    [InlineData(19.95, 40)]
    [InlineData(49.20, 75)]
    [InlineData(995.00, 1305)]
    public void Falcon_ExactCost_UsesTheTableCharge(double cost, double charge)
    {
        Assert.Equal((decimal)charge, FalconPricingConfig.GetCustomerPrice((decimal)cost));
    }

    [Fact]
    public void Falcon_CostBetweenRows_IsInterpolatedAndRoundedToFiveDollars()
    {
        // Halfway between 36.90 → 60 and 49.20 → 75 is 67.50, which rounds away from zero to 70.
        Assert.Equal(70m, FalconPricingConfig.GetCustomerPrice(43.05m));
    }

    [Fact]
    public void Falcon_CostOutsideTheTable_IsClampedToTheEnds()
    {
        Assert.Equal(40m, FalconPricingConfig.GetCustomerPrice(5m));
        Assert.Equal(1305m, FalconPricingConfig.GetCustomerPrice(5000m));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-10)]
    public void Falcon_NoCost_IsNoCharge(double cost)
    {
        Assert.Equal(0m, FalconPricingConfig.GetCustomerPrice((decimal)cost));
    }
}
