using System.Text.RegularExpressions;
using CsvHelper;

using WorkshopDashboard.Models;
using WorkshopDashboard.Services.External.Jms;
using WorkshopDashboard.Services.Workshop;

namespace WorkshopDashboard.Services.Jobs;

// ═══════════════════════════════════════════════════════════════════════════
//  BulkJobCreationService
//
//  Drives BulkJobCreate.razor: given an owner + bill-to company/contact pair and
//  a list of planned jobs (each with its own real notes and equipment picked
//  BEFORE anything is created), resolves every field CreateJob's own JS
//  would have auto-filled (see JmsQueries.OwnerJobDefaultsQuery/
//  CompanyBillingAddressQuery), then per planned job POSTs CreateJob via
//  JmsClient.CreateJobAsync followed by one AddJobEquipment POST per
//  picked equipment unit — so nothing gets created until the user is actually
//  happy with what each job should contain, and no separate "go back and add
//  the real notes" step is needed (they're in the create POST from the start).
//
//  On success JMS redirects to JobSummary?id={jobId}; the new job id is
//  read back off that redirected URL rather than the response body.
// ═══════════════════════════════════════════════════════════════════════════

public record OwnerJobDefaults(
    string CompanyName,
    string DefaultShipToRaw,
    string ShippingAddress,
    decimal? StandardLabourRate,
    int LabourBasis,
    int TravelBasis,
    int PartsBasis);

public record ShipToDetails(string Name, string Address);

public record EquipmentSearchHit(int EquipmentId, string SerialNo, string ModelNameNo, string BarcodeNo);

/// <summary>One job's worth of user-entered content, gathered before any job exists.</summary>
public record PlannedJob(string Instructions, IReadOnlyList<EquipmentSearchHit> Equipment);

public record EquipmentAddOutcome(EquipmentSearchHit Equipment, bool Success, string? Error);

public record BulkJobCreationResult(
    int Attempt,
    int? JobId,
    string? Error,
    CreateJobRequest? Request = null,
    IReadOnlyList<EquipmentAddOutcome>? EquipmentResults = null)
{
    public bool Success => JobId.HasValue;
}

public class BulkJobCreationService
{
    // Fixed per the workshop's own process — every bulk-created job uses these.
    public const int BusinessTypeId = 6;
    public const int JobPriorityId = 3;
    public const decimal LabourEstimateHours = 5m;

    private readonly JmsReadSession _jmsRead;
    private readonly JmsUserSession _jmsSession;
    private readonly CurrentUserResolver _currentUser;
    private readonly ILogger<BulkJobCreationService> _logger;

    public BulkJobCreationService(JmsReadSession jmsRead, JmsUserSession jmsSession, CurrentUserResolver currentUser,
        ILogger<BulkJobCreationService> logger)
    {
        _jmsRead = jmsRead;
        _jmsSession = jmsSession;
        _currentUser = currentUser;
        _logger = logger;
    }

    /// <summary>Last day of the current month, unless fewer than 7 days remain in it — in which
    /// case the last day of next month, so a job never lands with only a few days' notice.</summary>
    public static DateTime ComputeRequiredDate(DateTime today)
    {
        var lastDayThisMonth = new DateTime(today.Year, today.Month, DateTime.DaysInMonth(today.Year, today.Month));
        if ((lastDayThisMonth - today).Days >= 7)
            return lastDayThisMonth;

        var nextMonth = today.AddMonths(1);
        return new DateTime(nextMonth.Year, nextMonth.Month, DateTime.DaysInMonth(nextMonth.Year, nextMonth.Month));
    }

    public async Task<OwnerJobDefaults?> GetOwnerDefaultsAsync(int ownerCompanyId, CancellationToken ct = default)
    {
        var jms = await _jmsRead.GetClientAsync();
        if (jms == null) return null;

        var query = JmsQueries.OwnerJobDefaultsQuery(ownerCompanyId);
        var res = await jms.PostQueryForCsvAsync(query);

        if (!res.Success) return null;

        var row = ParseCsv<OwnerDefaultsCsvRow>(res.Content ?? "").FirstOrDefault();
        if (row == null)
        {
            _logger.LogWarning("No job-creation defaults found for owner company {CompanyId}", ownerCompanyId);
            return null;
        }

        return new OwnerJobDefaults(
            row.company_name ?? "",
            row.default_ship_to ?? "",
            row.shipping_address ?? "",
            row.standard_labour_rate,
            row.labour_pref ?? 0,
            row.travel_pref ?? 0,
            row.part_pref ?? 0);
    }

