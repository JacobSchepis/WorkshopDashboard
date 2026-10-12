using CsvHelper;
using CsvHelper.Configuration;
using System.Globalization;

using WorkshopDashboard.Services.External.Jms;
using WorkshopDashboard.Services.Workshop;
namespace WorkshopDashboard.Services.Admin;

public record ScopeCompanyRow(int CompanyId, string? CompanyName);

public record ScopeModelRow(int ModelId, string? Label);

public record ScopeContactRow(int ContactId, string? FullName, string? Email, string? PhoneNo, string? Mobile, int? CompanyId, string? CompanyName);

public record JobScopeLookup(int JobId, int? OwnerCompanyId, string? OwnerCompanyName, List<(int ModelId, string? Label)> Models);

/// <summary>
/// Small autocomplete/label lookups shared by the admin scope pages (ScopeJobManager,
/// ScopeCustomerManager, LoanEquipmentManager, RepairerManager, CustomsInfoManager,
/// SendCustomerEmail) — company names, model labels, contact details, and their search
/// variants. One class because they're all "tiny lookup against JMS for an admin
/// autocomplete/chip," not because the pages that use them are related. Formatting
/// (e.g. "Acme Ltd (123)") stays in each page — this only returns raw fields.
/// </summary>
public class ScopeLookupService
{
    private readonly JmsReadSession _jmsRead;

    public ScopeLookupService(JmsReadSession jmsRead) => _jmsRead = jmsRead;

    public async Task<List<ScopeCompanyRow>> GetCompanyNamesAsync(IReadOnlyCollection<int> companyIds)
    {
        if (companyIds.Count == 0) return new();

        var jms = await _jmsRead.GetClientAsync();
        if (jms == null) return new();

        var res = await jms.PostQueryForCsvAsync(JmsQueries.CompanyNamesQuery(companyIds));
        if (!res.Success) return new();

        return ParseCsv<CompanyNameCsvRow>(res.Content)
            .Select(r => new ScopeCompanyRow(r.company_id, r.company_name))
            .ToList();
    }

    public async Task<List<ScopeCompanyRow>> SearchCompaniesAsync(string term)
    {
        var jms = await _jmsRead.GetClientAsync();
        if (jms == null) return new();

        var res = await jms.PostQueryForCsvAsync(JmsQueries.CompanySearchQuery(term));
        if (!res.Success) return new();

        return ParseCsv<CompanyNameCsvRow>(res.Content)
            .Select(r => new ScopeCompanyRow(r.company_id, r.company_name))
            .ToList();
    }

    public async Task<List<ScopeCompanyRow>> SearchClientsAsync(string term)
    {
        var jms = await _jmsRead.GetClientAsync();
        if (jms == null) return new();

        var res = await jms.PostQueryForCsvAsync(JmsQueries.ClientSearchQuery(term));
        if (!res.Success) return new();

        return ParseCsv<CompanyNameCsvRow>(res.Content)
            .Select(r => new ScopeCompanyRow(r.company_id, r.company_name))
            .ToList();
    }

    public async Task<List<ScopeModelRow>> GetModelLabelsAsync(IReadOnlyCollection<int> modelIds)
    {
        if (modelIds.Count == 0) return new();

        var jms = await _jmsRead.GetClientAsync();
        if (jms == null) return new();

        var res = await jms.PostQueryForCsvAsync(JmsQueries.ModelLabelsQuery(modelIds));
        if (!res.Success) return new();

        return ParseCsv<ModelLabelCsvRow>(res.Content)
            .Select(r => new ScopeModelRow(r.model_id, r.label))
            .ToList();
    }

    public async Task<List<ScopeModelRow>> SearchModelsAsync(string term)
    {
        var jms = await _jmsRead.GetClientAsync();
        if (jms == null) return new();

        var res = await jms.PostQueryForCsvAsync(JmsQueries.ModelSearchQuery(term));
        if (!res.Success) return new();

        return ParseCsv<ModelLabelCsvRow>(res.Content)
            .Select(r => new ScopeModelRow(r.model_id, r.label))
            .ToList();
    }

    public async Task<List<ScopeContactRow>> GetContactDetailsAsync(IReadOnlyCollection<int> contactIds)
    {
        if (contactIds.Count == 0) return new();

        var jms = await _jmsRead.GetClientAsync();
        if (jms == null) return new();

        var res = await jms.PostQueryForCsvAsync(JmsQueries.ContactDetailsQuery(contactIds));
        if (!res.Success) return new();

        return ParseCsv<ContactCsvRow>(res.Content)
            .Select(r => new ScopeContactRow(r.contact_id, r.full_name, r.email, r.phone_no, r.mobile, r.company_id, r.company_name))
            .ToList();
    }

    public async Task<List<ScopeContactRow>> SearchContactsAsync(string term, IEnumerable<int> companyIds)
    {
        var jms = await _jmsRead.GetClientAsync();
        if (jms == null) return new();

        var res = await jms.PostQueryForCsvAsync(JmsQueries.ContactSearchQuery(term, companyIds));
        if (!res.Success) return new();

        return ParseCsv<ContactCsvRow>(res.Content)
            .Select(r => new ScopeContactRow(r.contact_id, r.full_name, r.email, r.phone_no, r.mobile, r.company_id, r.company_name))
            .ToList();
    }

    public async Task<JobScopeLookup?> GetJobScopeLookupAsync(int jobId)
    {
        var jms = await _jmsRead.GetClientAsync();
        if (jms == null) return null;

        var res = await jms.PostQueryForCsvAsync(JmsQueries.JobScopeLookupQuery(jobId));
        if (!res.Success) throw new Exception($"Lookup failed: {res.StatusCode} {res.ReasonPhrase}");

        var rows = ParseCsv<JobScopeLookupCsvRow>(res.Content).ToList();
        if (rows.Count == 0) return null;

        var first = rows[0];
        return new JobScopeLookup(
            jobId,
            first.owner_company_id,
            first.owner_company_name,
            rows.Where(r => r.model_id.HasValue)
                .Select(r => (r.model_id!.Value, r.model_label))
                .DistinctBy(m => m.Item1)
                .ToList());
    }

    private static IEnumerable<T> ParseCsv<T>(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return Enumerable.Empty<T>();
        using var reader = new StringReader(content);
        using var csv = new CsvReader(reader, CsvConfig());
        return csv.GetRecords<T>().ToList();
    }

    private static CsvConfiguration CsvConfig() => WaitingQuoteReport.MakeCsvConfig();

    private class CompanyNameCsvRow
    {
        public int company_id { get; set; }
        public string? company_name { get; set; }
    }

    private class ModelLabelCsvRow
    {
        public int model_id { get; set; }
        public string? label { get; set; }
    }

    private class ContactCsvRow
    {
        public int contact_id { get; set; }
        public string? full_name { get; set; }
        public string? email { get; set; }
        public string? phone_no { get; set; }
        public string? mobile { get; set; }
        public int? company_id { get; set; }
        public string? company_name { get; set; }
    }

    private class JobScopeLookupCsvRow
    {
        public int job_id { get; set; }
        public int? owner_company_id { get; set; }
        public string? owner_company_name { get; set; }
        public int? model_id { get; set; }
        public string? model_label { get; set; }
    }
}
