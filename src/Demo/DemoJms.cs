using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using WorkshopDashboard.Services.External.Jms;

namespace WorkshopDashboard.Demo;

// ═══════════════════════════════════════════════════════════════════════════
//  DemoJms
//
//  Demo mode's JMS: answers every request JmsClient makes, in process, the way
//  the real site would — session cookies from the login form, a redirect to the
//  login page for an expired session, CSV from the query page, the job pages'
//  form posts applied to DemoJmsDatabase, PDFs from the document pages. The
//  rest of the JMS layer (JmsReadSession, JmsUserSessionPool, the write queue,
//  every lookup service) runs exactly as it does against the real thing.
//
//  Logins: "demo" / "demo" for people, plus the service account in Options.
// ═══════════════════════════════════════════════════════════════════════════

public sealed class DemoJms
{
    public const string DemoUsername = "demo";
    public const string DemoPassword = "demo";
    private const string ServiceAccountUsername = "demo-service";

    /// <summary>JMS settings for demo mode: invented page names, no secrets. Links for people
    /// go to the app's own /demo-jms pages (see DemoJmsPages).</summary>
    public static JmsOptions Options { get; } = new()
    {
        BaseUrl = "https://jms.demo.invalid/",
        LinkBaseUrl = "/demo-jms",
        LoginLatitude = "0",
        LoginLongitude = "0",
        ServiceAccount = new JmsServiceAccount { Username = ServiceAccountUsername, Password = DemoPassword },
        Endpoints = DemoEndpoints(),
    };

    private static JmsEndpoints DemoEndpoints()
    {
        var endpoints = new JmsEndpoints();
        foreach (var p in typeof(JmsEndpoints).GetProperties().Where(p => p.PropertyType == typeof(string)))
            p.SetValue(endpoints, p.Name.ToLowerInvariant());
        return endpoints;
    }

    private static readonly Dictionary<string, string> EndpointByPath =
        typeof(JmsEndpoints).GetProperties().ToDictionary(p => p.Name.ToLowerInvariant(), p => p.Name);

    private readonly ConcurrentDictionary<string, int> _sessions = new(); // session id → JMS user id

    private readonly ILogger<DemoJms> _logger;

    public DemoJms(DemoJmsDatabase database, ILogger<DemoJms> logger)
    {
        Database = database;
        _logger = logger;
    }

    public DemoJmsDatabase Database { get; }

    public JmsTransport Transport => new(jar => new Handler(this, jar));

    // ── One handler per JmsClient, holding that client's cookie jar ─────────

    private sealed class Handler(DemoJms jms, CookieContainer jar) : HttpMessageHandler
    {
        private static readonly Uri BaseUri = Options.BaseUri;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath.Trim('/').ToLowerInvariant();
            var query = QueryHelpers.ParseQuery(request.RequestUri.Query)
                .ToDictionary(q => q.Key, q => q.Value.ToString(), StringComparer.OrdinalIgnoreCase);
            var form = await ReadFormAsync(request.Content);

            if (path.StartsWith("demo-files/"))
                return Pdf(request, "Report", $"File: {Path.GetFileName(path)}");

            EndpointByPath.TryGetValue(path, out var endpoint);
            if (endpoint == nameof(JmsEndpoints.Login))
                return Login(request, form);

            var sessionId = jar.GetCookies(BaseUri)["PHPSESSID"]?.Value;
            if (sessionId is null || !jms._sessions.TryGetValue(sessionId, out var userId))
                return Html(new HttpRequestMessage(HttpMethod.Get, new Uri(BaseUri, Options.Endpoints.Login)), "login page");

