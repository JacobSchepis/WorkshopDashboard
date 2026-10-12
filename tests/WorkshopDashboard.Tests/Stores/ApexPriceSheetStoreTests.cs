using System.Text;
using WorkshopDashboard.Data;
using WorkshopDashboard.Services.Stores;
using WorkshopDashboard.Tests.Fakes;

namespace WorkshopDashboard.Tests.Stores;

// The Apex price sheet store: reading the converted CSV, previewing what an import would change,
// replacing the sheet, and searching it. Lines here are invented — the real sheet is confidential
// and never goes in the repo.
public class ApexPriceSheetStoreTests
{
    private const string Header = "line_item,code,description,category,price_usd,section\n";

    private static Stream Csv(string body) => new MemoryStream(Encoding.UTF8.GetBytes(Header + body));

    private static readonly string Sheet =
        "1,X100,\"Replace bending rubber, large diameter\",Bending Rubber,270.00,Flexible Repair Pricing\n" +
        "2,X101,Reseal bending rubber,Bending Rubber,175.00,Flexible Repair Pricing\n" +
        "3,X200,\"Rebuild bending section, GIF series - includes angulation adjustment\",Bending Section,\"$2,079.00\",Flexible Repair Pricing\n";

    [Fact]
    public void Parse_ReadsEveryLine_IncludingQuotedCommasAndDollarPrices()
    {
        var lines = ApexPriceSheetStore.Parse(Csv(Sheet));

        Assert.Equal(3, lines.Count);
        Assert.Equal("Replace bending rubber, large diameter", lines[0].Description);
        Assert.Equal(2079.00m, lines[2].PriceUsd);
        Assert.Equal(3, lines[2].LineItem);
    }

    [Fact]
    public void Parse_RejectsAFileThatIsntAPriceSheet()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ApexPriceSheetStore.Parse(new MemoryStream(Encoding.UTF8.GetBytes("name,amount\nfoo,1\n"))));
        Assert.Contains("price sheet", ex.Message);
    }

    [Fact]
    public void Parse_RejectsABadPrice_NamingTheLine()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ApexPriceSheetStore.Parse(Csv("7,X700,Thing,Cat,TBA,\n")));
        Assert.Contains("X700", ex.Message);
    }

    [Fact]
    public void Parse_RejectsDuplicateCodes()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ApexPriceSheetStore.Parse(Csv("1,X1,A,Cat,1,\n2,x1,B,Cat,2,\n")));
        Assert.Contains("X1", ex.Message);
    }

    [Fact]
    public async Task FirstImport_IsAllAdditions_AndReplaceStoresIt()
    {
        using var db = new TestDb();
        var store = new ApexPriceSheetStore(db);

        var diff = await store.PreviewAsync(Csv(Sheet));
        Assert.Equal(3, diff.Added.Count);
        Assert.True(diff.HasChanges);

        await store.ReplaceAllAsync(diff.Incoming);
        Assert.Equal(new[] { "X100", "X101", "X200" }, (await store.LoadAsync()).Select(l => l.Code));
    }

    [Fact]
    public async Task Preview_ShowsAddedRemovedAndPriceChanges_WithoutSaving()
    {
        using var db = new TestDb();
        var store = new ApexPriceSheetStore(db);
        await store.ReplaceAllAsync(ApexPriceSheetStore.Parse(Csv(Sheet)));

        var diff = await store.PreviewAsync(Csv(
            "1,X100,\"Replace bending rubber, large diameter\",Bending Rubber,290.00,\n" +   // price up
            "2,X101,Reseal bending rubber - all models,Bending Rubber,175.00,\n" +          // wording only
            "3,X300,Replace light guide,Image/Light Guide bundle,900.00,\n"));               // new; X200 gone

        Assert.Equal(new[] { "X300" }, diff.Added.Select(l => l.Code));
        Assert.Equal(new[] { "X200" }, diff.Removed.Select(l => l.Code));
        var change = Assert.Single(diff.PriceChanged);
        Assert.Equal((270.00m, 290.00m), (change.Old.PriceUsd, change.New.PriceUsd));
        Assert.Equal(1, diff.DescriptionChanged); // just X101's wording — X100 only changed price
        Assert.Equal(3, (await store.LoadAsync()).Count); // nothing saved yet
    }

    [Fact]
    public async Task Preview_OfTheSameSheet_HasNoChanges()
    {
        using var db = new TestDb();
        var store = new ApexPriceSheetStore(db);
        await store.ReplaceAllAsync(ApexPriceSheetStore.Parse(Csv(Sheet)));

        Assert.False((await store.PreviewAsync(Csv(Sheet))).HasChanges);
    }

    [Fact]
    public async Task Replace_SwapsTheWholeSheet()
    {
        using var db = new TestDb();
        var store = new ApexPriceSheetStore(db);
        await store.ReplaceAllAsync(ApexPriceSheetStore.Parse(Csv(Sheet)));

        await store.ReplaceAllAsync(ApexPriceSheetStore.Parse(Csv("1,X900,Only line,Misc,1.00,\n")));

        Assert.Equal(new[] { "X900" }, (await store.LoadAsync()).Select(l => l.Code));
    }

    [Theory]
    [InlineData("bending rubber", new[] { "X100", "X101" })]
    [InlineData("BEND gif", new[] { "X200" })]
    [InlineData("x20", new[] { "X200" })]
    [InlineData("rubber reseal", new[] { "X101" })]
    [InlineData("angulation", new[] { "X200" })]
    [InlineData("light guide", new string[0])]
    public void Search_MatchesEveryWord_InCodeDescriptionOrCategory(string term, string[] expected)
    {
        var lines = ApexPriceSheetStore.Parse(Csv(Sheet));
        Assert.Equal(expected, ApexPriceSheetStore.Search(lines, term).Select(l => l.Code));
    }

    [Fact]
    public void Search_PutsCodeMatchesFirst()
    {
        var lines = new List<ApexPriceLine>
        {
            new() { Code = "A1", LineItem = 1, Description = "See X200 for the full rebuild" },
            new() { Code = "X200", LineItem = 2, Description = "Rebuild" },
        };

        Assert.Equal(new[] { "X200", "A1" }, ApexPriceSheetStore.Search(lines, "x200").Select(l => l.Code));
    }
}
