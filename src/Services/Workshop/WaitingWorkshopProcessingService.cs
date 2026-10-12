using CsvHelper;
using CsvHelper.Configuration;
using System.Globalization;

using WorkshopDashboard.Services.External.Jms;
using WorkshopDashboard.Services.Jobs;
using WorkshopDashboard.Services.Stores;
namespace WorkshopDashboard.Services.Workshop;

public class WwpJobRecord
{
    public string JobId { get; set; } = "";
    public string Owner { get; set; } = "";
    public string Equipment { get; set; } = "";
    public string Fault { get; set; } = "";
    public DateTime FirstSetDate { get; set; }
    public DateTime? LastWorked { get; set; }
    public DateTime? Eta { get; set; }
    public int? EtaServiceId { get; set; }
    public bool ApprovalConfirmed { get; set; }
    public int? ApprovalServiceId { get; set; }
    public bool PoUploaded { get; set; }

    /// <summary>job.order_number says "Credit Applied" — paid with credit, so there's no PO to upload.</summary>
    public bool CreditApplied { get; set; }
}

/// <summary>
/// Data access for WaitingWorkshopProcessing.razor — loading the queue and saving its
/// ETA/approval notes. Mark-worked shares JobTouchService with WaitingQuoteConfirmation;
/// everything else here is specific to this page's queue.
/// </summary>
public class WaitingWorkshopProcessingService
{
    private readonly JmsReadSession _jmsRead;
    private readonly JmsUserSession _jmsSession;
    private readonly CurrentUserResolver _currentUser;

    public WaitingWorkshopProcessingService(JmsReadSession jmsRead, JmsUserSession jmsSession, CurrentUserResolver currentUser)
    {
        _jmsRead = jmsRead;
        _jmsSession = jmsSession;
        _currentUser = currentUser;
    }

    public async Task<List<WwpJobRecord>> GetJobsAsync(ScopeJobExclusions exclusions)
    {
        var jms = await _jmsRead.GetClientAsync();
        if (jms == null) return new();

        var query = JmsQueries.WaitingWorkshopProcessingQuery(exclusions);
        var res = await jms.PostQueryForCsvAsync(query);
        if (!res.Success) return new();

        using var reader = new StringReader(res.Content);
        using var csv = new CsvReader(reader, CsvConfig());

        return csv.GetRecords<JobCsvRow>()
            .Select(r => new WwpJobRecord
            {
                JobId = r.job_id.ToString(),
                Owner = r.owner ?? "",
                Equipment = r.equipment ?? "",
                Fault = r.fault ?? "",
                FirstSetDate = DateTime.TryParse(r.first_set_date, out var fd) ? fd : DateTime.MinValue,
                LastWorked = DateTime.TryParse(r.last_worked_date, out var lw) ? lw : (DateTime?)null,
                Eta = ParseEta(r.eta_raw),
                EtaServiceId = r.eta_service_id,
                ApprovalConfirmed = ParseApproval(r.approval_raw),
                ApprovalServiceId = r.approval_service_id,
                PoUploaded = r.po_count > 0,
                CreditApplied = r.order_number?.Contains("Credit Applied", StringComparison.OrdinalIgnoreCase) == true
            })
            .ToList();
    }

    /// <summary>Writes/updates the "ETA - yyyy-MM-dd" service_type_id = 11 note.</summary>
    public async Task<bool> SaveEtaAsync(int jobId, int? etaServiceId, DateTime eta, Action<string>? logWarning = null)
    {
        var user = await _currentUser.ResolveAsync();
        if (user is null)
        {
            logWarning?.Invoke($"Could not resolve current user; ETA not saved for job {jobId}.");
            return false;
        }

        var description = $"ETA - {eta:yyyy-MM-dd}";

        var res = etaServiceId.HasValue
            ? await _jmsSession.WriteAsync(new UpdateJobServiceWrite(jobId, etaServiceId.Value, new JobServiceUpdateRequest
            {
                ServiceTypeId = 11,
                Description = description,
                UserId = user.Value.Id,
                TechnicianName = user.Value.Name,
                ItemHide = true
            }))
            : await _jmsSession.WriteAsync(new AddJobServiceWrite(jobId, new JobServiceRequest
            {
                ServiceTypeId = 11,
                Description = description,
                UserId = user.Value.Id,
                ItemHide = true
            }));

        if (!res.Accepted)
            logWarning?.Invoke($"ETA save failed for job {jobId}: {res.ToResponse().StatusCode} {res.ToResponse().ReasonPhrase}");

        return res.Accepted;
    }

    /// <summary>Toggles the "Approval Confirmed"/"Approval Not Confirmed" service_type_id = 11 note.</summary>
    public async Task<bool> ToggleApprovalAsync(int jobId, int? approvalServiceId, bool newState, Action<string>? logWarning = null)
    {
        var user = await _currentUser.ResolveAsync();
        if (user is null)
        {
            logWarning?.Invoke($"Could not resolve current user; approval not saved for job {jobId}.");
            return false;
        }

        var description = newState ? "Approval Confirmed" : "Approval Not Confirmed";

        var res = approvalServiceId.HasValue
            ? await _jmsSession.WriteAsync(new UpdateJobServiceWrite(jobId, approvalServiceId.Value, new JobServiceUpdateRequest
            {
                ServiceTypeId = 11,
                Description = description,
                UserId = user.Value.Id,
                TechnicianName = user.Value.Name,
                ItemHide = true
            }))
            : await _jmsSession.WriteAsync(new AddJobServiceWrite(jobId, new JobServiceRequest
            {
                ServiceTypeId = 11,
                Description = description,
                UserId = user.Value.Id,
                ItemHide = true
            }));

        if (!res.Accepted)
            logWarning?.Invoke($"approval save failed for job {jobId}: {res.ToResponse().StatusCode} {res.ToResponse().ReasonPhrase}");

        return res.Accepted;
    }

    /// <summary>Parses the "ETA - yyyy-MM-dd" description written by SaveEtaAsync.</summary>
    private static DateTime? ParseEta(string? raw)
    {
        const string marker = "ETA - ";
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var i = raw.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;

        var datePart = raw[(i + marker.Length)..].Trim();
        return DateTime.TryParse(datePart, out var d) ? d : null;
    }

    /// <summary>True when the latest "Approval%" note on the job is "Approval Confirmed" rather than "Approval Not Confirmed".</summary>
    private static bool ParseApproval(string? raw) =>
        !string.IsNullOrWhiteSpace(raw) && raw.StartsWith("Approval Confirmed", StringComparison.OrdinalIgnoreCase);

    private static CsvConfiguration CsvConfig() => new(CultureInfo.InvariantCulture)
    {
        HeaderValidated = null,
        MissingFieldFound = null,
        PrepareHeaderForMatch = args => args.Header.ToLower().Trim()
    };

    private class JobCsvRow
    {
        public int job_id { get; set; }
        public string? owner { get; set; }
        public string? fault { get; set; }
        public string? job_status { get; set; }
        public string? equipment { get; set; }
        public string? last_worked_date { get; set; }
        public string? first_set_date { get; set; }
        public string? eta_raw { get; set; }
        public int? eta_service_id { get; set; }
        public string? approval_raw { get; set; }
        public int? approval_service_id { get; set; }
        public int po_count { get; set; }
        public string? order_number { get; set; }
    }
}
