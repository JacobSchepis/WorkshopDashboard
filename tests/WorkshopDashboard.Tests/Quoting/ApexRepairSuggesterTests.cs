using WorkshopDashboard.Data;
using WorkshopDashboard.Services.Quoting.Apex;

namespace WorkshopDashboard.Tests.Quoting;

// The guardrails around the AI's repair suggestions: only codes on the price sheet survive,
// and descriptions/prices always come from the sheet. (The AI call itself isn't tested here.)
public class ApexRepairSuggesterTests
{
    private static readonly List<ApexPriceLine> Sheet = new()
    {
        new() { Code = "X100", LineItem = 1, Description = "Replace bending rubber", Category = "Bending Rubber", PriceUsd = 270m },
        new() { Code = "X200", LineItem = 2, Description = "Rebuild bending section, includes bending rubber", Category = "Bending Section", PriceUsd = 2079m },
    };

    private const string Answer = """
        {
            "technical_observations": ["Bending section worn.", " "],
            "repairs": [
                { "code": "x200", "reason": "Notes say bending section is worn.", "confidence": "high" },
                { "code": "Z999", "reason": "Made up.", "confidence": "low" },
                { "code": "X200", "reason": "Duplicate.", "confidence": "medium" },
                { "code": "X100", "reason": "Rubber torn.", "confidence": "unsure" }
            ],
            "unmatched_findings": ["Minor scratches on the insertion tube."]
        }
        """;

    [Fact]
    public void OnlyCodesOnTheSheet_AreKept_OnceEach()
    {
        var result = ApexRepairSuggester.Interpret(Answer, Sheet);

        Assert.Equal(new[] { "X200", "X100" }, result.Repairs.Select(r => r.Line.Code));
        Assert.Equal(new[] { "Z999" }, result.RejectedCodes);
    }

    [Fact]
    public void DescriptionAndPrice_ComeFromTheSheet()
    {
        var result = ApexRepairSuggester.Interpret(Answer, Sheet);

        var rebuild = result.Repairs[0];
        Assert.Equal("Rebuild bending section, includes bending rubber", rebuild.Line.Description);
        Assert.Equal(2079m, rebuild.Line.PriceUsd);
        Assert.Equal(2349m, result.TotalUsd);
    }

    [Fact]
    public void ReasonsConfidenceAndObservations_AreCarriedThrough()
    {
        var result = ApexRepairSuggester.Interpret(Answer, Sheet);

        Assert.Equal("Notes say bending section is worn.", result.Repairs[0].Reason);
        Assert.Equal(SuggestionConfidence.High, result.Repairs[0].Confidence);
        Assert.Equal(SuggestionConfidence.Low, result.Repairs[1].Confidence); // unknown value → Low
        Assert.Equal(new[] { "Bending section worn." }, result.TechnicalObservations);
        Assert.Equal(new[] { "Minor scratches on the insertion tube." }, result.UnmatchedFindings);
    }

    // ── Pass 2: combining ────────────────────────────────────────────────────

    private static readonly List<ApexPriceLine> CapSheet = new()
    {
        new() { Code = "T1", LineItem = 1, Description = "Replace insertion tube", Category = "Insertion Tube Assembly", PriceUsd = 1800m },
        new() { Code = "U1", LineItem = 2, Description = "Replace U-cord", Category = "U-Cord", PriceUsd = 1200m },
        new() { Code = "L1", LineItem = 3, Description = "Replace light bundle", Category = "Image/Light Guide bundle", PriceUsd = 900m },
        new() { Code = "C1", LineItem = 4, Description = "Replace CCD", Category = "CCD System", PriceUsd = 2500m },
        new() { Code = "R1", LineItem = 5, Description = "Replace bending rubber", Category = "Bending Rubber", PriceUsd = 270m },
        new() { Code = "B1", LineItem = 6, Description = "Rebuild bending section, includes bending rubber", Category = "Bending Section", PriceUsd = 2079m },
        new() { Code = "CAP", LineItem = 7, Description = "GIF-H190 Cap - includes insertion tube, U-cord, light bundle; CCD extra", Category = "Cap - Large Diameter", PriceUsd = 3100m },
        new() { Code = "PRICEY", LineItem = 8, Description = "Expensive cap", Category = "Cap - Large Diameter", PriceUsd = 9000m },
    };

    private static List<SuggestedRepair> PassOne(params string[] codes) =>
        codes.Select(c => new SuggestedRepair(CapSheet.Single(l => l.Code == c), $"finding for {c}", SuggestionConfidence.High)).ToList();

