using CsvHelper;
using CsvHelper.Configuration;
using System.Globalization;

using WorkshopDashboard.Services.External.Jms;
using WorkshopDashboard.Services.Workshop;
namespace WorkshopDashboard.Services.Jobs;

public record InspectionEquipmentInfo(string? SerialNo, string? AssetNo, int? ModelId, string? Comments, int ResourceCount);

public record TestEquipmentStatus(
    int EquipmentId,
    string? SerialNo,
    string? ModelName,
    DateTime? LastInspectionDate,
    string? OperationalStatus,
    int? CertificateId,
    string? CertificateFilename,
    DateTime? CalibrationExpiry,
    int? LastInspectionId)
{
    /// <summary>Calibrated if the latest certificate hasn't expired as of today.</summary>
    public bool InCalibration => CalibrationExpiry != null && CalibrationExpiry.Value.Date >= DateTime.Today;
}

/// <summary>A job's latest inspection of one piece of equipment. ScopeModel is the inspected
/// unit's manufacturer and model ("Olympus GIF-H190"), falling back to its generic name when
/// JMS has no model on it, or null if there's neither.</summary>
public record LatestInspection(int InspectionId, DateTime? Date, string? Comments, string? ScopeModel = null);

public record InspectionJobMeta(int? OwnerCompanyId, int? ReportId, string? RsJobNo, string? Area, DateTime? LastWorkedDate);

public record InspectionTestResultRow(
    int? InspectionId,
    int? ResultId,
    int? TestScriptItemId,
    int? TestTypeId,
    int? TestEquipmentId,
    string? ResultValue);

/// <summary>
/// The queries ServiceReport.razor's RunReport step runs, previously
/// inline raw SQL in the page (the only page bypassing JmsQueries.cs entirely). Compliance
/// logic (EquipmentRequiredTypeIds) stays in the page — it's report-display business logic,
/// not a data-access concern.
/// </summary>
public class InspectionReportLookup
{
    private readonly JmsReadSession _jmsRead;

    public InspectionReportLookup(JmsReadSession jmsRead) => _jmsRead = jmsRead;

    public async Task<List<int>> GetInspectionIdsForJobAsync(int jobId)
    {
        var jms = await _jmsRead.GetClientAsync();
        if (jms == null) return new();

        var csv = await jms.PostQueryForCsvAsync(JmsQueries.InspectionIdsForJobQuery(jobId));
        if (!csv.Success) throw new Exception($"Inspection lookup failed: {csv.StatusCode} {csv.ReasonPhrase}");

        return ParseCsv<InspectionRow>(csv.Content).Select(r => r.inspection_id).ToList();
    }

    public async Task<Dictionary<int, InspectionEquipmentInfo>> GetEquipmentInfoAsync(IEnumerable<int> inspectionIds)
    {
        var jms = await _jmsRead.GetClientAsync();
        if (jms == null) return new();

        var csv = await jms.PostQueryForCsvAsync(JmsQueries.InspectionEquipmentQuery(inspectionIds));
        if (!csv.Success) throw new Exception($"Equipment lookup failed: {csv.StatusCode} {csv.ReasonPhrase}");

        // GroupBy + take first row per inspection, rather than ToDictionary directly, so a
        // job whose equipment join ever returns >1 row for the same inspection_id doesn't
        // crash the whole report with a duplicate-key exception.
        return ParseCsv<EquipmentInfoRow>(csv.Content)
            .GroupBy(r => r.inspection_id)
            .ToDictionary(g => g.Key, g =>
            {
                var r = g.First();
                return new InspectionEquipmentInfo(r.serial_no, r.asset_no, r.model_id, r.comments, r.resource_count);
            });
    }

    /// <summary>The latest inspection of <paramref name="equipmentId"/> on <paramref name="jobId"/>,
    /// or null if it hasn't been inspected on this job.</summary>
    public async Task<LatestInspection?> GetLatestInspectionAsync(int jobId, int equipmentId)
    {
        var jms = await _jmsRead.GetClientAsync();
        if (jms == null) return null;

        var csv = await jms.PostQueryForCsvAsync(JmsQueries.LatestJobEquipmentInspectionQuery(jobId, equipmentId));
        if (!csv.Success) throw new Exception($"Inspection lookup failed: {csv.StatusCode} {csv.ReasonPhrase}");

        var row = ParseCsv<LatestInspectionRow>(csv.Content).FirstOrDefault();
        return row == null ? null : new LatestInspection(row.inspection_id, row.creation_date, row.comments, ScopeModelOf(row));
    }

