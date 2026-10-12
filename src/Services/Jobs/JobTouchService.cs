using CsvHelper;

using WorkshopDashboard.Services.External.Jms;
using WorkshopDashboard.Services.Workshop;
namespace WorkshopDashboard.Services.Jobs;

// ═══════════════════════════════════════════════════════════════════════════
//  JobTouchService
//
//  Bumps job.last_worked_date without leaving a permanent record. Updating an
//  existing service row does NOT touch last_worked_date on JMS's side — only
//  creating one does — so this creates a throwaway NOTE (service_type_id = 11)
//  service, looks up the id it was just given, then deletes it straight away
//  via AddJobService's del= param. Previously duplicated verbatim in
//  WaitingQuoteConfirmation.razor and WaitingWorkshopProcessing.razor.
// ═══════════════════════════════════════════════════════════════════════════

public class JobTouchService
{
    private readonly JmsUserSession _jmsSession;
    private readonly CurrentUserResolver _currentUser;

    public JobTouchService(JmsUserSession jmsSession, CurrentUserResolver currentUser)
    {
        _jmsSession = jmsSession;
        _currentUser = currentUser;
    }

    public async Task<bool> TouchLastWorkedAsync(int jobId, Action<string>? logWarning = null)
    {
        var jms = await _jmsSession.GetClientAsync();
        if (jms == null) return false;

        var user = await _currentUser.ResolveAsync();
        if (user is null)
        {
            logWarning?.Invoke($"Could not resolve current user; last-worked touch not sent for job {jobId}.");
            return false;
        }

        var createRes = await jms.AddJobServiceAsync(jobId, new JobServiceRequest
        {
            ServiceTypeId = 11,
            Description = "Marked worked",
            UserId = user.Value.Id,
            ItemHide = true // hide internal note from customer-facing views
        });
        if (!createRes.Success)
        {
            logWarning?.Invoke($"last-worked touch (create) failed for job {jobId}: {createRes.StatusCode} {createRes.ReasonPhrase}");
            return false;
        }

        var serviceId = await FindLatestNoteServiceIdAsync(jms, jobId);
        if (serviceId is null)
        {
            logWarning?.Invoke($"Could not find the throwaway service just created for job {jobId}; leaving it in place.");
            return true;
        }

        var deleteRes = await jms.DeleteJobServiceAsync(jobId, serviceId.Value);
        if (!deleteRes.Success)
            logWarning?.Invoke($"last-worked touch (delete) failed for job {jobId}, service {serviceId}: {deleteRes.StatusCode} {deleteRes.ReasonPhrase}");

        return true;
    }

    private static async Task<int?> FindLatestNoteServiceIdAsync(WorkshopDashboard.Abstract.IJmsReader jms, int jobId)
    {
        var query = JmsQueries.LatestServiceIdQuery(jobId, serviceTypeId: 11);
        var res = await jms.PostQueryForCsvAsync(query);
        if (!res.Success) return null;

        using var reader = new StringReader(res.Content);
        using var csv = new CsvReader(reader, WaitingQuoteReport.MakeCsvConfig());
        return csv.GetRecords<ServiceIdRow>().Select(r => (int?)r.service_id).FirstOrDefault();
    }

    private class ServiceIdRow
    {
        public int service_id { get; set; }
    }
}
