using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WorkshopDashboard.Services.External.Jms;

namespace WorkshopDashboard.Tests.Fakes;

/// <summary>A service line the fake JMS received, and which user's session sent it.</summary>
public record FakeServiceLine(int JobId, int ServiceTypeId, string Description, string SentBy);

/// <summary>
/// A stand-in for JMS, running on a random localhost port for one test. It implements just
/// enough of JMS's behaviour for the app's JMS layer: session-cookie logins, the auth check's
/// redirect-to-login, the query endpoint, adding service lines and changing job status. Page
/// names are invented (see <see cref="CreateOptions"/>) — the real ones never go in the repo.
///
/// Tests steer it through its properties: change a password, expire every session, make
/// queries or writes fail or reply too late, or stop it entirely to simulate an outage.
/// </summary>
public sealed class FakeJms : IAsyncDisposable
{
    public const string ServiceAccountUsername = "svc";
    public const string ServiceAccountPassword = "svc-pw";

    private readonly WebApplication _app;
    private readonly ConcurrentDictionary<string, string> _passwords = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _sessions = new(); // session id -> username
    private readonly ConcurrentDictionary<string, int> _logins = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<FakeServiceLine> _serviceLines = new();
    private readonly ConcurrentQueue<string> _reportRequests = new();
    private readonly ConcurrentQueue<Dictionary<string, string>> _backgroundJobs = new();
    private readonly ConcurrentQueue<(int Id, string Title, string Message, string Link)> _notifications = new();
    private int _notificationCounter;
    private int _queryHits;
    private int _queryCounter;
    private int _statusChanges;

    public ConcurrentDictionary<int, int> JobStatuses { get; } = new();

    /// <summary>Queries answer 502 instead of a result.</summary>
    public bool FailQueries { get; set; }

    /// <summary>How long each ordinary query takes — long enough to overlap concurrent ones.</summary>
    public TimeSpan QueryDelay { get; set; } = TimeSpan.Zero;

    /// <summary>Writes are saved, but the reply only comes after this long — set it past the
    /// client's timeout to simulate "JMS applied it but we never heard back".</summary>
    public TimeSpan WriteReplyDelay { get; set; } = TimeSpan.Zero;

    /// <summary>Writes answer this status without saving anything (e.g. 503).</summary>
    public int? WriteFailureStatus { get; set; }

    public string BaseUrl => Options.BaseUrl;
    public JmsOptions Options { get; }

    public int QueryHits => _queryHits;

    /// <summary>How many status-change posts JMS has applied (including repeats of the same status).</summary>
    public int StatusChanges => _statusChanges;
    public IReadOnlyList<FakeServiceLine> ServiceLines => _serviceLines.ToList();
    public int LoginCount(string username) => _logins.GetValueOrDefault(username);

    /// <summary>Every monthly report request, as "page?query" (e.g. "job-progress?start_date=...").</summary>
    public IReadOnlyList<string> ReportRequests => _reportRequests.ToList();

    /// <summary>Report pages that answer an HTML error page instead of a PDF.</summary>
    public HashSet<string> BrokenReports { get; } = new();

    /// <summary>Every background job submitted, as its posted form fields.</summary>
    public IReadOnlyList<Dictionary<string, string>> BackgroundJobs => _backgroundJobs.ToList();

    /// <summary>How long a background job takes before its notification appears.</summary>
    public TimeSpan BackgroundJobDelay { get; set; } = TimeSpan.Zero;

    /// <summary>While true, every request is dropped without a reply (a temporary outage).</summary>
    public bool Unreachable { get; set; }

    /// <summary>Background jobs are accepted but never post a notification.</summary>
    public bool BackgroundJobsNeverFinish { get; set; }

    public void AddNotification(string title, string message, string link) =>
        _notifications.Enqueue((Interlocked.Increment(ref _notificationCounter), title, message, link));

