using System.Globalization;
using System.Net;
using CsvHelper;
using System.Text;
using WorkshopDashboard.Components.Helpers;
using WorkshopDashboard.Services.External;
using WorkshopDashboard.Services.External.Email;
using WorkshopDashboard.Services.External.Google;
using WorkshopDashboard.Services.External.Monday;
using WorkshopDashboard.Services.External.OpenAi;
using WorkshopDashboard.Services.External.Jms;
using WorkshopDashboard.Services.External.Shipping;

namespace WorkshopDashboard.Demo;

// ═══════════════════════════════════════════════════════════════════════════
//  DemoMode
//
//  DemoMode=true (the default in appsettings.json) runs the whole app with no
//  outside services and no secrets: JMS is DemoJms over a seeded in-memory
//  database, and every other integration (OpenAI, carriers, Gmail, Sheets,
//  monday.com) is a stand-in. Everything above those seams — pages, lookups,
//  the write queue, pricing — is the same code the real deployment runs.
//  Log in as demo / demo.
// ═══════════════════════════════════════════════════════════════════════════

public static class DemoMode
{
    public const string ConfigKey = "DemoMode";

    public static void AddDemoServices(this IServiceCollection services)
    {
        services.AddOptions<JmsOptions>().Configure(o =>
        {
            var demo = DemoJms.Options;
            o.BaseUrl = demo.BaseUrl;
            o.LinkBaseUrl = demo.LinkBaseUrl;
            o.LoginLatitude = demo.LoginLatitude;
            o.LoginLongitude = demo.LoginLongitude;
            o.ServiceAccount = demo.ServiceAccount;
            o.Endpoints = demo.Endpoints;
        });
        services.Configure<MondayOptions>(o =>
        {
            o.PurchasingBoardId = DemoMonday.BoardId;
            o.OpenOrdersGroupId = DemoMonday.OpenGroupId;
            o.FinalisationGroupId = DemoMonday.FinalisationGroupId;
        });
        DevOnlyComponentBase.AllowedUsername = DemoJms.DemoUsername;

        services.AddSingleton<DemoJmsDatabase>();
        services.AddSingleton<DemoJms>();
        services.AddSingleton(sp => sp.GetRequiredService<DemoJms>().Transport);
        services.AddSingleton(_ =>
        {
            var status = new JmsStatus();
            status.Report(true, DateTime.Now); // always reachable — there's no uptime monitor in demo mode
            return status;
        });
        services.AddScoped<DemoAppSeeder>();

        services.AddSingleton<IJsonCompletionClient, DemoJsonCompletionClient>();
        services.AddSingleton<DemoMailOutbox>();
        services.AddSingleton<IMailTransport>(sp => sp.GetRequiredService<DemoMailOutbox>());
        services.AddSingleton<ICustomerCreditSource, DemoCustomerCredit>();
        services.AddSingleton<ILoanEquipmentExporter, DemoLoanEquipmentExporter>();
        services.AddSingleton<IMondayClient, DemoMonday>();
        foreach (var carrier in new[] { "TNT", "JMLA", "DHL", "FedEx" })
            services.AddSingleton<ICarrierTracker>(new DemoCarrierTracker(carrier));
    }

    /// <summary>Seeds the app's own settings from the demo data the first time (see DemoAppSeeder).</summary>
    public static async Task SeedDemoAppDataAsync(this WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<DemoAppSeeder>().SeedIfEmptyAsync();
    }

    /// <summary>The "open in JMS" job links (JmsLinks) land here in demo mode: a plain read-only
    /// summary of the job from the demo database, instead of the real system's job page.</summary>
    public static void MapDemoJmsPages(this WebApplication app)
    {
        app.MapGet("/demo-jms/{page}", async (string page, int? id, DemoJmsDatabase db) =>
        {
            if (!page.Equals(DemoJms.Options.Endpoints.JobSummary, StringComparison.OrdinalIgnoreCase) || id is null)
                return Results.NotFound();

            var job = await db.QueryCsvAsync($@"
                SELECT j.job_id AS ""Job"", c.company_name AS ""Owner"", js.job_status AS ""Status"", j.notes AS ""Fault"",
                       j.creation_date AS ""Created"", j.last_worked_date AS ""Last worked""
                FROM job j LEFT JOIN company c ON c.company_id = j.owner_company_id
                LEFT JOIN job_status js ON js.job_status_id = j.job_status_id WHERE j.job_id = {id.Value}");
            var history = await db.QueryCsvAsync($@"
                SELECT jsh.creation_date AS ""When"", js.job_status AS ""Status"", jsh.notes AS ""Notes""
                FROM job_status_history jsh LEFT JOIN job_status js ON js.job_status_id = jsh.job_status_id
                WHERE jsh.job_id = {id.Value} ORDER BY jsh.creation_date");
            var services = await db.QueryCsvAsync($@"
                SELECT s.creation_date AS ""Added"", st.service_type AS ""Type"", s.description AS ""Description"", s.cost_price AS ""Cost""
                FROM services s LEFT JOIN service_type st ON st.service_type_id = s.service_type_id
                WHERE s.job_id = {id.Value} AND s.deleted = 'f' ORDER BY s.creation_date");

            var html = new StringBuilder($"<!doctype html><html><head><meta charset=utf-8><meta name=viewport content=\"width=device-width\">" +
                $"<title>Job {id} (demo)</title><style>body{{font-family:system-ui,sans-serif;margin:24px;max-width:960px}}" +
                "table{border-collapse:collapse;width:100%;margin-bottom:24px}td,th{border:1px solid #ccc;padding:6px 8px;text-align:left;vertical-align:top}" +
                "th{background:#f3f4f6}p{color:#555}</style></head><body>");
            html.Append($"<h1>Job {id}</h1><p>Demo stand-in for the job management system's own job page.</p>");
            html.Append(Table(job)).Append("<h2>Status history</h2>").Append(Table(history)).Append("<h2>Services</h2>").Append(Table(services));
            return Results.Content(html.Append("</body></html>").ToString(), "text/html");
        }).RequireAuthorization();
    }

    private static string Table(string csv)
    {
        using var parser = new CsvParser(new StringReader(csv), CultureInfo.InvariantCulture);
        var sb = new StringBuilder("<table>");
        var rows = 0;
        while (parser.Read())
        {
            var tag = rows++ == 0 ? "th" : "td";
            sb.Append("<tr>");
            foreach (var cell in parser.Record ?? [])
                sb.Append($"<{tag}>{WebUtility.HtmlEncode(cell).Replace("\n", "<br>")}</{tag}>");
            sb.Append("</tr>");
        }
        return rows <= 1 ? "<p>None.</p>" : sb.Append("</table>").ToString();
    }
}
