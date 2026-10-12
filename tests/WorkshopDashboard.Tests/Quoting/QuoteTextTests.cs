using WorkshopDashboard.Services.Quoting.Dorado;
using WorkshopDashboard.Services.Quoting.Falcon;

namespace WorkshopDashboard.Tests.Quoting;

// What actually gets posted to JMS as quote line text, and the Falcon extraction's own
// arithmetic and note clean-up.
public class QuoteTextTests
{
    // ── Dorado descriptions ──

    [Fact]
    public void DoradoLevelDescription_IncludesObservationsAndTheLevelWording()
    {
        var text = DoradoServiceDescriptions.GetTechServiceDescription("Level 1", new[] { "Scratched window", "Chipped lens" });

        Assert.StartsWith("Technical Observation:\nScratched window\nChipped lens", text);
        Assert.Contains("Repair inclusive of: Thorough diagnostic testing", text);
    }

    [Fact]
    public void DoradoDescription_UnknownLevel_LeavesOutTheLevelWording()
    {
        var text = DoradoServiceDescriptions.GetTechServiceDescription("Level 9", new[] { "Scratched window" });

        Assert.DoesNotContain("Repair inclusive of", text);
    }

    [Fact]
    public void DoradoDescriptions_UseAustralianSpelling()
    {
        var text = DoradoServiceDescriptions.GetTechServiceDescription("Level 3", new[] { "Fiber damage near center" });

        Assert.Contains("Fibre damage near centre", text);
        Assert.DoesNotContain("Fiber", text);
    }

    [Fact]
    public void DoradoSemiRigidDescription_NeverShowsAPrice()
    {
        // The quote's own figures are Apex's USD cost — printing them would expose our buy price.
        var text = DoradoServiceDescriptions.GetSemiRigidDescription(new[] { "Bent shaft" }, "Straighten and reseal");

        Assert.DoesNotContain("$", text);
        Assert.Contains("Repair inclusive of: Straighten and reseal", text);
    }

    [Fact]
    public void RodLensDescription_ShowsTheDiscountOnlyWhenThereIsOne()
    {
        Assert.DoesNotContain("Discount", DoradoServiceDescriptions.GetRodLensDescription(2, 220m, 0m));
        Assert.Contains("Agreed Pricing Discount: -$130.00", DoradoServiceDescriptions.GetRodLensDescription(3, 330m, 130m));
    }

    // ── Falcon extraction ──

    private static FalconQuoteExtraction FalconQuote(decimal printedSubtotal) => new()
    {
        Subtotal = printedSubtotal,
        LineItems =
        {
            new FalconLineItem { Quantity = 2, UnitPrice = 10m, Identifier = "SN1" },
            new FalconLineItem { Quantity = 1, UnitPrice = 5m },
        }
    };

    [Fact]
    public void FalconTotals_AreWorkedOutFromTheLineItems()
    {
        var quote = FalconQuote(printedSubtotal: 25m);

        Assert.Equal(25m, quote.FalconLineTotal);
        Assert.Equal(2.50m, quote.Gst);
        Assert.Equal(27.50m, quote.TotalPrice);
        Assert.Equal(1, quote.MissingIdentifierCount);
    }

    [Theory]
    [InlineData(25.00, true)]
    [InlineData(25.01, true)]
    [InlineData(24.98, false)]
    public void FalconQuote_ReconcilesOnlyWhenTheLinesMatchThePrintedSubtotal(double printed, bool reconciles)
    {
        Assert.Equal(reconciles, FalconQuote((decimal)printed).Reconciles);
    }

    [Fact]
    public void FalconNotes_AreCleanedOfFillerDuplicatesAndPunctuation()
    {
        var item = new FalconLineItem
        {
            EquipmentType = "Scissors",
            Notes = { "- Note: cracked lens", "(worn seal)", "N/A", "surgical tool", "Scissors", "Cracked Lens", "  " }
        };

        Assert.Equal(new[] { "cracked lens", "worn seal" }, item.CleanNotes);
    }

    [Fact]
    public void FalconServiceDescription_IsHeaderThenIdentifierThenNotes()
    {
        var item = new FalconLineItem
        {
            EquipmentType = "Scissors",
            RepairAction = "Sharpen",
            Identifier = "ABC123",
            Notes = { "tips bent" }
        };

        Assert.Equal(string.Join(Environment.NewLine, "Scissors - Sharpen", "ABC123", "tips bent"), item.ServiceDescription);
    }

    [Fact]
    public void FalconHeader_WithoutARepairAction_IsJustTheEquipment()
    {
        Assert.Equal("Scissors", new FalconLineItem { EquipmentType = " Scissors " }.Header);
    }

    [Fact]
    public void FalconNotesEditor_SplitsOnLineBreaks()
    {
        var item = new FalconLineItem();
        item.SetNotesFromText("one\r\ntwo\nthree");

        Assert.Equal(new[] { "one", "two", "three" }, item.Notes);
    }
}
