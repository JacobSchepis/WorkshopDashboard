using WorkshopDashboard.Models;
using WorkshopDashboard.Services.Admin;
using WorkshopDashboard.Util;

namespace WorkshopDashboard.Tests.Admin;

// Small rules used across several pages: which repairer a job belongs to, which customer
// department a job goes to, label formatting, job id input, and the DHL item-upload CSV.
public class SmallHelperTests
{
    // ── RepairerResolver ──

    private static readonly List<Repairer> Repairers = new()
    {
        new() { Name = "Apex", ModelIds = { 10, 11 } },
        new() { Name = "Falcon", JobIds = { 5000 } },
        new() { Name = "Dorado", ModelIds = { 20 }, JobIds = { 6000 } },
    };

    [Fact]
    public void JobOverride_WinsOverTheEquipmentModel()
    {
        Assert.Equal("Dorado", RepairerResolver.Resolve(Repairers, new List<int> { 10 }, jobId: 6000));
    }

    [Fact]
    public void EquipmentModel_PicksTheRepairer()
    {
        Assert.Equal("Apex", RepairerResolver.Resolve(Repairers, new List<int> { 99, 11 }, jobId: 1));
    }

    [Fact]
    public void JobWithNoEquipment_DefaultsToFalcon()
    {
        Assert.Equal(RepairerResolver.NoEquipmentDefault, RepairerResolver.Resolve(Repairers, new List<int>(), jobId: 1));
    }

    [Fact]
    public void UnmatchedEquipment_IsUnassigned()
    {
        Assert.Null(RepairerResolver.Resolve(Repairers, new List<int> { 99 }, jobId: 1));
    }

    // ── ScopeCustomer departments ──

    private static readonly ScopeCustomer Customer = new()
    {
        DisplayName = "Hospital",
        Departments =
        {
            new() { Name = "Flex", JobContactIds = { 1, 2 } },
            new() { Name = "Everything else" },
            new() { Name = "Rigid", JobContactIds = { 3 } },
        }
    };

    [Theory]
    [InlineData(2, "Flex")]
    [InlineData(3, "Rigid")]
    [InlineData(99, "Everything else")]
    [InlineData(null, "Everything else")]
    public void Job_GoesToTheDepartmentListingItsContact_OrTheCatchAll(int? contactId, string expected)
    {
        Assert.Equal(expected, Customer.DepartmentForJobContact(contactId)!.Name);
    }

    [Fact]
    public void CustomerWithoutACatchAll_LeavesUnlistedContactsUnassigned()
    {
        var customer = new ScopeCustomer { Departments = { new() { Name = "Flex", JobContactIds = { 1 } } } };

        Assert.Null(customer.DepartmentForJobContact(2));
    }

    // ── Labels and input ──

    [Theory]
    [InlineData(5, "Acme", "Acme (5)")]
    [InlineData(5, " ", "(5)")]
    [InlineData(5, null, "(5)")]
    public void CompanyLabel_FallsBackToTheIdAlone(int id, string? name, string expected)
    {
        Assert.Equal(expected, ScopeLabelFormatter.FormatCompanyLabel(id, name));
    }

    [Fact]
    public void ContactDetails_SkipBlankParts()
    {
        Assert.Equal("Jane · 0400 000 000", ScopeLabelFormatter.FormatContactDetails("Jane", " ", "0400 000 000"));
        Assert.Equal("(no details)", ScopeLabelFormatter.FormatContactDetails(null, null, null));
    }

    [Theory]
    [InlineData(" 123 ", true, 123)]
    [InlineData("0", false, 0)]
    [InlineData("-5", false, -5)]
    [InlineData("12a", false, 0)]
    [InlineData(null, false, 0)]
    public void JobId_MustBeAPositiveWholeNumber(string? raw, bool ok, int expected)
    {
        Assert.Equal(ok, JobIdParser.TryParseSingle(raw, out var jobId));
        if (ok) Assert.Equal(expected, jobId);
    }

    // ── DHL item-upload CSV ──

    private static DhlItemCsvRow Item(string description = "Endoscope", string serial = "SN1", string country = "Japan") => new()
    {
        Description = description,
        CommodityCode = "9018.90",
        DeclaredValue = "150",
        Weight = "2.5",
        CountryOfOrigin = country,
        SerialNumber = serial,
    };

    [Fact]
    public void DhlCsv_IsOneFourteenColumnRowPerUnit_WithNoHeader()
    {
        var csv = DhlItemCsvGenerator.Generate(new List<DhlItemCsvRow> { Item(), Item(serial: "SN2") }, "USD");

        var lines = csv.TrimEnd().Split(Environment.NewLine);
        Assert.Equal(2, lines.Length);
        Assert.Equal("1,INV_ITEM,Endoscope,9018.90,1,EA,150,USD,2.5,,JP,SE,SN1,", lines[0]);
    }

    [Fact]
    public void DhlCsv_WithoutASerial_LeavesTheReferenceBlank()
    {
        var csv = DhlItemCsvGenerator.Generate(new List<DhlItemCsvRow> { Item(serial: "") }, "USD");

        Assert.EndsWith(",JP,,," + Environment.NewLine, csv);
    }

    [Fact]
    public void DhlCsv_EscapesCommasAndQuotes_AndTrimsLongDescriptions()
    {
        var csv = DhlItemCsvGenerator.Generate(new List<DhlItemCsvRow>
        {
            Item(description: "Scope, flexible \"HD\""),
            Item(description: new string('x', 200)),
        }, "USD");

        Assert.Contains("\"Scope, flexible \"\"HD\"\"\"", csv);
        Assert.Contains("," + new string('x', 170) + ",", csv);
        Assert.DoesNotContain(new string('x', 171), csv);
    }

    [Theory]
    [InlineData("Japan", "JP")]
    [InlineData(" united states of america ", "US")]
    [InlineData("UK", "GB")]
    [InlineData("de", "DE")]
    [InlineData("Atlantis", null)]
    [InlineData("", null)]
    public void CountryNames_BecomeTwoLetterCodes(string name, string? expected)
    {
        Assert.Equal(expected, CountryCodes.ToAlpha2(name));
    }
}
