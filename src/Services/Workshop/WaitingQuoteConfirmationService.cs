using CsvHelper;
using CsvHelper.Configuration;
using System.Globalization;

using WorkshopDashboard.Abstract;
using WorkshopDashboard.Services.External.Jms;
using WorkshopDashboard.Services.Jobs;
using WorkshopDashboard.Services.Stores;
namespace WorkshopDashboard.Services.Workshop;

public record WqcQuoteInfo(string QuoteId, decimal Price, DateTime QuoteDate)
{
    /// <summary>Price with the flat 10% GST component backed out.</summary>
    public decimal PriceExGst => Math.Round(Price / 1.1m, 2, MidpointRounding.AwayFromZero);
}

public class WqcJobRecord
{
    public string JobId { get; set; } = "";
    public string Owner { get; set; } = "";
    public int OwnerCompanyId { get; set; }
    public string Equipment { get; set; } = "";
    public string Fault { get; set; } = "";
    public DateTime FirstSetDate { get; set; }
    public DateTime? LastWorked { get; set; }
    public DateTime? FollowUp { get; set; }
    public int? FollowUpServiceId { get; set; }
    public List<WqcQuoteInfo> Quotes { get; set; } = new();
}

/// <summary>
/// Data access for WaitingQuoteConfirmation.razor — loading the queue and saving the
/// "Follow Up" note. Mark-worked shares JobTouchService with WaitingWorkshopProcessing;
/// everything else here (the query, the quotes_raw/followup_raw parsing) is specific to
/// this one page's queue, so it stays its own service rather than a generic one.
/// </summary>
public class WaitingQuoteConfirmationService
{
    private readonly JmsReadSession _jmsRead;
    private readonly JmsUserSession _jmsSession;
    private readonly CurrentUserResolver _currentUser;

    public WaitingQuoteConfirmationService(JmsReadSession jmsRead, JmsUserSession jmsSession, CurrentUserResolver currentUser)
    {
        _jmsRead = jmsRead;
        _jmsSession = jmsSession;
        _currentUser = currentUser;
    }

    public async Task<List<WqcJobRecord>> GetJobsAsync(ScopeJobExclusions exclusions)
    {
        var jms = await _jmsRead.GetClientAsync();
        return jms == null ? new() : await GetJobsAsync(jms, exclusions);
    }

    // Static overload for callers that already hold their own IJmsReader — RuleCheckService,
    // which runs from the kiosk WorkshopNotifications page (see DashboardLookup's static
    // overloads for the same reasoning).
    public static async Task<List<WqcJobRecord>> GetJobsAsync(IJmsReader jms, ScopeJobExclusions exclusions)
    {
        var query = JmsQueries.WaitingQuoteConfirmationQuery(exclusions);
        var res = await jms.PostQueryForCsvAsync(query);
        if (!res.Success) return new();

        using var reader = new StringReader(res.Content);
        using var csv = new CsvReader(reader, CsvConfig());

        return csv.GetRecords<JobCsvRow>()
            .Select(r =>
            {
                var followUp = ParseFollowUp(r.followup_raw);
                return new WqcJobRecord
                {
                    JobId = r.job_id.ToString(),
                    Owner = r.owner ?? "",
                    OwnerCompanyId = r.owner_company_id ?? 0,
                    Equipment = r.equipment ?? "",
                    Fault = r.fault ?? "",
                    FirstSetDate = DateTime.TryParse(r.first_set_date, out var fd) ? fd : DateTime.MinValue,
                    LastWorked = DateTime.TryParse(r.last_worked_date, out var lw) ? lw : (DateTime?)null,
                    FollowUp = followUp,
                    FollowUpServiceId = r.followup_service_id,
                    Quotes = ParseQuotes(r.quotes_raw)
                };
            })
            .ToList();
    }

    /// <summary>Writes/updates the "Follow Up - yyyy-MM-dd" service_type_id = 11 note.
    /// Returns false (no write attempted) if the current user can't be resolved.</summary>
    public async Task<bool> SaveFollowUpAsync(int jobId, int? followUpServiceId, DateTime followUp, Action<string>? logWarning = null)
    {
        var user = await _currentUser.ResolveAsync();
        if (user is null)
        {
            logWarning?.Invoke($"Could not resolve current user; follow-up not saved for job {jobId}.");
            return false;
        }

        var description = $"Follow Up - {followUp:yyyy-MM-dd}";

        var res = followUpServiceId.HasValue
            ? await _jmsSession.WriteAsync(new UpdateJobServiceWrite(jobId, followUpServiceId.Value, new JobServiceUpdateRequest
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
            logWarning?.Invoke($"follow-up save failed for job {jobId}: {res.ToResponse().StatusCode} {res.ToResponse().ReasonPhrase}");

        return res.Accepted;
    }

    private static DateTime? ParseFollowUp(string? raw)
    {
        const string marker = "Follow Up - ";
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var i = raw.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        var datePart = raw[(i + marker.Length)..].Trim();
        return DateTime.TryParse(datePart, out var d) ? d : null;
    }

    /// <summary>Parses the "id|amount|yyyy-MM-dd;;id|amount|yyyy-MM-dd" list built by the
    /// quotes_raw subquery — one entry per unaccepted quote on the job.</summary>
    private static List<WqcQuoteInfo> ParseQuotes(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new();

        var quotes = new List<WqcQuoteInfo>();
        foreach (var entry in raw.Split(";;", StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = entry.Split('|');
            if (parts.Length != 3) continue;
            if (!decimal.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var price)) continue;
            var date = DateTime.TryParse(parts[2], out var d) ? d : DateTime.MinValue;
            quotes.Add(new WqcQuoteInfo(parts[0], price, date));
        }
        return quotes;
    }

    private static CsvConfiguration CsvConfig() => WaitingQuoteReport.MakeCsvConfig();

    private class JobCsvRow
    {
        public int job_id { get; set; }
        public string? owner { get; set; }
        public int? owner_company_id { get; set; }
        public string? fault { get; set; }
        public string? job_status { get; set; }
        public string? equipment { get; set; }
        public string? last_worked_date { get; set; }
        public string? first_set_date { get; set; }
        public string? followup_raw { get; set; }
        public int? followup_service_id { get; set; }
        public string? quotes_raw { get; set; }
    }
}
