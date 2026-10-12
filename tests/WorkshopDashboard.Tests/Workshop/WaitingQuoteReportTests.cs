using WorkshopDashboard.Services.Workshop;

namespace WorkshopDashboard.Tests.Workshop;

// The open summary report's data handling: turning JMS's per-quote CSV rows into jobs,
// grouping them for per-contact emails, and the display helpers.
public class WaitingQuoteReportTests
{
    private const string Header =
        "job_id,job_status_id,creation_date,fault,job_status,equipment,contact_id,contact_name,contact_email,job_quote_id,total_amount,quote_date,accepted,file_location,eta_raw\n";

    [Fact]
    public void RowsForTheSameJob_BecomeOneJobWithItsQuotes()
    {
        var jobs = WaitingQuoteReport.ParseJobs(Header +
            "1001,10,2026-09-01,Leaking,Waiting Quote Confirmation,Olympus GIF-H190 (123),5,Jane Smith,jane@example.com,501,1100,2026-09-05,f,/q/501.pdf,\n" +
            "1001,10,2026-09-01,Leaking,Waiting Quote Confirmation,Olympus GIF-H190 (123),5,Jane Smith,jane@example.com,502,550,2026-09-10,t,,\n" +
            "1001,10,2026-09-01,Leaking,Waiting Quote Confirmation,Olympus GIF-H190 (123),5,Jane Smith,jane@example.com,502,550,2026-09-10,t,,\n");

        var job = Assert.Single(jobs);
        Assert.Equal("1001", job.JobId);
        Assert.Equal("jane@example.com", job.ContactEmail);
        Assert.Equal(new[] { "501", "502" }, job.Quotes.Select(q => q.QuoteId));
        Assert.Equal("/q/501.pdf", job.Quotes[0].FileLocation);
        Assert.Null(job.Quotes[1].FileLocation);
        Assert.True(job.Quotes[1].Accepted);
        Assert.Equal(1000m, job.Quotes[0].PriceExGst);
    }

    [Fact]
    public void JobWithNoQuotes_StillAppears()
    {
        var job = Assert.Single(WaitingQuoteReport.ParseJobs(Header +
            "1002,20,2026-09-01,Broken,Work In Progress,Storz 26003 (9),,,,,,,,,\n"));

        Assert.Empty(job.Quotes);
        Assert.Null(job.ContactEmail);
    }

    [Theory]
    [InlineData(" ()", "Leaking", "Leaking")]
    [InlineData("", "  Leaking ", "Leaking")]
    [InlineData(" ()", "", "—")]
    public void JobWithNoEquipment_ShowsTheFaultInstead(string equipment, string fault, string expected)
    {
        var job = WaitingQuoteReport.ParseJobs(Header + $"1003,10,2026-09-01,\"{fault}\",Waiting,\"{equipment}\",,,,,,,,,\n").Single();

        Assert.Equal(expected, job.Equipment);
    }

    [Fact]
    public void EtaNote_IsParsedIntoADate()
    {
        var job = WaitingQuoteReport.ParseJobs(Header + "1004,30,2026-09-01,x,Waiting,E,,,,,,,,,ETA - 2026-10-15\n").Single();

        Assert.Equal(new DateTime(2026, 10, 15), job.Eta);
        Assert.Equal(job.Eta, job.EtaInput);
    }

    [Fact]
    public void Jobs_AreGroupedByContact_WithUnknownContactsLast()
    {
        var quoteJobs = new List<WqJob>
        {
            new() { JobId = "1", ContactEmail = "zoe@example.com", ContactName = "Zoe" },
            new() { JobId = "2", ContactEmail = null },
            new() { JobId = "3", ContactEmail = "adam@example.com", ContactName = "Adam" },
        };
        var otherJobs = new List<WqJob> { new() { JobId = "4", ContactEmail = "adam@example.com", ContactName = "Adam" } };

        var groups = WaitingQuoteReport.GroupByContact(quoteJobs, otherJobs);

        Assert.Equal(new[] { "Adam", "Zoe", null }, groups.Select(g => g.ContactName));
        Assert.Equal(new[] { "3" }, groups[0].QuoteJobs.Select(j => j.JobId));
        Assert.Equal(new[] { "4" }, groups[0].OtherJobs.Select(j => j.JobId));
        Assert.False(groups[2].CanSend);
    }

    [Theory]
    [InlineData(0, WqUrgency.InDate)]
    [InlineData(20, WqUrgency.InDate)]
    [InlineData(21, WqUrgency.Medium)]
    [InlineData(29, WqUrgency.Medium)]
    [InlineData(30, WqUrgency.Urgent)]
    public void QuoteUrgency_GrowsWithAge(int daysOld, WqUrgency expected)
    {
        Assert.Equal(expected, WaitingQuoteReport.GetUrgency(DateTime.Today.AddDays(-daysOld)));
    }

    [Fact]
    public void QuoteWithNoDate_IsNotUrgent()
    {
        Assert.Equal(WqUrgency.InDate, WaitingQuoteReport.GetUrgency(DateTime.MinValue));
    }

    [Theory]
    [InlineData("Waiting Quote Confirmation", "Pending Quote Approval")]
    [InlineData("waiting quote confirmation", "Pending Quote Approval")]
    [InlineData("Some New Status", "Some New Status")]
    public void Statuses_AreShownInCustomerFriendlyWords(string raw, string expected)
    {
        Assert.Equal(expected, WaitingQuoteReport.FriendlyStatus(raw));
    }

    [Fact]
    public void Equipment_StartingWithAKnownManufacturer_IsAbbreviated()
    {
        var abbreviations = new Dictionary<string, string> { ["Olympus Medical"] = "Olympus" };

        Assert.Equal("Olympus GIF-H190", WaitingQuoteReport.AbbreviateEquipment("olympus medical GIF-H190", abbreviations));
        Assert.Equal("Storz 26003", WaitingQuoteReport.AbbreviateEquipment("Storz 26003", abbreviations));
    }

    [Theory]
    [InlineData("Jane Smith", "Jane")]
    [InlineData("  Jane  ", "Jane")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void FirstName_IsTheFirstWord(string? fullName, string? expected)
    {
        Assert.Equal(expected, WaitingQuoteReport.FirstName(fullName));
    }

    [Fact]
    public void LatestQuoteDate_IsTheNewestQuote_OrNeverWithoutQuotes()
    {
        var job = new WqJob
        {
            Quotes = { new WqQuote { DateSent = new DateTime(2026, 9, 1) }, new WqQuote { DateSent = new DateTime(2026, 9, 9) } }
        };

        Assert.Equal(new DateTime(2026, 9, 9), WaitingQuoteReport.LatestQuoteDate(job));
        Assert.Equal(DateTime.MaxValue, WaitingQuoteReport.LatestQuoteDate(new WqJob()));
    }
}