    private static string? ScopeModelOf(LatestInspectionRow row)
    {
        var model = string.Join(" ", new[] { row.manufacturer, row.model_name_no }
            .Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim()));
        if (model.Length > 0) return model;
        return string.IsNullOrWhiteSpace(row.generic_name) ? null : row.generic_name.Trim();
    }

    public async Task<Dictionary<int, TestEquipmentStatus>> GetTestEquipmentStatusAsync(IEnumerable<int> equipmentIds)
    {
        var ids = equipmentIds.Distinct().ToList();
        if (ids.Count == 0) return new();

        var jms = await _jmsRead.GetClientAsync();
        if (jms == null) return new();

        var csv = await jms.PostQueryForCsvAsync(JmsQueries.TestEquipmentStatusQuery(ids));
        if (!csv.Success) throw new Exception($"Test equipment lookup failed: {csv.StatusCode} {csv.ReasonPhrase}");

        return ParseCsv<TestEquipmentRow>(csv.Content)
            .GroupBy(r => r.equipment_id)
            .ToDictionary(g => g.Key, g =>
            {
                var r = g.First();
                return new TestEquipmentStatus(r.equipment_id, r.serial_no, r.model_name_no, r.last_inspection_date, r.operational_status,
                    r.certificate_id, r.certificate_filename, r.calibration_expiry,
                    r.last_inspection_id);
            });
    }

    public async Task<InspectionJobMeta?> GetJobMetaAsync(int jobId)
    {
        var jms = await _jmsRead.GetClientAsync();
        if (jms == null) return null;

        var csv = await jms.PostQueryForCsvAsync(JmsQueries.InspectionJobMetaQuery(jobId));
        if (!csv.Success) return null;

        var row = ParseCsv<JobMetaRow>(csv.Content).FirstOrDefault();
        if (row == null) return null;

        var (rsJobNo, area) = ParseRoutineServiceNotes(row.notes);
        return new InspectionJobMeta(row.owner_company_id, row.rs_report_id, rsJobNo, area, row.last_worked_date);
    }

    // Routine service job notes read "Routine Service Job - 2026-279 --- Medical Gas
    // Adaptors only - Level 1 Oncology Infusion centre". The area can itself contain
    // " - ", so split on the "---" separator rather than on dashes.
    private static readonly System.Text.RegularExpressions.Regex RoutineServiceNotesPattern = new(
        @"Routine\s+Service\s+Job\s*-\s*(?<no>[^\s-]+(?:-[^\s-]+)*)\s*-{2,}\s*(?<area>.+)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    public static (string? RsJobNo, string? Area) ParseRoutineServiceNotes(string? notes)
    {
        var match = RoutineServiceNotesPattern.Match(notes ?? "");
        if (!match.Success) return (null, null);
        return (match.Groups["no"].Value.Trim(), match.Groups["area"].Value.Trim());
    }

    public async Task<List<InspectionTestResultRow>> GetTestResultsAsync(IEnumerable<int> inspectionIds)
    {
        var jms = await _jmsRead.GetClientAsync();
        if (jms == null) return new();

        var csv = await jms.PostQueryForCsvAsync(JmsQueries.InspectionTestResultsQuery(inspectionIds));
        if (!csv.Success) throw new Exception($"Results lookup failed: {csv.StatusCode} {csv.ReasonPhrase}");

        return ParseCsv<ResultRow>(csv.Content)
            .Select(r => new InspectionTestResultRow(r.inspection_id, r.result_id, r.test_script_item_id, r.test_type_id, r.test_equipment_id, r.result_value))
            .ToList();
    }

    private static IEnumerable<T> ParseCsv<T>(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return Enumerable.Empty<T>();

        using var reader = new StringReader(content);
        using var csv = new CsvReader(reader, CsvConfig());
        return csv.GetRecords<T>().ToList();
    }

    private static CsvConfiguration CsvConfig() => WaitingQuoteReport.MakeCsvConfig();

    private class InspectionRow
    {
        public int inspection_id { get; set; }
    }

    private class LatestInspectionRow
    {
        public int inspection_id { get; set; }
        public DateTime? creation_date { get; set; }
        public string? comments { get; set; }
        public string? manufacturer { get; set; }
        public string? model_name_no { get; set; }
        public string? generic_name { get; set; }
    }

    private class EquipmentInfoRow
    {
        public int inspection_id { get; set; }
        public string? serial_no { get; set; }
        public string? asset_no { get; set; }
        public int? model_id { get; set; }
        public string? comments { get; set; }
        public int resource_count { get; set; }
    }

    private class TestEquipmentRow
    {
        public int equipment_id { get; set; }
        public string? serial_no { get; set; }
        public string? model_name_no { get; set; }
        public DateTime? last_inspection_date { get; set; }
        public string? operational_status { get; set; }
        public int? certificate_id { get; set; }
        public string? certificate_filename { get; set; }
        public DateTime? calibration_expiry { get; set; }
        public int? last_inspection_id { get; set; }
    }

    private class JobMetaRow
    {
        public int? owner_company_id { get; set; }
        public int? rs_report_id { get; set; }
        public string? notes { get; set; }
        public DateTime? last_worked_date { get; set; }
    }

    private class ResultRow
    {
        public int? inspection_id { get; set; }
        public int? result_id { get; set; }
        public int? test_script_item_id { get; set; }
        public int? test_type_id { get; set; }
        public int? test_equipment_id { get; set; }
        public string? result_value { get; set; }
    }
}