    /// <summary>A contact's department id (company_people.company_department_id) — CreateJob's
    /// "department"/"bill_department" hidden fields are filled with this the moment a contact is
    /// picked, not left blank.</summary>
    public async Task<int?> GetContactDepartmentIdAsync(int contactId, CancellationToken ct = default)
    {
        var jms = await _jmsRead.GetClientAsync();
        if (jms == null) return null;

        var res = await jms.PostQueryForCsvAsync(JmsQueries.ContactDepartmentIdQuery(contactId));
        if (!res.Success) return null;

        var departmentId = ParseCsv<ContactDepartmentCsvRow>(res.Content).FirstOrDefault()?.company_department_id;
        return departmentId;
    }

    /// <summary>Resolves the actual ship-to name/address for the owner company. `defaultShipToRaw`
    /// is `company.default_ship_to` — a company_id stored as text, per the live CreateJob's
    /// own JS (`if (data.default_ship_to != ui.item.company_id) {...} else { ship_to = company_name }`).
    /// Self-ship (the common case, and what a NULL/unparseable/matching value falls back to) uses the
    /// owner's own name/shipping address; a genuinely different id gets looked up.</summary>
    public async Task<ShipToDetails> ResolveShipToAsync(
        int ownerCompanyId, string ownerCompanyName, string ownerShippingAddress,
        string defaultShipToRaw, CancellationToken ct = default)
    {
        if (!int.TryParse(defaultShipToRaw, out var shipToCompanyId) || shipToCompanyId == ownerCompanyId)
        {
            return new ShipToDetails(ownerCompanyName, ownerShippingAddress);
        }

        var jms = await _jmsRead.GetClientAsync();
        if (jms == null) return new ShipToDetails(ownerCompanyName, ownerShippingAddress);

        var res = await jms.PostQueryForCsvAsync(JmsQueries.CompanyShippingDetailsQuery(shipToCompanyId));

        var row = res.Success ? ParseCsv<CompanyShippingCsvRow>(res.Content).FirstOrDefault() : null;
        if (row == null)
        {
            _logger.LogWarning("Ship-to company {CompanyId} lookup failed — shipping to the owner instead", shipToCompanyId);
            return new ShipToDetails(ownerCompanyName, ownerShippingAddress);
        }

        var details = new ShipToDetails(row.company_name ?? ownerCompanyName, row.shipping_address ?? "");
        return details;
    }

    public async Task<string> GetBillingAddressAsync(int billToCompanyId, CancellationToken ct = default)
    {
        var jms = await _jmsRead.GetClientAsync();
        if (jms == null) return "";

        var res = await jms.PostQueryForCsvAsync(JmsQueries.CompanyBillingAddressQuery(billToCompanyId));
        if (!res.Success) return "";

        var address = ParseCsv<CompanyAddressCsvRow>(res.Content).FirstOrDefault()?.address ?? "";
        return address;
    }

