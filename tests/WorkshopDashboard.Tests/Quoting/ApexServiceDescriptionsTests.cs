using WorkshopDashboard.Services.Quoting.Apex;

namespace WorkshopDashboard.Tests.Quoting;

// The service line text shared by the Apex and in-house quote pages.
public class ApexServiceDescriptionsTests
{
    [Fact]
    public void Observations_AreBulleted_AndBlankRowsDropped()
    {
        var text = ApexServiceDescriptions.GetTechnicalObservationsDescription(new[] { " Insertion tube kinked. ", "", "  " , "Light guide damaged." });

        Assert.Equal("Technical Observations:\n\n• Insertion tube kinked.\n• Light guide damaged.", text);
    }

    [Fact]
    public void Repairs_ListTheLinesAndTheBreakdown()
    {
        var text = ApexServiceDescriptions.GetRepairsDescription(new[] { "Replace light guide", "" }, 2310m, -231m, -150m, 1929m);

        Assert.Equal(
            "Repairs Required:\n\n• Replace light guide\n\nSub Total:  $2,310\nLabour Component Removed:  -$231\nDiscount:  -$150\nTotal Cost: ",
            text);
    }

    [Fact]
    public void Repairs_WithNoDiscounts_LeaveThoseLinesOut()
    {
        var text = ApexServiceDescriptions.GetRepairsDescription(new[] { "Reseal control body" }, 900m, 0m, 0m, 900m);

        Assert.DoesNotContain("Labour", text);
        Assert.DoesNotContain("Discount", text);
    }

    [Fact]
    public void Freight_SitsBetweenTheDiscountsAndTheTotal()
    {
        var text = ApexServiceDescriptions.GetRepairsDescription(new[] { "Rebuild bending section" }, 2310m, 0m, -150m, 2210m, freight: 50m);

        Assert.EndsWith("Sub Total:  $2,310\nDiscount:  -$150\nFreight:  $50\nTotal Cost: ", text);
    }

    [Fact]
    public void WorkOrderNotes_GiveTheCompletionDays()
    {
        Assert.Equal("Work Order Notes:\n\nEstimated time of completion: 11 working days.",
            ApexServiceDescriptions.GetWorkOrderNotesDescription(11));
    }
}