            return endpoint switch
            {
                nameof(JmsEndpoints.AuthCheck) => Html(request, "<html>dashboard</html>"),
                nameof(JmsEndpoints.Query) => await QueryAsync(request, form),
                nameof(JmsEndpoints.UserSearch) => await UserSearchAsync(request, form),
                nameof(JmsEndpoints.LabourConflictCheck) => Json(request, new { status = "success", time = "08:30" }),
                nameof(JmsEndpoints.PartPricingSearch) => Json(request, new { ordered = new { buy_price = "120.00", list_price = "185.00" } }),
                nameof(JmsEndpoints.NotYetReceivedItems) => Html(request, "<div class=\"nyr\">No outstanding items.</div>"),

                nameof(JmsEndpoints.OpenFile) => Pdf(request, "Quote", $"Document: {form.GetValueOrDefault("filename")}"),
                nameof(JmsEndpoints.JobSticker) => Pdf(request, $"Job {query.GetValueOrDefault("title")}", "Job sticker", fileName: $"job-{query.GetValueOrDefault("title")}.pdf"),
                nameof(JmsEndpoints.OpenCertificate) => Pdf(request, "Certificate", $"File: {query.GetValueOrDefault("file")}"),
                nameof(JmsEndpoints.DetailedInspectionReport) => Pdf(request, "Detailed Inspection Report", $"Inspection {query.GetValueOrDefault("inspection_ids")}"),
                nameof(JmsEndpoints.WorkSummaryReport) or nameof(JmsEndpoints.NotSeenReport) or nameof(JmsEndpoints.ErfaReport)
                    => Pdf(request, endpoint, $"Job {form.GetValueOrDefault("job_id")}"),
                nameof(JmsEndpoints.RsScheduleReport) or nameof(JmsEndpoints.JobRepairSummaryReport) or nameof(JmsEndpoints.RsPerformanceReport)
                    => Pdf(request, endpoint, $"Company {query.GetValueOrDefault("cid")}{query.GetValueOrDefault("company_id")}"),

                nameof(JmsEndpoints.JobSummary) => await ChangeStatusAsync(request, query, form, userId),
                nameof(JmsEndpoints.CreateJob) => await CreateJobAsync(request, form, userId),
                nameof(JmsEndpoints.AddJobEquipment) => await AddEquipmentAsync(request, form),
                nameof(JmsEndpoints.AddJobTimesheet) => await AddTimesheetAsync(request, query, form),
                nameof(JmsEndpoints.AddJobService) => await AddOrDeleteServiceAsync(request, query, form, userId),
                nameof(JmsEndpoints.UpdateJobService) => await UpdateServiceAsync(request, query, form),
                nameof(JmsEndpoints.JobResourceUpload) => await UploadResourceAsync(request, query, form, userId),
                nameof(JmsEndpoints.CreateRecall) => await CreateRecallAsync(request, form, userId),
                nameof(JmsEndpoints.BackgroundJobSubmission) => await BackgroundJobAsync(request, form, userId),
                nameof(JmsEndpoints.AddJobParts) or nameof(JmsEndpoints.CreateDeliveryDocket) => Html(request, "saved"),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request, Content = new StringContent("not found") },
            };
        }

        private HttpResponseMessage Login(HttpRequestMessage request, Dictionary<string, string> form)
        {
            var user = form.GetValueOrDefault("user_name")?.Trim() ?? "";
            var userId = user.ToLowerInvariant() switch
            {
                DemoUsername when form.GetValueOrDefault("password") == DemoPassword => DemoSeeder.DemoUserId,
                ServiceAccountUsername when form.GetValueOrDefault("password") == DemoPassword => DemoSeeder.AutomationUserId,
                _ => 0,
            };
            if (userId == 0) return Html(request, "login page");

            var sessionId = Guid.NewGuid().ToString("N");
            jms._sessions[sessionId] = userId;
            jar.Add(BaseUri, new Cookie("PHPSESSID", sessionId) { Path = "/" });
            return Html(new HttpRequestMessage(HttpMethod.Get, new Uri(BaseUri, Options.Endpoints.AuthCheck)), "<html>dashboard</html>");
        }

        private async Task<HttpResponseMessage> QueryAsync(HttpRequestMessage request, Dictionary<string, string> form)
        {
            try
            {
                var csv = await jms.Database.QueryCsvAsync(form.GetValueOrDefault("query") ?? "");
                return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent(csv, Encoding.UTF8, "text/csv") };
            }
            catch (Exception ex)
            {
                jms._logger.LogWarning("Demo query failed: {Error}", ex.Message);
                return new HttpResponseMessage(HttpStatusCode.InternalServerError) { RequestMessage = request, Content = new StringContent(ex.Message) };
            }
        }

        private async Task<HttpResponseMessage> UserSearchAsync(HttpRequestMessage request, Dictionary<string, string> form)
        {
            var words = (form.GetValueOrDefault("q") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) return Json(request, Array.Empty<object>());

            var where = string.Join(" AND ", words.Select((w, i) => $"(first_name LIKE @w{i} OR last_name LIKE @w{i})"));
            var csv = await jms.Database.QueryCsvAsync(
                $"SELECT user_id, first_name, last_name FROM user WHERE {Inline(where, words)} ORDER BY user_id LIMIT 20");
            var users = csv.Trim().Split('\n').Skip(1).Where(l => l.Length > 0).Select(l => l.Split(','))
                .Select(c => new { user_id = int.Parse(c[0], CultureInfo.InvariantCulture), first_name = c[1], last_name = c[2] });
            return Json(request, users);
        }

        // QueryCsvAsync takes no parameters (it runs the app's own already-built SQL), so the
        // search words are inlined as escaped literals.
        private static string Inline(string where, string[] words)
        {
            for (var i = words.Length - 1; i >= 0; i--)
                where = where.Replace($"@w{i}", $"'{words[i].Replace("'", "''")}%'");
            return where;
        }

        // ── Writes ──

        private async Task<HttpResponseMessage> ChangeStatusAsync(HttpRequestMessage request, Dictionary<string, string> query,
            Dictionary<string, string> form, int userId)
        {
            var jobId = Int(query.GetValueOrDefault("id"));
            if (request.Method == HttpMethod.Post && Int(form.GetValueOrDefault("new_job_status")) is var status and > 0)
            {
                var now = DemoSeeder.Stamp(DateTime.Now);
                await jms.Database.ExecuteAsync(
                    "UPDATE job SET job_status_id = @s, completed_date = CASE WHEN @s = 2 THEN @now ELSE completed_date END WHERE job_id = @j",
                    ("@s", status), ("@now", now), ("@j", jobId));
                await jms.Database.ExecuteAsync(
                    "INSERT INTO job_status_history (job_id, job_status_id, creation_date, user_id, notes) VALUES (@j, @s, @now, @u, @n)",
                    ("@j", jobId), ("@s", status), ("@now", now), ("@u", userId), ("@n", form.GetValueOrDefault("notes") ?? ""));
            }
            return Html(request, $"<html>job {jobId}</html>");
        }

        private async Task<HttpResponseMessage> CreateJobAsync(HttpRequestMessage request, Dictionary<string, string> form, int userId)
        {
            var jobId = await jms.Database.ExecuteAsync("SELECT COALESCE(MAX(job_id), 0) + 1 FROM job");
            var now = DemoSeeder.Stamp(DateTime.Now);
            await jms.Database.ExecuteAsync(
                @"INSERT INTO job (job_id, job_no, owner_company_id, job_status_id, user_id, contact_name, notes, creation_date, start_date, job_priority_id, order_number)
                  VALUES (@j, @j, @c, 4, @u, @contact, @notes, @now, @now, @p, '')",
                ("@j", jobId), ("@c", Int(form.GetValueOrDefault("company_id"))), ("@u", userId),
                ("@contact", form.GetValueOrDefault("owner_contact_id")), ("@notes", form.GetValueOrDefault("instructions_and_fault_details")),
                ("@now", now), ("@p", Int(form.GetValueOrDefault("job_priority_id"))));
            await jms.Database.ExecuteAsync(
                "INSERT INTO job_status_history (job_id, job_status_id, creation_date, user_id, notes) VALUES (@j, 4, @now, @u, '')",
                ("@j", jobId), ("@now", now), ("@u", userId));

            var created = new HttpRequestMessage(HttpMethod.Get, new Uri(BaseUri, $"{Options.Endpoints.JobSummary}?id={jobId}"));
            return Html(created, $"<html>job {jobId}</html>");
        }

        private async Task<HttpResponseMessage> AddEquipmentAsync(HttpRequestMessage request, Dictionary<string, string> form)
        {
            await jms.Database.ExecuteAsync(
                "INSERT INTO job_equipment (job_id, equipment_id, creation_date, active_status) VALUES (@j, @e, @now, 't')",
                ("@j", Int(form.GetValueOrDefault("job_id"))), ("@e", Int(form.GetValueOrDefault("equipment_id_add"))), ("@now", DemoSeeder.Stamp(DateTime.Now)));
            return Html(request, "saved");
        }

        private async Task<HttpResponseMessage> AddTimesheetAsync(HttpRequestMessage request, Dictionary<string, string> query,
            Dictionary<string, string> form)
        {
            var date = DateTime.TryParseExact(form.GetValueOrDefault("labour_date"), "dd-MM-yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : DateTime.Today;
            var start = date + (TimeSpan.TryParse(form.GetValueOrDefault("labour_start_time"), CultureInfo.InvariantCulture, out var s) ? s : TimeSpan.FromHours(9));
            var end = date + (TimeSpan.TryParse(form.GetValueOrDefault("labour_finish_time"), CultureInfo.InvariantCulture, out var e) ? e : start.TimeOfDay);
            await jms.Database.ExecuteAsync(
                @"INSERT INTO labour (user_id, creation_date, start_date, end_date, job_id, description, timesheet_duration, approved)
                  VALUES (@u, @now, @s, @e, @j, @d, @h, 't')",
                ("@u", Int(form.GetValueOrDefault("user_id"))), ("@now", DemoSeeder.Stamp(DateTime.Now)), ("@s", DemoSeeder.Stamp(start)),
                ("@e", DemoSeeder.Stamp(end)), ("@j", Int(query.GetValueOrDefault("id"))), ("@d", form.GetValueOrDefault("description")),
                ("@h", (end - start).TotalHours));
            return Html(request, "saved");
        }

        private async Task<HttpResponseMessage> AddOrDeleteServiceAsync(HttpRequestMessage request, Dictionary<string, string> query,
            Dictionary<string, string> form, int userId)
        {
            var jobId = Int(query.GetValueOrDefault("id"));
            var now = DemoSeeder.Stamp(DateTime.Now);

            if (query.TryGetValue("del", out var del))
            {
                await jms.Database.ExecuteAsync("UPDATE services SET deleted = 't' WHERE service_id = @s AND job_id = @j", ("@s", Int(del)), ("@j", jobId));
                return Html(request, "deleted");
            }

            await jms.Database.ExecuteAsync(
                @"INSERT INTO services (job_id, equipment_id, service_type_id, creation_date, user_id, description, cost_price, quantity, deleted, approved)
                  VALUES (@j, @e, @t, @now, @u, @d, @c, @q, 'f', 't')",
                ("@j", jobId), ("@e", Int(form.GetValueOrDefault("equipment_id_add"))), ("@t", Int(form.GetValueOrDefault("service_type"))),
                ("@now", now), ("@u", Int(form.GetValueOrDefault("user_id")) is var u and > 0 ? u : userId),
                ("@d", form.GetValueOrDefault("description")), ("@c", Dec(form.GetValueOrDefault("cost_price"))),
                ("@q", Int(form.GetValueOrDefault("quantity")) is var q and > 0 ? q : 1));
            await jms.Database.ExecuteAsync("UPDATE job SET last_worked_date = @now WHERE job_id = @j", ("@now", now), ("@j", jobId));
            return Html(request, "saved");
        }

        private async Task<HttpResponseMessage> UpdateServiceAsync(HttpRequestMessage request, Dictionary<string, string> query,
            Dictionary<string, string> form)
        {
            var jobId = Int(query.GetValueOrDefault("id"));
            await jms.Database.ExecuteAsync(
                "UPDATE services SET description = @d, cost_price = @c, quantity = @q, service_type_id = @t WHERE service_id = @s AND job_id = @j",
                ("@d", form.GetValueOrDefault("description")), ("@c", Dec(form.GetValueOrDefault("cost_price"))),
                ("@q", Int(form.GetValueOrDefault("quantity")) is var q and > 0 ? q : 1), ("@t", Int(form.GetValueOrDefault("service_type"))),
                ("@s", Int(query.GetValueOrDefault("s_id"))), ("@j", jobId));
            await jms.Database.ExecuteAsync("UPDATE job SET last_worked_date = @now WHERE job_id = @j", ("@now", DemoSeeder.Stamp(DateTime.Now)), ("@j", jobId));
            return Html(request, "saved");
        }

        private async Task<HttpResponseMessage> UploadResourceAsync(HttpRequestMessage request, Dictionary<string, string> query,
            Dictionary<string, string> form, int userId)
        {
            var id = await jms.Database.ExecuteAsync("SELECT COALESCE(MAX(online_resource_id), 0) + 1 FROM online_resource");
            await jms.Database.ExecuteAsync(
                @"INSERT INTO online_resource (online_resource_id, title, file_location, notes, creation_date, user_id, active_status)
                  VALUES (@id, @t, @f, @n, @now, @u, 't')",
                ("@id", id), ("@t", form.GetValueOrDefault("title") ?? "Document"), ("@f", $"/demo/resource-{id}.pdf"),
                ("@n", form.GetValueOrDefault("notes")), ("@now", DemoSeeder.Stamp(DateTime.Now)), ("@u", userId));
            await jms.Database.ExecuteAsync(
                "INSERT INTO online_resource_link (online_resource_id, link_table_key_name, link_table_key_value) VALUES (@id, 'job_id', @j)",
                ("@id", id), ("@j", Int(query.GetValueOrDefault("id"))));
            return Html(request, "uploaded");
        }

        private async Task<HttpResponseMessage> CreateRecallAsync(HttpRequestMessage request, Dictionary<string, string> form, int userId)
        {
            await jms.Database.ExecuteAsync(
                "INSERT INTO recall (tga_recall_reference, notes, active, user_id, creation_date) VALUES (@r, @n, 't', @u, @now)",
                ("@r", form.GetValueOrDefault("tga_recall_reference")), ("@n", form.GetValueOrDefault("recall_title")),
                ("@u", userId), ("@now", DemoSeeder.Stamp(DateTime.Now)));
            return Html(request, "saved");
        }

        // Background report jobs finish straight away: the "PDF Complete" notification the app
        // polls for is posted at once, linking to a demo-files/ download this handler serves.
        private async Task<HttpResponseMessage> BackgroundJobAsync(HttpRequestMessage request, Dictionary<string, string> form, int userId)
        {
            var message = form.GetValueOrDefault("job_type") switch
            {
                "report_job_progress_cli" => $"Job Progress Report for {form.GetValueOrDefault("company_name")} has been completed, Please check your notification to download.",
                _ => $"Detailed Inspection Report for job {form.GetValueOrDefault("job_id")} has been completed, Please check your notification to download.",
            };
            var id = await jms.Database.ExecuteAsync("SELECT COALESCE(MAX(user_notifications_id), 0) + 1 FROM user_notification");
            await jms.Database.ExecuteAsync(
                @"INSERT INTO user_notification (user_notifications_id, user_id, creation_date, notification_link, notification_message, notification_title)
                  VALUES (@id, @u, @now, @l, @m, 'PDF Complete')",
                ("@id", id), ("@u", userId), ("@now", DemoSeeder.Stamp(DateTime.Now)), ("@l", $"demo-files/report-{id}.pdf"), ("@m", message));
            return Html(request, "queued");
        }

        // ── Responses ──

        private static HttpResponseMessage Html(HttpRequestMessage request, string body) =>
            new(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent(body, Encoding.UTF8, "text/html") };

        private static HttpResponseMessage Json(HttpRequestMessage request, object body) =>
            new(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

        private static HttpResponseMessage Pdf(HttpRequestMessage request, string title, string detail, string? fileName = null)
        {
            var content = new ByteArrayContent(DemoDocuments.Pdf(title, detail));
            content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
            if (fileName != null)
                content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileName = fileName };
            return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = content };
        }

        private static async Task<Dictionary<string, string>> ReadFormAsync(HttpContent? content)
        {
            var form = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            switch (content)
            {
                case MultipartFormDataContent multipart:
                    foreach (var part in multipart)
                    {
                        var name = part.Headers.ContentDisposition?.Name?.Trim('"');
                        if (name != null && part.Headers.ContentDisposition?.FileName == null)
                            form[name] = await part.ReadAsStringAsync();
                    }
                    break;
                case FormUrlEncodedContent:
                    foreach (var (key, value) in QueryHelpers.ParseQuery(await content.ReadAsStringAsync()))
                        form[key] = value.ToString();
                    break;
            }
            return form;
        }

        private static int Int(string? s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
        private static double Dec(string? s) => double.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : 0;
    }
}