    /// <summary>Creates one job per entry in <paramref name="plannedJobs"/>, one CreateJob POST
    /// at a time (JMS has no bulk-create endpoint), then adds that job's picked equipment via
    /// AddJobEquipment. Stops and returns what it has so far if the current user can't be
    /// resolved or the owner's defaults can't be looked up; individual job/equipment POST failures
    /// are captured per-row instead of aborting the whole batch.</summary>
    public async Task<List<BulkJobCreationResult>> CreateJobsAsync(
        int ownerCompanyId, string ownerCompanyName,
        int ownerContactId, string ownerContactName,
        int billToCompanyId, string billToCompanyName,
        int billContactId, string billContactName,
        IReadOnlyList<PlannedJob> plannedJobs,
        CancellationToken ct = default)
    {
        _logger.LogInformation("Creating {Count} job(s) for owner company {OwnerCompanyId}, billed to {BillToCompanyId}",
            plannedJobs.Count, ownerCompanyId, billToCompanyId);

        var results = new List<BulkJobCreationResult>();

        var jms = await _jmsSession.GetClientAsync();
        if (jms == null)
        {
            _logger.LogWarning("Bulk job creation stopped: not logged in to JMS");
            results.Add(new BulkJobCreationResult(0, null, "Not authenticated with JMS."));
            return results;
        }

        var currentUser = await _currentUser.ResolveAsync();
        if (currentUser == null)
        {
            _logger.LogWarning("Bulk job creation stopped: could not resolve the logged-in JMS user");
            results.Add(new BulkJobCreationResult(0, null, "Could not resolve the logged-in JMS user."));
            return results;
        }

        var defaults = await GetOwnerDefaultsAsync(ownerCompanyId, ct);
        if (defaults == null)
        {
            _logger.LogWarning("Bulk job creation stopped: no defaults for owner company {CompanyId}", ownerCompanyId);
            results.Add(new BulkJobCreationResult(0, null, $"Could not look up job-creation defaults for owner company {ownerCompanyId}."));
            return results;
        }

        var billingAddress = await GetBillingAddressAsync(billToCompanyId, ct);
        var shipTo = await ResolveShipToAsync(ownerCompanyId, ownerCompanyName, defaults.ShippingAddress, defaults.DefaultShipToRaw, ct);
        var ownerDepartmentId = await GetContactDepartmentIdAsync(ownerContactId, ct);
        var billDepartmentId = ownerContactId == billContactId ? ownerDepartmentId : await GetContactDepartmentIdAsync(billContactId, ct);
        var requiredDate = ComputeRequiredDate(DateTime.Today);

        for (var i = 0; i < plannedJobs.Count; i++)
        {
            var attempt = i + 1;
            var planned = plannedJobs[i];

            var request = new CreateJobRequest
            {
                OwnerCompanyId = ownerCompanyId,
                OwnerCompanyName = ownerCompanyName,
                BillToCompanyId = billToCompanyId,
                BillToCompanyName = billToCompanyName,
                StandardLabourRate = defaults.StandardLabourRate,

                OwnerContactId = ownerContactId,
                OwnerContactName = ownerContactName,
                OwnerDepartmentId = ownerDepartmentId,
                BillContactId = billContactId,
                BillContactName = billContactName,
                BillDepartmentId = billDepartmentId,

                ShipTo = shipTo.Name,
                ShippingAddress = shipTo.Address,
                BillingAddress = billingAddress,

                BusinessTypeId = BusinessTypeId,
                UserId = currentUser.Value.Id,
                ContactUserId = currentUser.Value.Id,
                JobPriorityId = JobPriorityId,
                LabourBasis = defaults.LabourBasis,
                TravelBasis = defaults.TravelBasis,
                PartsBasis = defaults.PartsBasis,

                RequiredDate = requiredDate,
                LabourEstimate = LabourEstimateHours,
                Instructions = planned.Instructions
            };

            try
            {
                var res = await jms.CreateJobAsync(request, ct);
                var jobId = ExtractJobId(res.Url);

                if (!jobId.HasValue)
                {
                    _logger.LogWarning("Job {Attempt} of {Count}: no job id in the response ({StatusCode}, ended at {Url})",
                        attempt, plannedJobs.Count, res.StatusCode, res.Url);
                    results.Add(new BulkJobCreationResult(attempt, null, $"No job id in response ({res.StatusCode} {res.ReasonPhrase})."));
                    continue;
                }

                var equipmentResults = new List<EquipmentAddOutcome>();
                foreach (var equipment in planned.Equipment)
                {
                    try
                    {
                        var eqRes = await AddEquipmentAsync(jobId.Value, ownerCompanyId, equipment, ct);
                        equipmentResults.Add(new EquipmentAddOutcome(
                            equipment,
                            eqRes?.Success == true,
                            eqRes?.Success == true ? null : $"{eqRes?.StatusCode} {eqRes?.ReasonPhrase}"));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Adding equipment {EquipmentId} to job {JobId} failed", equipment.EquipmentId, jobId);
                        equipmentResults.Add(new EquipmentAddOutcome(equipment, false, ex.Message));
                    }
                }

                results.Add(new BulkJobCreationResult(attempt, jobId, null, request, equipmentResults));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Creating job {Attempt} of {Count} failed", attempt, plannedJobs.Count);
                results.Add(new BulkJobCreationResult(attempt, null, ex.Message));
            }
        }

        return results;
    }

