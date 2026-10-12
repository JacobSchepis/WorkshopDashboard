using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;

using WorkshopDashboard.Abstract;
using WorkshopDashboard.Services.External.Jms;
using WorkshopDashboard.Services.Workshop;
namespace WorkshopDashboard.Services.Jobs;

public record JobNote(int ServiceId, string Text, DateTime CreatedAt, string? AuthorName);

// ═══════════════════════════════════════════════════════════════════════════
//  JobNotesService
//
//  The tracking notes on a job (Overview's highlighted jobs, and the workshop
//  notifications kiosk), built on the existing services table rather than a
//  new one — a note is a plain-text $0 service (service_type_id = 11, NOTE —
//  see JmsClient.Services.cs). Plenty of other NOTE rows exist on a job (typed
//  straight into JMS, ETA rows, the model watchlist's marker), so the app's
//  own notes are stored with a "[Tracking] " prefix: only those are read back,
//  and the prefix is stripped before display. In JMS itself the prefix stays
//  visible, which also shows where the line came from. Notes written before
//  the prefix existed are deliberately left behind (still in JMS, not shown).
//
//  Reusable from any page: inject JobNotesService, call
//  AddNoteAsync/UpdateNoteAsync/DeleteNoteAsync/GetNotesAsync.
// ═══════════════════════════════════════════════════════════════════════════

public class JobNotesService
{
    /// <summary>Marks a NOTE line as one of this app's tracking notes.</summary>
    public const string TrackingPrefix = "[Tracking] ";

    // Matches the type table comment in JmsClient.Services.cs — kept as a literal here
    // (same convention WaitingWorkshopProcessing.razor already uses for its own type-11
    // rows) since JmsClient's ServiceTypeNote constant is private to that file.
    private const int ServiceTypeNote = 11;

    private readonly CurrentUserResolver _currentUser;
    private readonly JmsReadSession _jmsRead;
    private readonly JmsUserSession _jmsSession;

    public JobNotesService(CurrentUserResolver currentUser, JmsReadSession jmsRead, JmsUserSession jmsSession)
    {
        _currentUser = currentUser;
        _jmsRead = jmsRead;
        _jmsSession = jmsSession;
    }

    public async Task<bool> AddNoteAsync(int jobId, string text) =>
        (await _jmsSession.WriteAsync(JmsWrite.AddNote(jobId, Tag(text)))).Accepted;

    /// <summary>Adds a NOTE line exactly as given, without the tracking prefix — for markers
    /// other features read back themselves (e.g. the model watchlist's "Ignored" note),
    /// which mustn't show up as tracking notes.</summary>
    public async Task<bool> AddUntaggedNoteAsync(int jobId, string text) =>
        (await _jmsSession.WriteAsync(JmsWrite.AddNote(jobId, text.Trim()))).Accepted;

    public Task<bool> UpdateNoteAsync(int jobId, JobNote note, string newText) =>
        UpdateNoteAsync(jobId, note.ServiceId, newText);

    public async Task<bool> UpdateNoteAsync(int jobId, int serviceId, string newText)
    {
        var user = await _currentUser.ResolveAsync();
        if (user == null) return false;

        var res = await _jmsSession.WriteAsync(new UpdateJobServiceWrite(jobId, serviceId, new JobServiceUpdateRequest
        {
            ServiceTypeId = ServiceTypeNote,
            Description = Tag(newText),
            UserId = user.Value.Id,
            TechnicianName = user.Value.Name,
        }));

        return res.Accepted;
    }

    public Task<bool> DeleteNoteAsync(int jobId, JobNote note) =>
        DeleteNoteAsync(jobId, note.ServiceId);

    public async Task<bool> DeleteNoteAsync(int jobId, int serviceId) =>
        (await _jmsSession.WriteAsync(new DeleteJobServiceWrite(jobId, serviceId))).Accepted;

    /// <summary>A job's tracking notes, newest first.</summary>
    public async Task<List<JobNote>> GetNotesAsync(int jobId)
    {
        var jms = await _jmsRead.GetClientAsync();
        if (jms == null) return new();

        var csv = await jms.PostQueryForCsvAsync(JmsQueries.JobNotesQuery(new[] { jobId }));
        return csv.Success ? ReadNotes(csv.Content).Select(r => r.Note).ToList() : new();
    }

    /// <summary>Each job's newest tracking note, for jobs that have one. Static for the kiosk
    /// WorkshopNotifications page, which has its own IJmsReader.</summary>
    public static async Task<Dictionary<int, JobNote>> GetLatestNotesAsync(IJmsReader jms, IReadOnlyCollection<int> jobIds)
    {
        if (jobIds.Count == 0) return new();

        var csv = await jms.PostQueryForCsvAsync(JmsQueries.JobNotesQuery(jobIds));
        if (!csv.Success) return new();

        // Rows come newest first, so the first per job is its latest.
        return ReadNotes(csv.Content)
            .GroupBy(r => r.JobId)
            .ToDictionary(g => g.Key, g => g.First().Note);
    }

    internal static string Tag(string text) => TrackingPrefix + text.Trim();

    internal static string Untag(string? description)
    {
        var text = (description ?? "").Trim();
        return text.StartsWith(TrackingPrefix.Trim(), StringComparison.Ordinal)
            ? text[TrackingPrefix.Trim().Length..].Trim()
            : text;
    }

    private static List<(int JobId, JobNote Note)> ReadNotes(string content)
    {
        using var reader = new StringReader(content);
        using var csvReader = new CsvReader(reader, CsvConfig());

        return csvReader.GetRecords<NoteRow>()
            .Select(row => (row.job_id, ToJobNote(row)))
            .ToList();
    }

    private static JobNote ToJobNote(NoteRow row)
    {
        var author = string.Join(" ", new[] { row.first_name, row.last_name }.Where(s => !string.IsNullOrWhiteSpace(s)));

        return new JobNote(
            row.service_id,
            Untag(row.description),
            row.creation_date,
            string.IsNullOrWhiteSpace(author) ? null : author);
    }

    private static CsvConfiguration CsvConfig() => WaitingQuoteReport.MakeCsvConfig();

    private class NoteRow
    {
        public int job_id { get; set; }
        public int service_id { get; set; }
        public string? description { get; set; }
        public DateTime creation_date { get; set; }
        public string? first_name { get; set; }
        public string? last_name { get; set; }
    }
}
