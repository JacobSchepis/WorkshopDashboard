using System.Globalization;
using WorkshopDashboard.Data;
using WorkshopDashboard.Models;
using WorkshopDashboard.Services.Reporting;
using WorkshopDashboard.Services.Stores;

namespace WorkshopDashboard.Demo;

// ═══════════════════════════════════════════════════════════════════════════
//  DemoAppSeeder
//
//  The app's own settings that people normally build up by hand — scope
//  customers and their contacts, which repairer handles which models, the
//  Apex price sheet, kiosk notices and tasks, monthly report sites — filled in
//  from the demo JMS data on a demo database's first start, so those pages
//  aren't empty. Runs only when there are no scope customers yet, so anything
//  changed while trying the demo is left alone.
// ═══════════════════════════════════════════════════════════════════════════

public sealed class DemoAppSeeder(
    DemoJmsDatabase jms,
    ScopeCustomerStore customers,
    RepairerStore repairers,
    ApexPriceSheetStore priceSheet,
    TeamNoticeStore notices,
    WorkshopTaskStore tasks,
    TurnaroundStatStore turnaround,
    MonthlyReportService monthlyReports)
{
    public async Task SeedIfEmptyAsync()
    {
        if ((await customers.LoadAsync()).Count > 0) return;

        var companies = Rows(await jms.QueryCsvAsync("SELECT company_id, company_name FROM company WHERE company_id >= 100 ORDER BY company_id"));
        var contacts = Rows(await jms.QueryCsvAsync("SELECT company_people_id, company_id FROM company_people ORDER BY company_people_id"))
            .GroupBy(r => Int(r[1])).ToDictionary(g => g.Key, g => g.Select(r => Int(r[0])).ToList());
        string[] states = ["NSW", "NSW", "VIC", "QLD", "SA", "WA"];

        for (var i = 0; i < companies.Count; i++)
        {
            var id = Int(companies[i][0]);
            var people = contacts.GetValueOrDefault(id) ?? [];
            await customers.AddAsync(new ScopeCustomer
            {
                DisplayName = companies[i][1],
                CompanyIds = [id],
                State = states[i % states.Length],
                PrimaryContactIds = people.Take(1).ToList(),
                SecondaryContactIds = people.Skip(1).Take(2).ToList(),
                Tags = i % 4 == 0 ? ["Contract"] : [],
                CreditSheetTabName = i % 5 == 0 ? companies[i][1] : null,
            });
            if (i < 6) await monthlyReports.AddSiteAsync(id, companies[i][1]);
        }

        // Which repairer each model goes to (RepairerResolver). Model ids are DemoSeeder's catalog.
        var models = Rows(await jms.QueryCsvAsync("SELECT model_id, description FROM model ORDER BY model_id"));
        int[] Where(Func<string, bool> pick) => models.Where(m => pick(m[1])).Select(m => Int(m[0])).ToArray();
        var assignments = new (string Name, int[] Models)[]
        {
            ("Apex", Where(d => d is "Gastroscope" or "Colonoscope" or "Duodenoscope")),
            ("Cobalt", Where(d => d == "Bronchoscope")),
            ("Dorado", Where(d => d.StartsWith("Rigid Telescope") || d.StartsWith("Rigid Arthroscope"))),
            ("Falcon", Where(d => d == "Rigid Cystoscope")),
            ("Bayside", []),
            ("Ember", []),
        };
        foreach (var (name, modelIds) in assignments)
            await repairers.AddAsync(new Repairer { Name = name, ModelIds = modelIds.ToList() });

        await priceSheet.ReplaceAllAsync(PriceSheet());

        await notices.AddAsync(new TeamNotice { Text = "Courier pick-up moved to 2:30pm this week.", Author = "Demo User" });
        await notices.AddAsync(new TeamNotice { Text = "New leak tester calibrated — use bench 2.", Author = "Demo User" });
        foreach (var (label, frequency) in new[]
                 {
                     ("Check reprocessing log", TaskFrequency.Daily), ("Empty sharps bin", TaskFrequency.Daily),
                     ("Calibrate leak tester", TaskFrequency.Weekly), ("Stocktake consumables", TaskFrequency.Monthly),
                 })
            await tasks.AddAsync(new WorkshopTask { Label = label, Frequency = frequency });
        await turnaround.AddAsync(new TurnaroundStat { Label = "Flexible scope repair", Days = 12.5 });
        await turnaround.AddAsync(new TurnaroundStat { Label = "Rigid scope repair", Days = 8 });
    }

    // A small, invented flexible-repair price list in the shape of the real one (code, category,
    // USD price) for the in-house flex quote page and the repair suggester to work from.
    private static List<ApexPriceLine> PriceSheet()
    {
        var lines = new (string Code, string Category, decimal Price, string Description)[]
        {
            ("AX-101", "Bending Section", 640, "Replace bending section rubber"),
            ("AX-102", "Bending Section", 1480, "Replace bending section mesh and rubber"),
            ("AX-201", "Insertion Tube", 2950, "Replace insertion tube"),
            ("AX-301", "Optics", 1860, "Replace CCD and re-align image"),
            ("AX-302", "Optics", 420, "Replace distal tip lens"),
            ("AX-401", "Light Guide", 1320, "Replace light guide bundle"),
            ("AX-501", "Channels", 780, "Replace biopsy channel"),
            ("AX-502", "Channels", 390, "Replace air/water nozzle"),
            ("AX-601", "Control Body", 1150, "Overhaul angulation system"),
            ("AX-901", "General", 210, "Leak test, clean and re-seal"),
        };
        return lines.Select((l, i) => new ApexPriceLine
        {
            Code = l.Code, LineItem = i + 1, Category = l.Category, PriceUsd = l.Price, Description = l.Description,
        }).ToList();
    }

    private static List<string[]> Rows(string csv) =>
        csv.Trim().Split('\n').Skip(1).Where(l => l.Length > 0).Select(SplitCsv).ToList();

    // Company names can contain commas (and so come back quoted).
    private static string[] SplitCsv(string line)
    {
        var cells = new List<string>();
        var cell = new System.Text.StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted && c == '"' && i + 1 < line.Length && line[i + 1] == '"') { cell.Append('"'); i++; }
            else if (c == '"') quoted = !quoted;
            else if (c == ',' && !quoted) { cells.Add(cell.ToString()); cell.Clear(); }
            else cell.Append(c);
        }
        cells.Add(cell.ToString());
        return cells.ToArray();
    }

    private static int Int(string s) => int.Parse(s, CultureInfo.InvariantCulture);
}
