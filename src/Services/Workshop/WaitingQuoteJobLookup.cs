using CsvHelper;
using WorkshopDashboard.Services.External.Jms;
using WorkshopDashboard.Services.Jobs;
using WorkshopDashboard.Services.Workshop;
using WorkshopDashboard.Services.Admin;
using WorkshopDashboard.Services.Stores;

namespace WorkshopDashboard.Services.Workshop;

public record WaitingQuoteCounts(int UrgentCount, int MediumCount, int InDateCount);

/// <summary>
/// Data access for the WaitingQuote feature — the dashboard card counts
/// (WaitingQuote.razor) and the per-company job detail (WaitingQuoteDetail.razor).
/// Row parsing into WqJob stays in WaitingQuoteReport.ParseJobs, which is shared with
/// other WaitingQuote consumers; this class only runs the two queries each page needs.
/// </summary>
public class WaitingQuoteJobLookup
{
    private readonly JmsReadSession _jmsRead;

    public WaitingQuoteJobLookup(JmsReadSession jmsRead) => _jmsRead = jmsRead;

    // Ticked quote PDFs for WaitingQuoteDetail.razor's attachment builder.
    public async Task<byte[]?> GetQuotePdfBytesAsync(string fileLocation)
    {
        var jms = await _jmsRead.GetClientAsync();
        return jms == null ? null : await jms.GetQuotePdfAsync(fileLocation);
    }

    public async Task<(List<WqJob> QuoteJobs, List<WqJob> OtherJobs)> GetJobsAsync(IEnumerable<int> companyIds, ScopeJobExclusions exclusions)
    {
        var jms = await _jmsRead.GetClientAsync();
        if (jms == null) return (new(), new());

        var ids = companyIds.ToList();
        var quoteQuery = JmsQueries.BuildQuoteJobsQuery(ids, exclusions);
        var otherQuery = JmsQueries.BuildOtherJobsQuery(ids, exclusions);

        var quoteTask = jms.PostQueryForCsvAsync(quoteQuery);
        var otherTask = jms.PostQueryForCsvAsync(otherQuery);
        await Task.WhenAll(quoteTask, otherTask);

        var quoteJobs = quoteTask.Result.Success ? WaitingQuoteReport.ParseJobs(quoteTask.Result.Content) : new();
        var otherJobs = otherTask.Result.Success ? WaitingQuoteReport.ParseJobs(otherTask.Result.Content) : new();
        return (quoteJobs, otherJobs);
    }

    public async Task<(Dictionary<int, WaitingQuoteCounts> QuoteCounts, Dictionary<int, int> RepairCounts)> GetCountsAsync(IEnumerable<int> companyIds, ScopeJobExclusions exclusions)
    {
        var jms = await _jmsRead.GetClientAsync();
        if (jms == null) return (new(), new());

        var ids = companyIds.ToList();
        var quoteQuery = JmsQueries.BuildQuoteCountsQuery(ids, exclusions);
        var repairQuery = JmsQueries.BuildRepairCountsQuery(ids, exclusions);

        var quoteTask = jms.PostQueryForCsvAsync(quoteQuery);
        var repairTask = jms.PostQueryForCsvAsync(repairQuery);
        await Task.WhenAll(quoteTask, repairTask);

        var quoteRes = quoteTask.Result;
        var repairRes = repairTask.Result;

        var quoteCounts = new Dictionary<int, WaitingQuoteCounts>();
        if (quoteRes.Success)
        {
            var config = WaitingQuoteReport.MakeCsvConfig();
            using var reader = new StringReader(quoteRes.Content);
            using var csv = new CsvReader(reader, config);
            foreach (var row in csv.GetRecords<QuoteCountsCsvRow>())
                quoteCounts[row.owner_company_id] = new WaitingQuoteCounts(row.urgent_count, row.medium_count, row.indate_count);
        }

        var repairCounts = new Dictionary<int, int>();
        if (repairRes.Success)
        {
            var config = WaitingQuoteReport.MakeCsvConfig();
            using var reader = new StringReader(repairRes.Content);
            using var csv = new CsvReader(reader, config);
            foreach (var row in csv.GetRecords<RepairCountsCsvRow>())
                repairCounts[row.owner_company_id] = row.repair_count;
        }

        return (quoteCounts, repairCounts);
    }

    private class QuoteCountsCsvRow
    {
        public int owner_company_id { get; set; }
        public int urgent_count { get; set; }
        public int medium_count { get; set; }
        public int indate_count { get; set; }
    }

    private class RepairCountsCsvRow
    {
        public int owner_company_id { get; set; }
        public int repair_count { get; set; }
    }
}
