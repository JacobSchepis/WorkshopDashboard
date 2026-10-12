using WorkshopDashboard.Services.Quoting.Ember;
using WorkshopDashboard.Services.Quoting.Shipping;

namespace WorkshopDashboard.Tests.Quoting;

// Freight is folded into the flex/probe quotes' repair line rather than posted on its own.
public class FreightTests
{
    [Fact]
    public void FreightFor_IsTheZonesFlexPrice()
    {
        foreach (var (id, group) in ShippingConfig.Groups)
            Assert.Equal(group.FlexPrice, ShippingConfig.FreightFor(id));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(999)]
    public void FreightFor_NoOrUnknownZone_IsZero(int? groupId)
    {
        Assert.Equal(0m, ShippingConfig.FreightFor(groupId));
    }

    [Fact]
    public void EmberRepairs_ShowSubTotalFreightAndTotal()
    {
        var quote = new EmberQuoteExtraction { RepairsRequired = { "Replace lens", " " } };

        var text = EmberServiceDescriptions.GetRepairsDescription(quote, 1234.5m, 50m);

        Assert.Equal("Repairs Required:\n\n• Replace lens\n\nSub Total:  $1,234.50\nFreight:  $50.00\nTotal Cost: ", text);
    }

    [Fact]
    public void EmberRepairs_WithNoFreight_LeaveTheFreightLineOut()
    {
        var text = EmberServiceDescriptions.GetRepairsDescription(new EmberQuoteExtraction(), 100m, 0m);

        Assert.DoesNotContain("Freight", text);
        Assert.Contains("None noted.", text);
    }
}