    private static ApexRepairSuggester.ProposedChange Change(string use, params string[] replaces) =>
        new(use, replaces.ToList(), $"{use} covers {string.Join(", ", replaces)}");

    [Fact]
    public void CheaperCap_ReplacesTheLinesItCovers_InTheirPlace()
    {
        var (repairs, changes) = ApexRepairSuggester.ApplyConsolidations(
            PassOne("T1", "U1", "L1", "C1"), new[] { Change("CAP", "T1", "U1", "L1") }, CapSheet);

        Assert.Equal(new[] { "CAP", "C1" }, repairs.Select(r => r.Line.Code));
        var c = Assert.Single(changes);
        Assert.True(c.Accepted);
        Assert.Equal(3900m - 3100m, c.SavingUsd);
        Assert.Equal(3100m + 2500m, repairs.Sum(r => r.Line.PriceUsd));
    }

    [Fact]
    public void CapThatIsntCheaper_IsNotApplied()
    {
        var (repairs, changes) = ApexRepairSuggester.ApplyConsolidations(
            PassOne("T1", "U1"), new[] { Change("PRICEY", "T1", "U1") }, CapSheet);

        Assert.Equal(new[] { "T1", "U1" }, repairs.Select(r => r.Line.Code));
        Assert.False(Assert.Single(changes).Accepted);
    }

    [Fact]
    public void LineIncludedByAnotherChosenLine_IsDropped()
    {
        var (repairs, changes) = ApexRepairSuggester.ApplyConsolidations(
            PassOne("R1", "B1"), new[] { Change("B1", "R1") }, CapSheet);

        Assert.Equal(new[] { "B1" }, repairs.Select(r => r.Line.Code));
        var c = Assert.Single(changes);
        Assert.True(c.Accepted);
        Assert.False(c.IsSwap);
        Assert.Equal(270m, c.SavingUsd);
    }

    [Fact]
    public void UnknownCodes_AndLinesNotInTheQuote_AreIgnored()
    {
        var (repairs, changes) = ApexRepairSuggester.ApplyConsolidations(
            PassOne("T1", "U1"),
            new[] { Change("NOPE", "T1"), Change("CAP", "L1", "C1") },   // made-up code; replaces lines not in the quote
            CapSheet);

        Assert.Equal(new[] { "T1", "U1" }, repairs.Select(r => r.Line.Code));
        Assert.All(changes, c => Assert.False(c.Accepted));
    }

    [Fact]
    public void ALineCanOnlyBeReplacedOnce()
    {
        var (repairs, changes) = ApexRepairSuggester.ApplyConsolidations(
            PassOne("T1", "U1", "L1"), new[] { Change("CAP", "T1", "U1", "L1"), Change("CAP", "T1") }, CapSheet);

        Assert.Equal(new[] { "CAP" }, repairs.Select(r => r.Line.Code));
        Assert.Equal(new[] { true, false }, changes.Select(c => c.Accepted));
    }

    [Fact]
    public void SwappedInLine_TakesTheLowestConfidenceOfWhatItReplaces()
    {
        var passOne = PassOne("T1", "U1", "L1");
        passOne[1] = passOne[1] with { Confidence = SuggestionConfidence.Medium };

        var (repairs, _) = ApexRepairSuggester.ApplyConsolidations(passOne, new[] { Change("CAP", "T1", "U1", "L1") }, CapSheet);

        Assert.Equal(SuggestionConfidence.Medium, Assert.Single(repairs).Confidence);
    }

    [Fact]
    public void CombineAnswer_IsParsed()
    {
        var changes = ApexRepairSuggester.ParseCombine("""
            { "changes": [ { "use_code": "CAP", "replaces_codes": ["T1", "U1"], "explanation": "Cap covers both." } ] }
            """);

        var c = Assert.Single(changes);
        Assert.Equal(("CAP", "Cap covers both."), (c.UseCode, c.Explanation));
        Assert.Equal(new[] { "T1", "U1" }, c.ReplacesCodes);
    }

    [Fact]
    public void PassTwoPriceList_IncludesPrices()
    {
        Assert.Contains("CAP | Cap - Large Diameter | $3100.00 | GIF-H190 Cap", ApexRepairSuggester.BuildPriceList(CapSheet, withPrices: true));
    }

    [Fact]
    public void PriceList_GivesCodesAndDescriptions_ButNotPrices()
    {
        var list = ApexRepairSuggester.BuildPriceList(Sheet);

        Assert.Contains("X100 | Bending Rubber | Replace bending rubber", list);
        Assert.DoesNotContain("270", list);
        Assert.DoesNotContain("2079", list);
    }
}
