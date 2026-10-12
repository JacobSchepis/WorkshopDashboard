using CsvHelper;
using CsvHelper.Configuration;
using System.Globalization;

using WorkshopDashboard.Abstract;
using WorkshopDashboard.Services.External.Jms;
namespace WorkshopDashboard.Services.Workshop;

public class ShippingJobRecord
{
    public int JobId { get; set; }
    public string Notes { get; set; } = "";
    public string Owner { get; set; } = "";
    public string Model { get; set; } = "";
    public DateTime StatusChangeDate { get; set; }
    public List<string> Connote { get; set; } = new();
}

/// <summary>
/// Jobs-with-history lookup for ShippingChecks.razor's three sections (waiting arrival,
/// waiting outward shipping, unit departed) — same query shape JmsQueries.JobsWithHistoryQuery
/// documents as shared with Forms/JobListSection.razor.
/// </summary>
public class ShippingJobLookup
{
    private readonly JmsReadSession _jmsRead;

    public ShippingJobLookup(JmsReadSession jmsRead) => _jmsRead = jmsRead;

    // connoteStatusLabel is the job_status text whose history note holds the connote — not
    // necessarily the same status being queried (e.g. Unit Departed jobs already moved past
    // Waiting Outward Shipping, but that's still where the connote was recorded).
    public async Task<List<ShippingJobRecord>> GetJobsAsync(int jobStatusId, bool filterHistoryToStatus, string connoteStatusLabel)
    {
        var jms = await _jmsRead.GetClientAsync();
        return jms == null ? new() : await GetJobsAsync(jms, jobStatusId, filterHistoryToStatus, connoteStatusLabel);
    }

    // Static overload for callers that already hold their own IJmsReader — the kiosk
    // WorkshopNotifications page (via HeldShipmentLookup), same reasoning as
    // DashboardLookup's static overloads.
    public static async Task<List<ShippingJobRecord>> GetJobsAsync(IJmsReader jms, int jobStatusId, bool filterHistoryToStatus, string connoteStatusLabel)
    {
        var query = JmsQueries.JobsWithHistoryQuery(jobStatusId, filterHistoryToStatus);
        var res = await jms.PostQueryForCsvAsync(query);

        if (!res.Success) return new();

        var config = WaitingQuoteReport.MakeCsvConfig();

        using var reader = new StringReader(res.Content);
        using var csv = new CsvReader(reader, config);

        var rows = csv.GetRecords<JobCsvRow>().ToList();
        var grouped = rows.GroupBy(r => r.job_id);

        var jobs = new List<ShippingJobRecord>();
        foreach (var group in grouped)
        {
            var first = group.First();

            var job = new ShippingJobRecord
            {
                JobId = group.Key,
                Notes = first.job_notes ?? "",
                Owner = first.company_name ?? "",
                Model = first.model_name_no ?? ""
            };

            job.StatusChangeDate = group
                .Where(r => DateTime.TryParse(r.creation_date, out _))
                .Select(r => DateTime.Parse(r.creation_date!))
                .OrderByDescending(d => d)
                .FirstOrDefault() is DateTime d && d != default ? d : DateTime.Today;

            job.Connote = group
                .Where(r => r.job_status?.Equals(connoteStatusLabel, StringComparison.OrdinalIgnoreCase) == true
                         && !string.IsNullOrWhiteSpace(r.history_notes))
                .Select(r => r.history_notes!)
                .Distinct()
                .ToList();

            jobs.Add(job);
        }

        return jobs.OrderBy(j => j.Owner).ToList();
    }

    private class JobCsvRow
    {
        public int job_id { get; set; }
        public string? job_notes { get; set; }
        public string? first_name { get; set; }
        public string? job_status { get; set; }
        public string? history_notes { get; set; }
        public string? creation_date { get; set; }
        public string? company_name { get; set; }
        public string? model_name_no { get; set; }
    }
}
