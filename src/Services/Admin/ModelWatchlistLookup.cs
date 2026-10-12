using CsvHelper;
using WorkshopDashboard.Services.External.Jms;
using WorkshopDashboard.Services.Stores;
using WorkshopDashboard.Services.Workshop;

namespace WorkshopDashboard.Services.Admin;

public record ModelRepairCount(int ModelId, int JobCount);

public record JobStatusOption(int Id, string Name);

public record WatchlistJobRow(
    int JobId, string Owner, string Status, DateTime Created, string Fault,
    List<int> ModelIds, List<string> SerialNumbers, decimal? QuotedAmount, DateTime? QuotedDate,
    decimal TotalSpent, int TotalSpentJobs);

/// <summary>
/// Backs the Model Watchlist page (/model-watchlist): the "models we've repaired, last 12
/// months" reference list, and open jobs for whichever models staff have flagged as worth
/// watching. See JmsQueries.cs's "Model Watchlist" section for the query definitions.
/// </summary>
public class ModelWatchlistLookup
{
    private readonly JmsReadSession _jmsRead;

    public ModelWatchlistLookup(JmsReadSession jmsRead) => _jmsRead = jmsRead;

    /// <summary>modelIds narrows the count to just those models (e.g. for showing a job count
    /// next to a search hit) instead of the top-200-by-volume list.</summary>
    public async Task<List<ModelRepairCount>> GetModelRepairCountsAsync(DateTime periodStart, ScopeJobExclusions exclusions, IReadOnlyCollection<int>? modelIds = null)
    {
        var jms = await _jmsRead.GetClientAsync();
        if (jms == null) return new();

        var res = await jms.PostQueryForCsvAsync(JmsQueries.ModelRepairCountsQuery(periodStart, exclusions, modelIds));
        if (!res.Success) return new();

        using var reader = new StringReader(res.Content);
        using var csv = new CsvReader(reader, WaitingQuoteReport.MakeCsvConfig());
        return csv.GetRecords<ModelCountCsvRow>()
            .Select(r => new ModelRepairCount(r.model_id, r.job_count))
            .ToList();
    }

    public async Task<List<WatchlistJobRow>> GetOpenJobsAsync(IReadOnlyCollection<int> modelIds, ScopeJobExclusions exclusions, IReadOnlyCollection<int>? ignoredStatusIds = null)
    {
        if (modelIds.Count == 0) return new();

        var jms = await _jmsRead.GetClientAsync();
        if (jms == null) return new();

        var res = await jms.PostQueryForCsvAsync(JmsQueries.OpenScopeJobsForModelsQuery(modelIds, exclusions, ignoredStatusIds));
        if (!res.Success) return new();

        using var reader = new StringReader(res.Content);
        using var csv = new CsvReader(reader, WaitingQuoteReport.MakeCsvConfig());
        return csv.GetRecords<WatchlistJobCsvRow>()
            .Select(r => new WatchlistJobRow(
                r.job_id,
                string.IsNullOrWhiteSpace(r.owner) ? $"(company {r.owner_company_id})" : r.owner,
                r.job_status ?? "",
                DateTime.TryParse(r.creation_date, out var d) ? d : DateTime.MinValue,
                r.fault ?? "",
                (r.model_ids ?? "")
                    .Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => int.TryParse(s.Trim(), out var id) ? id : -1)
                    .Where(id => id > 0)
                    .Distinct()
                    .ToList(),
                (r.serial_numbers ?? "")
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(s => s.Length > 0)
                    .Distinct()
                    .ToList(),
                r.quoted_amount,
                DateTime.TryParse(r.quoted_date, out var qd) ? qd : null,
                r.total_spent,
                r.total_spent_jobs))
            .ToList();
    }

    /// <summary>Every job status (id + name), for the "ignored statuses" picker on
    /// ModelWatchlist.razor — a small, effectively-static list, safe to cache per page load.</summary>
    public async Task<List<JobStatusOption>> GetAllJobStatusesAsync()
    {
        var jms = await _jmsRead.GetClientAsync();
        if (jms == null) return new();

        var res = await jms.PostQueryForCsvAsync(JmsQueries.AllJobStatusesQuery());
        if (!res.Success) return new();

        using var reader = new StringReader(res.Content);
        using var csv = new CsvReader(reader, WaitingQuoteReport.MakeCsvConfig());
        return csv.GetRecords<JobStatusCsvRow>()
            .Select(r => new JobStatusOption(r.job_status_id, r.job_status ?? $"Status {r.job_status_id}"))
            .ToList();
    }

    private class JobStatusCsvRow
    {
        public int job_status_id { get; set; }
        public string? job_status { get; set; }
    }

    private class ModelCountCsvRow
    {
        public int model_id { get; set; }
        public int job_count { get; set; }
    }

    private class WatchlistJobCsvRow
    {
        public int job_id { get; set; }
        public int? owner_company_id { get; set; }
        public string? owner { get; set; }
        public string? job_status { get; set; }
        public string? creation_date { get; set; }
        public string? fault { get; set; }
        public string? model_ids { get; set; }
        public string? serial_numbers { get; set; }
        public decimal? quoted_amount { get; set; }
        public string? quoted_date { get; set; }
        public decimal total_spent { get; set; }
        public int total_spent_jobs { get; set; }
    }
}
