using CsvHelper;
using CsvHelper.Configuration;
using System.Globalization;

using WorkshopDashboard.Abstract;
using WorkshopDashboard.Services.External.Jms;
using WorkshopDashboard.Services.Stores;
using WorkshopDashboard.Services.Workshop;
namespace WorkshopDashboard.Services.Admin;

public record LoanEquipmentRow(
    int EquipmentId,
    int? ModelId,
    int? ManufacturerId,
    string? ManufacturerName,
    string? ModelNameNo,
    string? SerialNo,
    string? LastInspectedDate,
    int? LoanJobId,
    string? LoanedDate,
    int AgreementCount);

public record PreviousOwnerRow(int EquipmentId, int CompanyId, string? CompanyName);

public record CoveringJobRow(int JobId, string? JobStatus, string? QuotedDate, string? Equipment);

public record CustomerModelJobRow(
    int JobId,
    string? JobStatus,
    string? QuotedDate,
    int ModelId,
    int? ManufacturerId,
    string? ManufacturerName,
    string? ModelName,
    int CompanyId,
    string? CompanyName,
    string? SerialNo);

/// <summary>JMS data access for LoanEquipmentManager.razor's fleet view and its
/// "covering jobs" cross-reference.</summary>
public class LoanEquipmentLookup
{
    private readonly JmsReadSession _jmsRead;

    public LoanEquipmentLookup(JmsReadSession jmsRead) => _jmsRead = jmsRead;

    public async Task<List<LoanEquipmentRow>?> GetLoanEquipmentAsync()
    {
        var jms = await _jmsRead.GetClientAsync();
        return jms == null ? null : await GetLoanEquipmentAsync(jms);
    }

    // Static overload for callers that already hold their own IJmsReader — RuleCheckService,
    // which runs from the kiosk WorkshopNotifications page (see DashboardLookup's static
    // overloads for the same reasoning).
    public static async Task<List<LoanEquipmentRow>?> GetLoanEquipmentAsync(IJmsReader jms)
    {
        var res = await jms.PostQueryForCsvAsync(JmsQueries.LoanEquipmentQuery());
        if (!res.Success) return null;

        using var reader = new StringReader(res.Content);
        using var csv = new CsvReader(reader, CsvConfig());
        return csv.GetRecords<LoanEquipmentCsvRow>()
            .Select(r => new LoanEquipmentRow(
                r.equipment_id, r.model_id, r.manufacturer_id, r.manufacturer_name, r.model_name_no,
                r.serial_no, r.last_inspected_date, r.loan_job_id, r.loaned_date, r.agreement_count))
            .ToList();
    }

    public async Task<List<PreviousOwnerRow>> GetPreviousOwnersAsync(IReadOnlyCollection<int> equipmentIds)
    {
        if (equipmentIds.Count == 0) return new();

        var jms = await _jmsRead.GetClientAsync();
        if (jms == null) return new();

        var res = await jms.PostQueryForCsvAsync(JmsQueries.LoanEquipmentPreviousOwnersQuery(equipmentIds));
        if (!res.Success) return new();

        using var reader = new StringReader(res.Content);
        using var csv = new CsvReader(reader, CsvConfig());
        return csv.GetRecords<PreviousOwnerCsvRow>()
            .Select(r => new PreviousOwnerRow(r.equipment_id, r.company_id, r.company_name))
            .ToList();
    }

    public async Task<List<CoveringJobRow>> GetCoveringJobsAsync(IReadOnlyCollection<int> jobIds)
    {
        var jms = await _jmsRead.GetClientAsync();
        return jms == null ? new() : await GetCoveringJobsAsync(jms, jobIds);
    }

    // Static overload for callers that already hold their own IJmsReader — RuleCheckService,
    // which runs from the kiosk WorkshopNotifications page (see DashboardLookup's static
    // overloads for the same reasoning).
    public static async Task<List<CoveringJobRow>> GetCoveringJobsAsync(IJmsReader jms, IReadOnlyCollection<int> jobIds)
    {
        if (jobIds.Count == 0) return new();

        var res = await jms.PostQueryForCsvAsync(JmsQueries.CoveringJobsQuery(jobIds));
        if (!res.Success) return new();

        using var reader = new StringReader(res.Content);
        using var csv = new CsvReader(reader, CsvConfig());
        return csv.GetRecords<CoveringJobCsvRow>()
            .Select(r => new CoveringJobRow(r.job_id, r.job_status, r.quoted_date, r.equipment))
            .ToList();
    }

    public async Task<List<CustomerModelJobRow>?> GetCustomerModelJobsAsync(
        IReadOnlyCollection<int> modelIds, IReadOnlyCollection<int> companyIds, ScopeJobExclusions exclusions)
    {
        if (modelIds.Count == 0 || companyIds.Count == 0) return new();

        var jms = await _jmsRead.GetClientAsync();
        if (jms == null) return null;

        var res = await jms.PostQueryForCsvAsync(JmsQueries.CustomerModelJobsQuery(modelIds, companyIds, exclusions));
        if (!res.Success) return null;

        using var reader = new StringReader(res.Content);
        using var csv = new CsvReader(reader, CsvConfig());
        return csv.GetRecords<CustomerModelJobCsvRow>()
            .Select(r => new CustomerModelJobRow(
                r.job_id, r.job_status, r.quoted_date, r.model_id, r.manufacturer_id, r.manufacturer_name, r.model_name_no, r.current_company_id,
                r.company_name, r.serial_no))
            .ToList();
    }

    private static CsvConfiguration CsvConfig() => WaitingQuoteReport.MakeCsvConfig();

    private class LoanEquipmentCsvRow
    {
        public int equipment_id { get; set; }
        public int? model_id { get; set; }
        public int? manufacturer_id { get; set; }
        public string? manufacturer_name { get; set; }
        public string? model_name_no { get; set; }
        public string? serial_no { get; set; }
        public string? last_inspected_date { get; set; }
        public int? loan_job_id { get; set; }
        public string? loaned_date { get; set; }
        public int agreement_count { get; set; }
    }

    private class PreviousOwnerCsvRow
    {
        public int equipment_id { get; set; }
        public int company_id { get; set; }
        public string? company_name { get; set; }
    }

    private class CoveringJobCsvRow
    {
        public int job_id { get; set; }
        public string? job_status { get; set; }
        public string? quoted_date { get; set; }
        public string? equipment { get; set; }
    }

    private class CustomerModelJobCsvRow
    {
        public int job_id { get; set; }
        public string? job_status { get; set; }
        public string? quoted_date { get; set; }
        public int model_id { get; set; }
        public int? manufacturer_id { get; set; }
        public string? manufacturer_name { get; set; }
        public string? model_name_no { get; set; }
        public int current_company_id { get; set; }
        public string? company_name { get; set; }
        public string? serial_no { get; set; }
    }
}