    /// <summary>Serial-number search for the "add equipment" step, scoped to the job's owner company —
    /// works before any job exists, since it's keyed on the owner company, not a job id.</summary>
    public async Task<List<EquipmentSearchHit>> SearchEquipmentAsync(int ownerCompanyId, string term, CancellationToken ct = default)
    {
        var jms = await _jmsRead.GetClientAsync();
        if (jms == null) return new();

        var res = await jms.PostQueryForCsvAsync(JmsQueries.EquipmentSearchQuery(ownerCompanyId, term));
        if (!res.Success) return new();

        var hits = ParseCsv<EquipmentCsvRow>(res.Content)
            .Select(r => new EquipmentSearchHit(r.equipment_id, r.serial_no ?? "", r.model_name_no ?? "", r.barcode_no ?? ""))
            .ToList();
        return hits;
    }

    /// <summary>Adds an equipment unit to a job via AddJobEquipment.</summary>
    public async Task<HtmlResponse?> AddEquipmentAsync(int jobId, int ownerCompanyId, EquipmentSearchHit equipment, CancellationToken ct = default)
    {
        var jms = await _jmsSession.GetClientAsync();
        if (jms == null) return null;

        var res = await jms.AddJobEquipmentAsync(new AddJobEquipmentRequest
        {
            JobId = jobId,
            OwnerCompanyId = ownerCompanyId,
            EquipmentId = equipment.EquipmentId,
            SerialNo = equipment.SerialNo,
            BarcodeNo = equipment.BarcodeNo
        }, ct);

        if (!res.Success)
            _logger.LogWarning("Adding equipment {EquipmentId} to job {JobId} returned {StatusCode} {ReasonPhrase}",
                equipment.EquipmentId, jobId, res.StatusCode, res.ReasonPhrase);
        return res;
    }

    /// <summary>Downloads a job's sticker PDF (see JmsClient.GetJobStickerPdfAsync). Null if not
    /// authenticated or the response wasn't actually a PDF.</summary>
    public async Task<JobStickerFile?> GetJobStickerPdfAsync(int jobId, int copies = 1, CancellationToken ct = default)
    {
        var jms = await _jmsRead.GetClientAsync();
        if (jms == null) return null;

        var file = await jms.GetJobStickerPdfAsync(jobId, copies, ct);
        if (file == null) _logger.LogWarning("Sticker for job {JobId} didn't come back as a PDF", jobId);
        return file;
    }

    private static int? ExtractJobId(string? url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        var match = Regex.Match(url, @"[?&]id=(\d+)");
        return match.Success ? int.Parse(match.Groups[1].Value) : null;
    }

    private static IEnumerable<T> ParseCsv<T>(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return Enumerable.Empty<T>();
        using var reader = new StringReader(content);
        using var csv = new CsvReader(reader, WaitingQuoteReport.MakeCsvConfig());
        return csv.GetRecords<T>().ToList();
    }

    private class OwnerDefaultsCsvRow
    {
        public string? company_name { get; set; }
        public string? default_ship_to { get; set; }
        public string? shipping_address { get; set; }
        public decimal? standard_labour_rate { get; set; }
        public int? labour_pref { get; set; }
        public int? travel_pref { get; set; }
        public int? part_pref { get; set; }
    }

    private class CompanyAddressCsvRow
    {
        public int company_id { get; set; }
        public string? address { get; set; }
    }

    private class CompanyShippingCsvRow
    {
        public int company_id { get; set; }
        public string? company_name { get; set; }
        public string? shipping_address { get; set; }
    }

    private class ContactDepartmentCsvRow
    {
        public int company_people_id { get; set; }
        public int? company_department_id { get; set; }
    }

    private class EquipmentCsvRow
    {
        public int equipment_id { get; set; }
        public string? serial_no { get; set; }
        public string? model_name_no { get; set; }
        public string? barcode_no { get; set; }
    }
}
