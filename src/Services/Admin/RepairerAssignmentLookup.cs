using CsvHelper;
using CsvHelper.Configuration;
using System.Globalization;
using WorkshopDashboard.Models;

using WorkshopDashboard.Services.External.Jms;
using WorkshopDashboard.Services.Stores;
using WorkshopDashboard.Services.Workshop;
namespace WorkshopDashboard.Services.Admin;

public record RepairerJobRow(int JobId, string Owner, string Status, DateTime Created, DateTime? Completed, string Fault, List<int> ModelIds, bool SentToRepairer);

/// <summary>
/// The open/closed unassigned-scope-job queues RepairerManager.razor cross-references
/// against its model->repairer assignments.
/// </summary>
public class RepairerAssignmentLookup
{
    private readonly JmsReadSession _jmsRead;

    public RepairerAssignmentLookup(JmsReadSession jmsRead) => _jmsRead = jmsRead;

    public Task<List<RepairerJobRow>> GetOpenJobsAsync(ScopeJobExclusions exclusions) =>
        RunAsync(JmsQueries.OpenScopeJobsWithModelsQuery(exclusions), "Open jobs");

    public Task<List<RepairerJobRow>> GetClosedJobsAsync(ScopeJobExclusions exclusions, int monthsBack) =>
        RunAsync(JmsQueries.ClosedScopeJobsWithModelsQuery(exclusions, monthsBack), "Closed jobs");

    public Task<List<RepairerJobRow>> GetAllClosedJobsAsync(ScopeJobExclusions exclusions) =>
        RunAsync(JmsQueries.AllClosedScopeJobsWithModelsQuery(exclusions), "All closed jobs");

    private async Task<List<RepairerJobRow>> RunAsync(string query, string label)
    {
        var jms = await _jmsRead.GetClientAsync();
        if (jms == null) return new();

        var res = await jms.PostQueryForCsvAsync(query);
        if (!res.Success) return new();

        var config = WaitingQuoteReport.MakeCsvConfig();
        using var reader = new StringReader(res.Content);
        using var csv = new CsvReader(reader, config);

        return csv.GetRecords<OpenJobRow>().Select(r => new RepairerJobRow(
            r.job_id,
            string.IsNullOrWhiteSpace(r.owner) ? $"(company {r.owner_company_id})" : r.owner,
            r.job_status ?? "",
            DateTime.TryParse(r.creation_date, out var d) ? d : DateTime.MinValue,
            DateTime.TryParse(r.completed_date, out var cd) ? cd : null,
            r.fault ?? "",
            (r.model_ids ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => int.TryParse(s.Trim(), out var id) ? id : -1)
                .Where(id => id > 0)
                .Distinct()
                .ToList(),
            r.sent_to_repairer == 1
        )).ToList();
    }

    private class OpenJobRow
    {
        public int job_id { get; set; }
        public int? owner_company_id { get; set; }
        public string? owner { get; set; }
        public string? job_status { get; set; }
        public string? creation_date { get; set; }
        public string? completed_date { get; set; }
        public string? fault { get; set; }
        public string? model_ids { get; set; }
        public int sent_to_repairer { get; set; }
    }
}
