using WorkshopDashboard.Models;

using WorkshopDashboard.Services.External.Jms;
namespace WorkshopDashboard.Services.Jobs;

/// <summary>
/// Thin wrappers over the typed job-status/freight writes, shared by BulkStatusUpdate.razor
/// and InternationalShipping.razor's booking flow. All go through JmsUserSession.WriteAsync,
/// so while JMS is down they're queued rather than lost (a queued write reads as success).
/// </summary>
public class JobStatusActionService
{
    private readonly JmsUserSession _jmsSession;

    public JobStatusActionService(JmsUserSession jmsSession) => _jmsSession = jmsSession;

    public async Task<bool> ChangeJobStatusAsync(int jobId, JobStatus newStatus, string notes = "") =>
        (await _jmsSession.WriteAsync(new ChangeJobStatusWrite(jobId, newStatus, notes))).Accepted;

    public async Task<HtmlResponse?> AddFreightServiceAsync(int jobId, string description, decimal costPrice) =>
        (await _jmsSession.WriteAsync(JmsWrite.AddFreight(jobId, description, costPrice))).ToResponse();

    /// <summary>An informational, non-chargeable service line — used for manually-entered
    /// inbound tracking info (WorkshopFinalisationComplete), where JMS isn't billing the
    /// freight itself, unlike AddFreightServiceAsync's outbound-shipment charge.</summary>
    public async Task<HtmlResponse?> AddNoteServiceAsync(int jobId, string description) =>
        (await _jmsSession.WriteAsync(JmsWrite.AddNote(jobId, description))).ToResponse();

    /// <summary>Removes a service line by id — used to undo a manually-entered tracking
    /// note (WorkshopFinalisationComplete) that was typed in wrong.</summary>
    public async Task<HtmlResponse?> DeleteJobServiceAsync(int jobId, int serviceId) =>
        (await _jmsSession.WriteAsync(new DeleteJobServiceWrite(jobId, serviceId))).ToResponse();
}
