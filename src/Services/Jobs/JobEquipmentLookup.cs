using WorkshopDashboard.Services.External.Jms;
using WorkshopDashboard.Services.Workshop;
namespace WorkshopDashboard.Services.Jobs;

public record JobEquipmentInfo(string EquipmentId, string SerialNo, string GenericName);

public record JobEquipmentSummary(
    int JobId,
    string SerialNo,
    string OwnerName,
    string Notes,
    string ManufacturerName,
    int? ModelId,
    string ModelNameNo,
    int JobPriorityId);

public record JobOwnerEquipmentSummary(
    int JobId,
    string OwnerCompanyName,
    string ManufacturerName,
    string ModelName,
    string SerialNo);

/// <summary>
/// Shared "load equipment for a job" query used by the quote extractor pages'
/// equipment selector, plus the two batch job+equipment+owner lookups used by
/// BulkStatusUpdate (all equipment ever linked) and InternationalShipping
/// (active equipment only — je.active_status = 't') — kept as two methods
/// rather than one, since the active-status filter changes which rows come
/// back and unifying them would silently change BulkStatusUpdate's results.
/// </summary>
public class JobEquipmentLookup
{
    private readonly JmsReadSession _jmsRead;

    public JobEquipmentLookup(JmsReadSession jmsRead) => _jmsRead = jmsRead;

    public async Task<List<JobEquipmentInfo>> LoadAsync(int jobId)
    {
        var jms = await _jmsRead.GetClientAsync();
        if (jms == null) return new();

        var query = JmsQueries.JobEquipmentQuery(jobId);

        var csv = await jms.PostQueryForCsvAsync(query);
        var lines = csv.Content.Trim().Split('\n');

        return lines.Skip(1)
            .Select(l => l.Split(','))
            .Where(parts => parts.Length >= 3)
            .Select(parts => new JobEquipmentInfo(
                parts[0].Trim(),
                parts[1].Trim(),
                parts[2].Trim()))
            .ToList();
    }

    // Batch job+equipment+owner summary for a list of job ids — superset of the columns
    // BulkStatusUpdate and InternationalShipping each used to query separately
    // (JobsByIdsWithEquipmentQuery / EquipmentByJobIdsQuery + JobsByIdsQuery).
    public async Task<List<JobEquipmentSummary>> LoadForJobsAsync(IEnumerable<int> jobIds)
    {
        var ids = jobIds.ToList();
        if (ids.Count == 0) return new();

        var jms = await _jmsRead.GetClientAsync();
        if (jms == null) return new();

        var query = JmsQueries.EquipmentByJobIdsQuery(ids);
        var csv = await jms.PostQueryForCsvAsync(query);
        if (!csv.Success) return new();

        var config = WaitingQuoteReport.MakeCsvConfig();
        using var reader = new StringReader(csv.Content);
        using var csvReader = new CsvHelper.CsvReader(reader, config);

        return csvReader.GetRecords<JobEquipmentSummaryRow>()
            .Select(r => new JobEquipmentSummary(
                r.job_id,
                r.serial_no ?? "",
                r.owner_name ?? "",
                r.notes ?? "",
                r.manufacturer_name ?? "",
                r.model_id,
                r.model_name_no ?? "",
                r.job_priority_id))
            .ToList();
    }

    private class JobEquipmentSummaryRow
    {
        public int job_id { get; set; }
        public string? serial_no { get; set; }
        public string? owner_name { get; set; }
        public string? notes { get; set; }
        public string? manufacturer_name { get; set; }
        public int? model_id { get; set; }
        public string? model_name_no { get; set; }
        public int job_priority_id { get; set; }
    }

    // Used by BulkStatusUpdate — every job's linked equipment regardless of active_status.
    public async Task<List<JobOwnerEquipmentSummary>> LoadOwnerEquipmentSummaryAsync(IEnumerable<int> jobIds)
    {
        var ids = jobIds.ToList();
        if (ids.Count == 0) return new();

        var jms = await _jmsRead.GetClientAsync();
        if (jms == null) return new();

        var query = JmsQueries.JobsByIdsWithEquipmentQuery(ids);
        var csv = await jms.PostQueryForCsvAsync(query);
        if (!csv.Success) return new();

        var config = WaitingQuoteReport.MakeCsvConfig();
        using var reader = new StringReader(csv.Content);
        using var csvReader = new CsvHelper.CsvReader(reader, config);

        return csvReader.GetRecords<JobOwnerEquipmentSummaryRow>()
            .Select(r => new JobOwnerEquipmentSummary(
                r.job_id,
                r.owner_company_name ?? "",
                r.manufacturer_name ?? "",
                r.model_name_no ?? "",
                r.serial_no ?? ""))
            .ToList();
    }

    private class JobOwnerEquipmentSummaryRow
    {
        public int job_id { get; set; }
        public string? owner_company_name { get; set; }
        public string? manufacturer_name { get; set; }
        public string? model_name_no { get; set; }
        public string? serial_no { get; set; }
    }
}
