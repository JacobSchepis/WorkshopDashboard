using WorkshopDashboard.Models;

using WorkshopDashboard.Services.External.Jms;
namespace WorkshopDashboard.Services.Jobs;

/// <summary>
/// Data access for UploadDocument.razor — posting the resource upload and the follow-up
/// status changes. The page still builds the MultipartFormDataContent itself (the field
/// set is page-specific presentation, not a reusable data shape).
/// </summary>
public class DocumentUploadService
{
    private readonly JmsUserSession _jmsSession;

    public DocumentUploadService(JmsUserSession jmsSession) => _jmsSession = jmsSession;

    public async Task<HtmlResponse> UploadResourceAsync(int jobId, HttpContent content)
    {
        var jms = await _jmsSession.GetClientAsync();
        if (jms == null)
            return new HtmlResponse(false, 0, "JMS session unavailable.", "", null, null, null, new Dictionary<string, string>());

        return await jms.UploadJobResourceAsync(jobId, content);
    }

    public async Task ChangeJobStatusAsync(int jobId, JobStatus status, string note) =>
        await _jmsSession.WriteAsync(new ChangeJobStatusWrite(jobId, status, note));
}
