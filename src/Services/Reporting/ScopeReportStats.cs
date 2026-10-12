using CsvHelper;

using WorkshopDashboard.Services.Workshop;
namespace WorkshopDashboard.Services.Reporting;

/// <summary>
/// One company/customer/state/overall bucket of scope-report numbers for a period.
/// Built by summing the per-company rows from JmsQueries.BuildScopeReportStatsQuery
/// (see ScopeReportStatsParser.Parse) — a scope customer, a state, and the overall
/// total are all just different sets of company ids summed together.
/// </summary>
public class ScopeReportStats
{
    public int JobCount { get; set; }
    public int CompletedCount { get; set; }
    public int OpenCount { get; set; }

    /// <summary>Completed jobs (job_status_id = 2) with no job_invoice row at all — closed
    /// without ever being invoiced. A subset of CompletedCount, not an alternate total.</summary>
    public int ClosedWithoutInvoiceCount { get; set; }

    public decimal InvoicedTotal { get; set; }
    public decimal PaidTotal { get; set; }

    public decimal OutstandingTotal => InvoicedTotal - PaidTotal;

    /// <summary>Sum of (completed_date - creation_date) in days, across completed jobs
    /// that have a completed_date. Paired with TurnaroundJobCount so averages can still
    /// be combined correctly across companies/customers without re-querying.</summary>
    public double TurnaroundDaysSum { get; set; }
    public int TurnaroundJobCount { get; set; }

    public double? AvgTurnaroundDays => TurnaroundJobCount > 0 ? TurnaroundDaysSum / TurnaroundJobCount : null;

    /// <summary>Sum of (response_end - response_start) in days — time from a job first
    /// entering "Waiting Quote Confirmation" to it first reaching "Waiting Workshop
    /// Processing" or a "Quote declined" status afterwards. Paired with
    /// ResponseJobCount the same way TurnaroundDaysSum is.</summary>
    public double ResponseDaysSum { get; set; }
    public int ResponseJobCount { get; set; }

    public double? AvgResponseDays => ResponseJobCount > 0 ? ResponseDaysSum / ResponseJobCount : null;

    public static ScopeReportStats Sum(IEnumerable<ScopeReportStats> stats)
    {
        var total = new ScopeReportStats();
        foreach (var s in stats)
        {
            total.JobCount += s.JobCount;
            total.CompletedCount += s.CompletedCount;
            total.OpenCount += s.OpenCount;
            total.ClosedWithoutInvoiceCount += s.ClosedWithoutInvoiceCount;
            total.InvoicedTotal += s.InvoicedTotal;
            total.PaidTotal += s.PaidTotal;
            total.TurnaroundDaysSum += s.TurnaroundDaysSum;
            total.TurnaroundJobCount += s.TurnaroundJobCount;
            total.ResponseDaysSum += s.ResponseDaysSum;
            total.ResponseJobCount += s.ResponseJobCount;
        }
        return total;
    }
}

public static class ScopeReportStatsParser
{
    /// <summary>Parses JmsQueries.BuildScopeReportStatsQuery's CSV into a per-company map.</summary>
    public static Dictionary<int, ScopeReportStats> Parse(string csvContent)
    {
        var config = WaitingQuoteReport.MakeCsvConfig();

        using var reader = new StringReader(csvContent);
        using var csv = new CsvReader(reader, config);

        var map = new Dictionary<int, ScopeReportStats>();
        foreach (var row in csv.GetRecords<StatsRow>())
        {
            map[row.owner_company_id] = new ScopeReportStats
            {
                JobCount = row.job_count,
                CompletedCount = row.completed_count,
                OpenCount = row.open_count,
                ClosedWithoutInvoiceCount = row.closed_without_invoice_count,
                InvoicedTotal = row.invoiced_total,
                PaidTotal = row.paid_total,
                TurnaroundDaysSum = row.turnaround_days_sum,
                TurnaroundJobCount = row.turnaround_job_count
            };
        }
        return map;
    }

    /// <summary>Parses JmsQueries.BuildScopeResponseTimeQuery's CSV — a separate query from
    /// Parse above, so a problem with it (or the endpoint rejecting it) doesn't take out the
    /// simpler stats. See ScopeReportingOverview.razor's LoadAsync for how the two are
    /// combined via Merge.</summary>
    public static Dictionary<int, ScopeReportStats> ParseResponseTimes(string csvContent)
    {
        var config = WaitingQuoteReport.MakeCsvConfig();

        using var reader = new StringReader(csvContent);
        using var csv = new CsvReader(reader, config);

        var map = new Dictionary<int, ScopeReportStats>();
        foreach (var row in csv.GetRecords<ResponseTimeRow>())
        {
            map[row.owner_company_id] = new ScopeReportStats
            {
                ResponseDaysSum = row.response_days_sum,
                ResponseJobCount = row.response_job_count
            };
        }
        return map;
    }

    /// <summary>Combines the Parse and ParseResponseTimes maps into one per-company map. Safe
    /// to call even when one side is empty (e.g. its query failed) — that company's stats just
    /// come entirely from whichever side actually has them.</summary>
    public static Dictionary<int, ScopeReportStats> Merge(
        Dictionary<int, ScopeReportStats> stats, Dictionary<int, ScopeReportStats> responseTimes)
    {
        var ids = stats.Keys.Union(responseTimes.Keys);
        var merged = new Dictionary<int, ScopeReportStats>();
        foreach (var id in ids)
        {
            var parts = new List<ScopeReportStats>();
            if (stats.TryGetValue(id, out var s)) parts.Add(s);
            if (responseTimes.TryGetValue(id, out var r)) parts.Add(r);
            merged[id] = ScopeReportStats.Sum(parts);
        }
        return merged;
    }

    /// <summary>Sums the stats for a set of company ids (a scope customer's CompanyIds,
    /// or every id in a state/overall). Ids with no matching row (no jobs in the period)
    /// contribute nothing, which is correct.</summary>
    public static ScopeReportStats Aggregate(Dictionary<int, ScopeReportStats> byCompany, IEnumerable<int> companyIds) =>
        ScopeReportStats.Sum(companyIds.Where(byCompany.ContainsKey).Select(id => byCompany[id]));

    private class StatsRow
    {
        public int owner_company_id { get; set; }
        public int job_count { get; set; }
        public int completed_count { get; set; }
        public int open_count { get; set; }
        public int closed_without_invoice_count { get; set; }
        public decimal invoiced_total { get; set; }
        public decimal paid_total { get; set; }
        public double turnaround_days_sum { get; set; }
        public int turnaround_job_count { get; set; }
    }

    private class ResponseTimeRow
    {
        public int owner_company_id { get; set; }
        public double response_days_sum { get; set; }
        public int response_job_count { get; set; }
    }
}
