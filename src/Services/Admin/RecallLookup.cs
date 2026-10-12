using WorkshopDashboard.Models;

using WorkshopDashboard.Services.External.Jms;
namespace WorkshopDashboard.Services.Admin;

/// <summary>JMS data access for BulkCreateRecall.razor's duplicate check and submission.</summary>
public class RecallLookup
{
    private readonly JmsReadSession _jmsRead;
    private readonly JmsUserSession _jmsSession;

    public RecallLookup(JmsReadSession jmsRead, JmsUserSession jmsSession)
    {
        _jmsRead = jmsRead;
        _jmsSession = jmsSession;
    }

    public async Task<string?> GetExistingReferencesCsvAsync(IEnumerable<string> refs)
    {
        var jms = await _jmsRead.GetClientAsync();
        if (jms == null) return null;

        var res = await jms.PostQueryForCsvAsync(JmsQueries.ExistingRecallReferencesQuery(refs));
        return res.Content;
    }

    public async Task<HtmlResponse?> SubmitRecallAsync(HttpContent content)
    {
        var jms = await _jmsSession.GetClientAsync();
        return jms == null ? null : await jms.CreateRecallAsync(content);
    }
}
