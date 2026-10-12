using WorkshopDashboard.Services.Quoting;
using WorkshopDashboard.Services.Quoting.Dorado;

namespace WorkshopDashboard.Tests.Quoting;

// The in-house rigid page prices from its parts (level, $200 specialty flag, rod lens counts)
// rather than from a Dorado quote.
public class InHouseRigidPricingTests
{
    private static readonly DoradoPricingGroup Group = new() { Level1 = 650, Level2 = 750, Level3 = 850, MaxPrice = 950 };

    [Fact]
    public void LevelOnly_IsTheLevelPrice()
    {
        var calc = DoradoPricingCalculator.CalculateLevel("Level 2", Group, specialtySurcharge: null, rodLenses: 0, rodLenses4k: 0);

        Assert.Equal(750m, calc.Total);
        Assert.Equal(0m, calc.SpecialtySurcharge);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Level0_Is220_InEveryGroup(int groupId)
    {
        var calc = DoradoPricingCalculator.CalculateLevel("Level 0", DoradoPricingConfig.Groups[groupId], null, rodLenses: 0, rodLenses4k: 0);

        Assert.Equal(220m, calc.Total);
    }

    [Fact]
    public void Level0_SpecialtyScope_AddsTheSurcharge()
    {
        var calc = DoradoPricingCalculator.CalculateLevel("Level 0", Group, specialtySurcharge: 200m, rodLenses: 0, rodLenses4k: 0);

        Assert.Equal(420m, calc.Total);
    }

    [Fact]
    public void Level0_IsInHouseOnly()
    {
        Assert.True(DoradoLevels.IsKnownInHouse("Level 0"));
        Assert.False(DoradoLevels.IsKnown("Level 0"));
        Assert.DoesNotContain("Level 0", DoradoLevels.All);
    }

    [Fact]
    public void Level0_HasExteriorCleanWording()
    {
        var text = DoradoServiceDescriptions.GetTechServiceDescription("Level 0", new[] { "Dirty exterior" });

        Assert.Contains("Repair inclusive of: Thorough diagnostic testing and external cleaning of scope.", text);
    }

    [Fact]
    public void RodLenses_AreCappedAtTheGroupMax_ForStandardScopes()
    {
        var calc = DoradoPricingCalculator.CalculateLevel("Level 3", Group, null, rodLenses: 2, rodLenses4k: 0);

        Assert.Equal(200m, calc.RodLensesFullPrice);
        Assert.Equal(100m, calc.RodLensDiscount);
        Assert.Equal(950m, calc.Total);
    }

    [Fact]
    public void SpecialtyScope_AddsTheSurcharge_AndSkipsTheCap()
    {
        var calc = DoradoPricingCalculator.CalculateLevel("Level 3", Group, specialtySurcharge: 200m, rodLenses: 2, rodLenses4k: 0);

        Assert.Equal(0m, calc.RodLensDiscount);
        Assert.Equal(850m + 200m + 200m, calc.Total);
    }

    [Fact]
    public void FourKRodLenses_Are115Each_AndNeverCapped()
    {
        var calc = DoradoPricingCalculator.CalculateLevel("Level 3", Group, null, rodLenses: 0, rodLenses4k: 3);

        Assert.Equal(345m, calc.RodLenses4kChargedPrice);
        Assert.Equal(850m + 345m, calc.Total);
    }

    [Fact]
    public void SurchargeLine_WithNoDescription_HasNoBlankLine()
    {
        Assert.Equal("Specialty Scope Additional Cost:\n@ $200.00",
            DoradoServiceDescriptions.GetSpecialtySurchargeDescription("", 200m));
    }

    [Fact]
    public void WorkOrderNotes_GiveTheCompletionDays()
    {
        Assert.Equal("Work Order Notes:\n\nEstimated time of completion: 7 working days.", QuoteServiceText.WorkOrderNotes(7));
    }
}