    // Answers the two notification queries JmsQueries builds for Job Progress polling.
    private string AnswerNotificationQuery(string query)
    {
        var all = _notifications.ToList();
        if (query.Contains("MAX(user_notifications_id)"))
            return $"max_id\n{(all.Count == 0 ? 0 : all.Max(n => n.Id))}";

        var after = int.Parse(System.Text.RegularExpressions.Regex.Match(query, @"user_notifications_id > (\d+)").Groups[1].Value);
        var prefix = System.Text.RegularExpressions.Regex.Match(query, @"LIKE '(.*)%'").Groups[1].Value.Replace("''", "'");
        var hit = all.Where(n => n.Id > after && n.Title == "PDF Complete" && n.Message.StartsWith(prefix))
            .OrderByDescending(n => n.Id).FirstOrDefault();

        return "notification_link,notification_message\n" +
            (hit.Link == null ? "" : $"{hit.Link},\"{hit.Message.Replace("\"", "\"\"")}\"\n");
    }

    public void SetPassword(string username, string password) => _passwords[username] = password;
    public void ExpireAllSessions() => _sessions.Clear();

    private FakeJms(WebApplication app, JmsOptions options)
    {
        _app = app;
        Options = options;
        _passwords[ServiceAccountUsername] = ServiceAccountPassword;
    }

    /// <summary>Starts a fake on a free port. The client's request timeout is generous by
    /// default, so a cold or busy CI machine can't fail an ordinary test; tests about timeouts
    /// pass a short one so they don't wait it out.</summary>
    public static async Task<FakeJms> StartAsync(int requestTimeoutSeconds = 10)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();

        // Routes only need the page names, so they're mapped before the port is known.
        var fake = new FakeJms(app, CreateOptions("http://127.0.0.1/", requestTimeoutSeconds));
        fake.MapRoutes();
        await app.StartAsync();

        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        fake.Options.BaseUrl = address + "/";
        return fake;
    }

    public static JmsOptions CreateOptions(string baseUrl, int requestTimeoutSeconds = 10) => new()
    {
        BaseUrl = baseUrl,
        LoginLatitude = "0",
        LoginLongitude = "0",
        RequestTimeoutSeconds = requestTimeoutSeconds,
        ServiceAccount = new JmsServiceAccount { Username = ServiceAccountUsername, Password = ServiceAccountPassword },
        Endpoints = new JmsEndpoints
        {
            Login = "login",
            AuthCheck = "home",
            Query = "query",
            OpenFile = "open-file",
            UserSearch = "user-search",
            JobSticker = "sticker",
            PartPricingSearch = "part-pricing",
            NotYetReceivedItems = "not-received",
            WorkSummaryReport = "work-summary",
            NotSeenReport = "not-seen",
            ErfaReport = "erfa",
            DetailedInspectionReport = "detailed-inspection",
            OpenCertificate = "certificate",
            LabourConflictCheck = "labour-check",
            JobSummary = "job",
            CreateJob = "create-job",
            AddJobEquipment = "add-equipment",
            AddJobTimesheet = "add-timesheet",
            AddJobService = "add-service",
            UpdateJobService = "update-service",
            AddJobParts = "add-parts",
            CreateRecall = "create-recall",
            JobResourceUpload = "upload",
            CreateDeliveryDocket = "docket",
            BackgroundJobSubmission = "background-job",
            RsScheduleReport = "rs-schedule",
            JobRepairSummaryReport = "repair-summary",
            RsPerformanceReport = "rs-performance",
        }
    };

    /// <summary>Takes the fake offline — every request to it now fails to connect.</summary>
    public Task StopAsync() => _app.StopAsync();

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    // ---------------------------
    // Routes
    // ---------------------------

    private void MapRoutes()
    {
        var e = Options.Endpoints;

        // Unreachable drops every connection without answering — to the client that's an
        // outage (HttpRequestException with no status), and unlike StopAsync it can be undone.
        _app.Use(async (ctx, next) =>
        {
            if (Unreachable) { ctx.Abort(); return; }
            await next();
        });

        _app.MapGet("/", () => "fake jms");

        _app.MapPost("/" + e.Login, async (HttpContext ctx) =>
        {
            var sid = Session(ctx);
            var form = await ctx.Request.ReadFormAsync();
            var user = form["user_name"].ToString();
            if (_passwords.TryGetValue(user, out var real) && real == form["password"].ToString())
            {
                _sessions[sid] = user;
                _logins.AddOrUpdate(user, 1, (_, n) => n + 1);
                return Results.Redirect("/" + e.AuthCheck);
            }
            return Results.Content("login page", "text/html");
        });
        _app.MapGet("/" + e.Login, () => Results.Content("login page", "text/html"));

        _app.MapGet("/" + e.AuthCheck, (HttpContext ctx) =>
            UserOf(ctx) is { } user ? Results.Content($"welcome {user}", "text/html") : Results.Redirect("/" + e.Login));

        _app.MapPost("/" + e.Query, async (HttpContext ctx) =>
        {
            Interlocked.Increment(ref _queryHits);
            var query = (await ctx.Request.ReadFormAsync())["query"].ToString();

            if (query.Contains("COUNT(*) AS n"))
            {
                var start = query.IndexOf("description = '", StringComparison.Ordinal) + "description = '".Length;
                var description = query[start..query.IndexOf('\'', start)].Replace("''", "'");
                return Results.Content($"n\n{_serviceLines.Count(l => l.Description == description)}", "text/csv");
            }

            if (query.Contains("SELECT job_status_id FROM job WHERE job_id = "))
            {
                var jobId = int.Parse(query.Split('=').Last().Trim());
                return Results.Content($"job_status_id\n{JobStatuses.GetValueOrDefault(jobId)}", "text/csv");
            }

            if (query.Contains("FROM user_notification"))
                return Results.Content(AnswerNotificationQuery(query), "text/csv");

            if (QueryDelay > TimeSpan.Zero) await Task.Delay(QueryDelay);
            if (FailQueries) return Results.StatusCode(502);

            // A different value every time, so tests can tell a live answer from a cached one.
            return Results.Content($"value\n{Interlocked.Increment(ref _queryCounter)}", "text/csv");
        });

        _app.MapPost("/" + e.AddJobService, async (HttpContext ctx, int id) =>
        {
            if (UserOf(ctx) is not { } user) return Results.Redirect("/" + e.Login);
            if (WriteFailureStatus is { } status) return Results.StatusCode(status);

            var form = await ctx.Request.ReadFormAsync();
            var description = form["description"].ToString();
            if (description.Contains("REJECT")) return Results.StatusCode(400);

            _serviceLines.Enqueue(new FakeServiceLine(id, int.Parse(form["service_type"].ToString()), description, user));
            if (WriteReplyDelay > TimeSpan.Zero) await Task.Delay(WriteReplyDelay);
            return Results.Content("ok", "text/html");
        });

        _app.MapPost("/" + e.JobSummary, async (HttpContext ctx, int id) =>
        {
            if (UserOf(ctx) is null) return Results.Redirect("/" + e.Login);
            if (WriteFailureStatus is { } status) return Results.StatusCode(status);

            var form = await ctx.Request.ReadFormAsync();
            JobStatuses[id] = int.Parse(form["new_job_status"].ToString());
            Interlocked.Increment(ref _statusChanges);
            if (WriteReplyDelay > TimeSpan.Zero) await Task.Delay(WriteReplyDelay);
            return Results.Content("ok", "text/html");
        });

        // Background jobs: only Job Progress is modelled. It "finishes" after BackgroundJobDelay
        // by posting a PDF Complete notification whose link serves the file.
        _app.MapPost("/" + e.BackgroundJobSubmission, async (HttpContext ctx) =>
        {
            if (UserOf(ctx) is null) return Results.Redirect("/" + e.Login);
            var form = await ctx.Request.ReadFormAsync();
            var fields = form.ToDictionary(f => f.Key, f => f.Value.ToString());
            _backgroundJobs.Enqueue(fields);

            if (fields.GetValueOrDefault("job_type") == "report_job_progress_cli" && !BackgroundJobsNeverFinish)
            {
                var company = fields.GetValueOrDefault("company_name");
                _ = Task.Delay(BackgroundJobDelay).ContinueWith(_ => AddNotification(
                    "PDF Complete",
                    $"Job Progress Report for {company} has been completed, Please check your notification to download.",
                    "bg-files/job-progress.pdf"));
            }
            return Results.Content("Submitted", "text/html");
        });

        _app.MapGet("/bg-files/{name}", (HttpContext ctx, string name) =>
            UserOf(ctx) is null ? Results.Redirect("/" + e.Login) : Results.Bytes(OnePagePdf(Path.GetFileNameWithoutExtension(name)), "application/pdf"));

        foreach (var page in new[] { e.RsScheduleReport, e.JobRepairSummaryReport, e.RsPerformanceReport })
        {
            _app.MapGet("/" + page, (HttpContext ctx) =>
            {
                if (UserOf(ctx) is null) return Results.Redirect("/" + e.Login);
                _reportRequests.Enqueue(page + ctx.Request.QueryString);
                return BrokenReports.Contains(page)
                    ? Results.Content("<html><body><h1>Report error</h1><p>No data for company</p></body></html>", "text/html")
                    : Results.Bytes(OnePagePdf(page), "application/pdf");
            });
        }
    }

    /// <summary>A one-page PDF with <paramref name="title"/> on it.</summary>
    public static byte[] OnePagePdf(string title)
    {
        using var doc = new PdfSharpCore.Pdf.PdfDocument();
        var page = doc.AddPage();
        using (var gfx = PdfSharpCore.Drawing.XGraphics.FromPdfPage(page))
            gfx.DrawString(title, new PdfSharpCore.Drawing.XFont("Arial", 20), PdfSharpCore.Drawing.XBrushes.Black, 50, 100);
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    private string Session(HttpContext ctx)
    {
        if (ctx.Request.Cookies.TryGetValue("PHPSESSID", out var id)) return id;
        id = Guid.NewGuid().ToString("N");
        ctx.Response.Cookies.Append("PHPSESSID", id, new CookieOptions { Path = "/" });
        return id;
    }

    private string? UserOf(HttpContext ctx) =>
        ctx.Request.Cookies.TryGetValue("PHPSESSID", out var id) && _sessions.TryGetValue(id, out var user) ? user : null;
}
