using Microsoft.Extensions.Logging.Abstractions;
using WorkshopDashboard.Demo;
using WorkshopDashboard.Models;
using WorkshopDashboard.Services.External.Jms;
using WorkshopDashboard.Services.Quoting.Apex;
using WorkshopDashboard.Services.Quoting.Bayside;
using WorkshopDashboard.Services.Quoting.Cobalt;
using WorkshopDashboard.Services.Quoting.Dorado;
using WorkshopDashboard.Services.Quoting.Ember;
using WorkshopDashboard.Services.Quoting.Falcon;

namespace WorkshopDashboard.Tests.Demo;

// Demo mode runs the real JMS client against DemoJms, and the real quote extractors against
// the schema-driven fake LLM — these check both ends meet.
public class DemoModeTests : IClassFixture<JmsQueriesRunOnDemoTests.DemoDb>
{
    private readonly DemoJms _jms;

    public DemoModeTests(JmsQueriesRunOnDemoTests.DemoDb fixture) => _jms = new DemoJms(fixture.Database, NullLogger<DemoJms>.Instance);

    private JmsClient Client() => new(DemoJms.Options, _jms.Transport);

    private async Task<JmsClient> LoggedInAsync()
    {
        var client = Client();
        Assert.Equal(JmsAuthResult.Success, await client.TryLoginAsync(DemoJms.DemoUsername, DemoJms.DemoPassword));
        return client;
    }

    private async Task<string> ScalarAsync(string sql) => (await _jms.Database.QueryCsvAsync(sql)).Trim().Split('\n')[1].Trim();

    [Fact]
    public async Task Login_AcceptsTheDemoAccount_AndNothingElse()
    {
        Assert.Equal(JmsAuthResult.Rejected, await Client().CheckAuthAsync());
        Assert.Equal(JmsAuthResult.Rejected, await Client().TryLoginAsync(DemoJms.DemoUsername, "wrong"));
        await LoggedInAsync();
    }

    [Fact]
    public async Task Queries_AnswerAsCsv()
    {
        using var client = await LoggedInAsync();

        var res = await client.PostQueryForCsvAsync(JmsQueries.CompanyNameQuery(DemoSeeder.OwnCompanyId));

        Assert.True(res.Success);
        Assert.Equal("company_name\nWorkshop Dashboard Demo", res.Content.Trim());
    }

    [Fact]
    public async Task CreatedJobs_GetAnIdAndCanBeMovedThroughTheWorkflow()
    {
        using var client = await LoggedInAsync();

        var created = await client.CreateJobAsync(new CreateJobRequest
        {
            OwnerCompanyId = 100, OwnerCompanyName = "", BillToCompanyId = 100, BillToCompanyName = "",
            OwnerContactId = 2001, OwnerContactName = "", BillContactId = 2001, BillContactName = "",
            BusinessTypeId = 6, UserId = DemoSeeder.DemoUserId, ContactUserId = DemoSeeder.DemoUserId, JobPriorityId = 3,
            Instructions = "Demo test job", RequiredDate = DateTime.Today,
        });
        var jobId = int.Parse(System.Text.RegularExpressions.Regex.Match(created.Url!, @"id=(\d+)").Groups[1].Value);

        await client.ChangeJobStatusAsync(jobId, JobStatus.UnitArrivedAtWorkshop, "Arrived");
        await client.AddNoteServiceAsync(jobId, "[Tracking] Received");

        Assert.Equal("16", await ScalarAsync(JmsQueries.JobStatusIdQuery(jobId)));
        Assert.Equal("1", await ScalarAsync(JmsQueries.MatchingServiceCountQuery(jobId, JmsClient.ServiceTypeNote, "[Tracking] Received", 5)));
    }

    [Fact]
    public async Task DeletedServices_DropOutOfTheJob()
    {
        using var client = await LoggedInAsync();
        var jobId = DemoSeeder.FirstJobId + 10;

        await client.AddNoteServiceAsync(jobId, "to be deleted");
        var serviceId = int.Parse(await ScalarAsync(JmsQueries.LatestServiceIdQuery(jobId, JmsClient.ServiceTypeNote)));
        await client.DeleteJobServiceAsync(jobId, serviceId);

        Assert.Equal("0", await ScalarAsync(JmsQueries.MatchingServiceCountQuery(jobId, JmsClient.ServiceTypeNote, "to be deleted", 5)));
    }

    [Fact]
    public async Task BackgroundReports_PostTheNotificationTheAppPollsFor()
    {
        using var client = await LoggedInAsync();
        var before = int.Parse(await ScalarAsync(JmsQueries.LatestAnyNotificationIdQuery()));

        Assert.True(await client.QueueJobProgressReportAsync(100, "Kestrelvale Hospital", DateTime.Today.AddMonths(-1), DateTime.Today));
        var link = (await _jms.Database.QueryCsvAsync(JmsQueries.JobProgressNotificationQuery("Kestrelvale Hospital", before)))
            .Trim().Split('\n')[1].Split(',')[0];
        var pdf = await client.DownloadAsync(link);

        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(pdf, 0, 4));
    }

    // ── Quote extraction against the fake LLM ──

    private static readonly DemoJsonCompletionClient Llm = new();
    private static byte[] QuotePdf => DemoDocuments.Pdf("Repair quote", $"Job reference {DemoSeeder.FirstJobId + 5}", "Replace bending section rubber");

    [Fact]
    public async Task Extractors_GetAParseableAnswerForTheirSchema()
    {
        var apex = await new ApexQuoteExtractor(Llm).ExtractAsync(QuotePdf);
        await new BaysideQuoteExtractor(Llm).ExtractAsync(QuotePdf);
        await new CobaltQuoteExtractor(Llm).ExtractAsync(QuotePdf);
        await new DoradoQuoteExtractor(Llm).ExtractAsync(QuotePdf);
        await new EmberQuoteExtractor(Llm).ExtractAsync(QuotePdf);
        await new FalconQuoteExtractor(Llm).ExtractAsync(QuotePdf);

        Assert.Equal((DemoSeeder.FirstJobId + 5).ToString(), apex.JobNumber); // read from the PDF's text
    }
}
