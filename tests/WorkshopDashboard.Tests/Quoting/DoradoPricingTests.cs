using WorkshopDashboard.Services.Quoting.Dorado;

namespace WorkshopDashboard.Tests.Quoting;

// Dorado: rigid scopes are priced off the company's level table plus rod lens extras (capped at
// the group's max for standard scopes); anything without a level goes through Apex pricing.
public class DoradoPricingTests
{
    // Group 1: Level 1 650, Level 2 750, Level 3 850, max 950.
    private static readonly DoradoPricingGroup Group1 = DoradoPricingConfig.Groups[1];

    private static DoradoQuoteExtraction Quote(string level, int rodLenses = 0, int rodLenses4k = 0,
        bool specialty = false, decimal specialtyPrice = 0m, decimal total = 0m) => new()
    {
        WorksRequired = level,
        AdditionalRodLenses = rodLenses,
        AdditionalRodLenses4k = rodLenses4k,
        IsSpecialtyScope = specialty,
        SpecialtyScopePrice = specialtyPrice,
        TotalPrice = total,
    };

    [Theory]
    [InlineData("Level 1", 650)]
    [InlineData("Level 2", 750)]
    [InlineData("Level 3", 850)]
    [InlineData("  level 2 ", 750)]
    public void Level_IsPricedFromTheGroupTable(string level, decimal expected)
    {
        var result = DoradoPricingCalculator.Calculate(Quote(level), Group1)!;

        Assert.Equal(DoradoPricingMode.Level, result.Mode);
        Assert.Equal(expected, result.Total);
    }

    [Fact]
    public void Unrepairable_IsAZeroDollarLevel()
    {
        var result = DoradoPricingCalculator.Calculate(Quote("Unrepairable"), Group1)!;

        Assert.Equal(DoradoPricingMode.Level, result.Mode);
        Assert.True(result.IsEmpty);
    }

    [Fact]
    public void RodLenses_UnderTheCap_AreChargedInFull()
    {
        var result = DoradoPricingCalculator.Calculate(Quote("Level 1", rodLenses: 2), Group1)!; // 650 + 200

        Assert.Equal(200m, result.RodLensesChargedPrice);
        Assert.Equal(0m, result.RodLensDiscount);
        Assert.Equal(850m, result.Total);
    }

    [Fact]
    public void RodLenses_OverTheCap_AreDiscountedDownToTheGroupMax()
    {
        var result = DoradoPricingCalculator.Calculate(Quote("Level 2", rodLenses: 3), Group1)!; // 750 + 300 = 1050

        Assert.Equal(300m, result.RodLensesFullPrice);
        Assert.Equal(100m, result.RodLensDiscount);
        Assert.Equal(950m, result.Total);
    }

    [Fact]
    public void FourKRodLenses_AreNeverCapped()
    {
        // Level 3 (850) + 2 standard lenses (200) goes over the cap: 100 off. The 2 × 115 4K lenses sit on top.
        var result = DoradoPricingCalculator.Calculate(Quote("Level 3", rodLenses: 2, rodLenses4k: 2), Group1)!;

        Assert.Equal(100m, result.RodLensDiscount);
        Assert.Equal(230m, result.RodLenses4kChargedPrice);
        Assert.Equal(0m, result.RodLenses4kDiscount);
        Assert.Equal(1180m, result.Total);
    }

    [Fact]
    public void SpecialtyScope_DoublesTheSurcharge_AndSkipsTheCap()
    {
        var result = DoradoPricingCalculator.Calculate(
            Quote("Level 2", rodLenses: 3, specialty: true, specialtyPrice: 100m), Group1)!;

        Assert.Equal(200m, result.SpecialtySurcharge);
        Assert.Equal(0m, result.RodLensDiscount);
        Assert.Equal(750m + 200m + 300m, result.Total);
    }

    [Fact]
    public void NoLevel_IsPricedThroughApex()
    {
        var result = DoradoPricingCalculator.Calculate(Quote("", total: 1000m), group: null)!;

        Assert.Equal(DoradoPricingMode.Apex, result.Mode);
        Assert.False(result.HasLevel);
        Assert.NotNull(result.Apex);
        Assert.Equal(2080m, result.Total); // same as ApexPricingTests.DefaultRate_ConvertsUsdToAudBeforeTheMarkup
    }

    [Fact]
    public void LevelQuote_ForACompanyWithNoGroup_CannotBePriced()
    {
        Assert.Null(DoradoPricingCalculator.Calculate(Quote("Level 1"), group: null));
        Assert.Null(DoradoPricingCalculator.Calculate(Quote("Level 1"), companyId: -1));
    }

    [Fact]
    public void CompanyId_LooksUpItsGroup()
    {
        var result = DoradoPricingCalculator.Calculate(Quote("Level 2"), companyId: 2)!; // company 2 → group 2

        Assert.Equal(DoradoPricingConfig.Groups[2].Level2, result.Total);
    }

    [Theory]
    [InlineData("level 3", "Level 3")]
    [InlineData("  UNREPAIRABLE ", "Unrepairable")]
    [InlineData("Level 9", "")]
    [InlineData(null, "")]
    public void Levels_AreNormalisedToTheirCanonicalName(string? raw, string expected)
    {
        Assert.Equal(expected, DoradoLevels.Normalise(raw));
    }
}
