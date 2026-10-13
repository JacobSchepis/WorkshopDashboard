using System.Globalization;
using Bogus;
using Microsoft.Data.Sqlite;

namespace WorkshopDashboard.Demo;

// ═══════════════════════════════════════════════════════════════════════════
//  DemoSeeder
//
//  Fills the demo JMS database with a made-up endoscope repair workshop: our own
//  company, manufacturers, the six (fictional) repairers, ~25 hospitals with
//  contacts and equipment, and a few hundred repair jobs. Each job is walked
//  through the real workflow (JobFlow below) and leaves the same trail JMS
//  would — status history, quote lines and quotes, POs, ETA / approval /
//  tracking notes, invoices, inspections — so every queue, report and lookup in
//  the app has something realistic to show.
//
//  Fixed seed, dates relative to today: the same data every run, and "stale"
//  or "overdue" always means the same thing whenever the demo is opened.
//  Every person, company and number here is generated; none of it is real.
// ═══════════════════════════════════════════════════════════════════════════

public sealed class DemoSeeder
{
    public const int DefaultSeed = 20261012;

    public const int OwnCompanyId = 1;
    public const int LoanPoolCompanyId = 2;
    public const int DemoUserId = 100;
    public const int AutomationUserId = 189; // JmsClient's DefaultServiceUserId
    public const int FirstJobId = 140001;

    private readonly Faker _f;
    private readonly DateTime _now;
    private readonly DateTime _today;
    private SqliteConnection _db = null!;
    private SqliteTransaction _tx = null!;

    private readonly List<int> _staffIds = new();
    private readonly List<(int Id, string Name)> _customers = new();
    private readonly Dictionary<int, List<int>> _contactsByCompany = new();
    private readonly Dictionary<int, List<(int EquipmentId, int ModelId, bool Rigid)>> _equipmentByCompany = new();
    private readonly List<(int ModelId, string Name, bool Rigid)> _models = new();
    private int _serviceId = 900001, _historyId = 1, _quoteId = 50001, _invoiceId = 70001,
        _inspectionId = 30001, _resourceId = 1, _labourId = 1, _jobEquipmentId = 1;

    public DemoSeeder(int seed, DateTime now)
    {
        _f = new Faker("en_AU") { Random = new Randomizer(seed) };
        _now = now;
        _today = now.Date;
    }

    public void Seed(SqliteConnection db)
    {
        _db = db;
        using (_tx = db.BeginTransaction())
        {
            SeedLookups();
            SeedCompanies();
            SeedStaff();
            SeedModels();
            SeedCustomersAndEquipment();
            SeedLoanPool();
            SeedJobs();
            SeedPurchasing();
            SeedRecalls();
            _tx.Commit();
        }
    }

    // ── Reference tables ─────────────────────────────────────────────────────

    // JMS's status ids (the app's JobStatus enum and JmsQueries depend on them), with
    // generic names where JMS's mention the company.
    private static readonly (int Id, string Name)[] Statuses =
    [
        (1, "Waiting Warranty Approval"), (2, "Completed"), (3, "Invoiced"), (4, "Job Created"),
        (5, "Waiting Order No."), (6, "Waiting Picking"), (7, "Waiting Invoicing"), (8, "Waiting More Info"),
        (9, "Waiting Parts"), (10, "Waiting Quote Confirmation"), (11, "Waiting Service"), (12, "Work In Progress"),
        (13, "With Technician"), (14, "Parts Allocated"), (15, "Job Created (VMAU)"), (16, "Unit arrived at Workshop"),
        (17, "Unit collected"), (18, "Unit sent for repair"), (19, "Unit returned to service"), (20, "Loan Unit Provided"),
        (21, "Unit Decommissioned"), (22, "Waiting Invoice Approval"), (26, "Job Finalisation Request"),
        (27, "Unit returned to service.  Waiting PO."), (28, "Waiting Assessment"), (29, "Waiting Workshop Assignment"),
        (30, "Waiting Workshop Processing"), (31, "Workshop Finalisation Complete"), (32, "Waiting Quality Control Check"),
        (33, "Waiting Outward Shipping"), (34, "Waiting for arrival at Workshop"), (35, "Quote declined (return unrepaired)"),
        (36, "Quote declined (waiting customer resp)"), (37, "Queued for Assessment"), (38, "Quote declined (waiting sales assessment)"),
        (40, "Workshop Repair Initiated"), (41, "Assessment Complete"), (42, "Unit departed Workshop"),
        (43, "Repair booked with Third Party"), (44, "Repair on hold"), (45, "Work in progress with Third Party"),
        (46, "Preparing Quote"), (47, "Waiting Dispatch Booking"),
    ];

    private void SeedLookups()
    {
        foreach (var (id, name) in Statuses)
            Insert("job_status", new() { ["job_status_id"] = id, ["job_status"] = name });

        foreach (var (id, name) in new[] { (1, "LABOUR"), (2, "PARTS"), (9, "QUOTED"), (10, "FREIGHT"), (11, "NOTE") })
            Insert("service_type", new() { ["service_type_id"] = id, ["service_type"] = name, ["description"] = name, ["active_status"] = "t" });

        foreach (var (id, name) in new[] { (1, "Operational"), (2, "Not Operational"), (3, "Operational with Limitations") })
            Insert("operational_status", new() { ["operational_status_id"] = id, ["operational_status"] = name, ["active"] = "t" });

        foreach (var (id, name) in new[] { (1, "Ordered"), (2, "Part Delivered"), (3, "Delivered"), (4, "Cancelled") })
            Insert("order_status", new() { ["order_status_id"] = id, ["order_status"] = name, ["active_status"] = "t" });

        foreach (var (id, units) in new[] { (1, "MΩ"), (2, "µA"), (3, "Pass/Fail") })
            Insert("test_script_item", new() { ["test_script_item_id"] = id, ["test_type_id"] = id, ["units"] = units, ["active_status"] = "t" });
    }

    private static readonly string[] Manufacturers = ["Olympus", "Pentax Medical", "Fujifilm", "Karl Storz", "Stryker", "Richard Wolf"];

    // The fictional repairers the quote pages are written for (see Services/Quoting).
    public static readonly (int Id, string Name)[] Repairers =
    [
        (20, "Apex Scope Repair"), (21, "Bayside Repair Services"), (22, "Cobalt Endoscopy Inc."),
        (23, "Dorado Rigid Repair"), (24, "Ember Imaging"), (25, "Falcon Surgical Instruments"),
    ];

    private void SeedCompanies()
    {
        Company(OwnCompanyId, "Workshop Dashboard Demo", "1 Example Street, Sydney NSW 2000");
        Company(LoanPoolCompanyId, "Workshop Dashboard Demo (Loan Pool)", "1 Example Street, Sydney NSW 2000");
        for (var i = 0; i < Manufacturers.Length; i++)
            Company(10 + i, Manufacturers[i], _f.Address.FullAddress());
        foreach (var (id, name) in Repairers)
            Company(id, name, _f.Address.FullAddress());
    }

    private void Company(int id, string name, string address) =>
        Insert("company", new()
        {
            ["company_id"] = id, ["company_name"] = name, ["address"] = address, ["shipping_address"] = address,
            ["creation_date"] = Stamp(_today.AddYears(-6)), ["email"] = $"office@{Slug(name)}.example.com",
        });

    private void SeedStaff()
    {
        User(DemoUserId, "Demo", "User");
        User(AutomationUserId, "Workshop", "Automation");
        for (var i = 0; i < 7; i++)
            User(101 + i, _f.Name.FirstName(), _f.Name.LastName());
    }

    private void User(int id, string first, string last)
    {
        Insert("user", new()
        {
            ["user_id"] = id, ["first_name"] = first, ["last_name"] = last, ["title"] = "Technician",
            ["company_id"] = OwnCompanyId, ["creation_date"] = Stamp(_today.AddYears(-3)),
        });
        if (id != AutomationUserId) _staffIds.Add(id);
    }

    // Real manufacturers' public model names, so the equipment reads naturally.
    private static readonly (int Manufacturer, string Model, string Generic, bool Rigid)[] ModelCatalog =
    [
        (10, "GIF-H190", "Gastroscope", false), (10, "GIF-HQ190", "Gastroscope", false), (10, "CF-HQ190L", "Colonoscope", false),
        (10, "PCF-H190DL", "Colonoscope", false), (10, "BF-1TH190", "Bronchoscope", false), (10, "TJF-Q190V", "Duodenoscope", false),
        (11, "EG29-i10", "Gastroscope", false), (11, "EC38-i10L", "Colonoscope", false), (11, "EB15-J10", "Bronchoscope", false),
        (12, "EG-760R", "Gastroscope", false), (12, "EC-760R-V/L", "Colonoscope", false),
        (13, "26003AA", "Rigid Telescope 0°", true), (13, "26003BA", "Rigid Telescope 30°", true), (13, "27005BA", "Rigid Telescope 30°", true),
        (14, "502-503-030", "Rigid Arthroscope 30°", true), (14, "502-503-070", "Rigid Arthroscope 70°", true),
        (15, "8654.431", "Rigid Cystoscope", true),
    ];

    private void SeedModels()
    {
        for (var i = 0; i < ModelCatalog.Length; i++)
        {
            var (manufacturer, name, generic, rigid) = ModelCatalog[i];
            var id = 500 + i;
            Insert("model", new()
            {
                ["model_id"] = id, ["manufacture_id"] = manufacturer, ["model_name_no"] = name,
                ["description"] = generic, ["creation_date"] = Stamp(_today.AddYears(-5)),
            });
            _models.Add((id, name, rigid));
        }
    }

    // Made-up place names (Bogus's own cities read as American), so no hospital here is real.
    // Every combination was checked against real hospital names, which the obvious ones collide with.
    private static readonly string[] PlacePrefixes = ["Wattle", "Banksia", "Kestrel", "Myrtle", "Juniper", "Heron", "Willow", "Granite"];
    private static readonly string[] PlaceSuffixes = ["view", "field", "wood", "dale", "haven", "side", "vale", "ridge", "brook", "gate"];
    private static readonly string[] Saints = ["Anne", "Brendan", "Clare", "Bridget", "Columba", "Aidan"];

    private int _contactId = 2001, _equipmentId = 10001;

    private void SeedCustomersAndEquipment()
    {
        var used = new HashSet<string>();
        for (var i = 0; i < 25; i++)
        {
            string name;
            do
            {
                var place = $"{_f.PickRandom(PlacePrefixes)}{_f.PickRandom(PlaceSuffixes)}";
                name = _f.Random.WeightedRandom(
                    [$"{place} Private Hospital", $"{place} Hospital", $"St {_f.PickRandom(Saints)}'s Hospital",
                     $"{place} Day Surgery", $"{place} Endoscopy Centre"],
                    [0.3f, 0.3f, 0.15f, 0.15f, 0.1f]);
            } while (!used.Add(name));

            var id = 100 + i;
            Company(id, name, _f.Address.FullAddress());
            Insert("company_client", new()
            {
                ["company_id"] = id, ["labour_pref"] = 1, ["travel_pref"] = 1, ["part_pref"] = 1,
                ["active_status"] = "t", ["standard_labour_rate"] = 165.0,
            });
            _customers.Add((id, name));

            var contacts = new List<int>();
            for (var c = 0; c < _f.Random.Int(2, 4); c++)
            {
                var first = _f.Name.FirstName();
                var last = _f.Name.LastName();
                Insert("company_people", new()
                {
                    ["company_people_id"] = _contactId, ["full_name"] = $"{first} {last}", ["first_name"] = first, ["last_name"] = last,
                    ["title"] = _f.PickRandom("Nurse Unit Manager", "Endoscopy Coordinator", "Biomedical Engineer", "Theatre Manager", "Purchasing Officer"),
                    ["email"] = $"{first}.{last}@{Slug(name)}.example.com".ToLowerInvariant(),
                    ["phone_no"] = $"02 9{_f.Random.Number(100, 999)} {_f.Random.Number(1000, 9999)}",
                    ["company_id"] = id, ["active_status"] = "t", ["expired"] = "f",
                });
                contacts.Add(_contactId++);
            }
            _contactsByCompany[id] = contacts;

            var units = new List<(int, int, bool)>();
            for (var e = 0; e < _f.Random.Int(5, 14); e++)
            {
                var model = _f.PickRandom(_models);
                AddEquipment(_equipmentId, model.ModelId, id, barcode: "");
                units.Add((_equipmentId++, model.ModelId, model.Rigid));
            }
            _equipmentByCompany[id] = units;
        }
    }

    private void AddEquipment(int id, int modelId, int companyId, string barcode)
    {
        var model = _models.First(m => m.ModelId == modelId);
        Insert("equipment", new()
        {
            ["equipment_id"] = id, ["model_id"] = modelId, ["serial_no"] = _f.Random.Replace("#######"),
            ["current_company_id"] = companyId, ["generic_name"] = ModelCatalog[modelId - 500].Generic,
            ["model_name_no"] = model.Name, ["expired"] = "f", ["barcode_no"] = barcode,
            ["asset_no"] = _f.Random.Replace("A#####"), ["creation_date"] = Stamp(_today.AddDays(-_f.Random.Int(400, 2000))),
            ["last_inspected_date"] = Stamp(_today.AddDays(-_f.Random.Int(5, 300))), ["asset_condition"] = "Good",
        });
    }

    private void SeedLoanPool()
    {
        foreach (var model in _models.Where(m => !m.Rigid).Take(10))
            AddEquipment(_equipmentId++, model.ModelId, OwnCompanyId, barcode: "LOAN-UNIT");
    }

    // ── Jobs ─────────────────────────────────────────────────────────────────

    private static readonly string[] Faults =
    [
        "No image on monitor — intermittent picture loss", "Angulation stiff in up/down direction", "Fluid invasion suspected after reprocessing",
        "Failed leak test at insertion tube", "Bending section rubber torn", "Light guide damaged, dim illumination",
        "Biopsy channel blocked", "Air/water not working", "Image cloudy, possible lens damage", "Insertion tube crushed near boot",
        "Telescope image hazy — moisture in optics", "Bent shaft on rigid telescope", "Eyepiece loose", "Annual preventative maintenance",
    ];

    private static readonly string[] QuoteLines =
    [
        "Replace bending section rubber", "Replace insertion tube", "Replace CCD and adjust image", "Replace light guide bundle",
        "Overhaul angulation system", "Replace biopsy channel", "Replace distal tip lens", "Re-seal and leak test",
        "Replace rod lens system", "Straighten shaft and re-align optics", "Replace eyepiece assembly",
    ];

    // A job's route through the workflow. Each step is (status, days after the previous step).
    private enum Route { Repaired, InHouse, Declined }

    private void SeedJobs()
    {
        var jobId = FirstJobId;
        for (var i = 0; i < 420; i++)
        {
            // Older jobs are mostly finished; the last few weeks are spread over every stage.
            var ageDays = (int)Math.Round(Math.Pow(_f.Random.Double(), 1.6) * 520);
            var created = _today.AddDays(-ageDays).AddHours(_f.Random.Int(8, 16)).AddMinutes(_f.Random.Int(0, 59));
            // A job made "today" can't be booked in later than now, or it would have no history yet.
            // Drawn either way, so the time of day doesn't shift every random number after it.
            var earlier = _now.AddMinutes(-_f.Random.Int(5, 120));
            if (created > _now) created = earlier;
            SeedJob(jobId++, created);
        }

        // Which jobs the walk above leaves sitting in each queue depends on the date, so a few
        // are always parked in the queues the main pages list, or a page could open empty.
        foreach (var queue in AlwaysOccupiedQueues)
            for (var i = 0; i < 3; i++)
                SeedJob(jobId++, _now.AddDays(-_f.Random.Int(0, 10)).AddHours(-_f.Random.Int(1, 8)), stopAt: queue);
    }

    // Waiting Quote Confirmation, Waiting Workshop Processing, Waiting Workshop Assignment,
    // Workshop Finalisation Complete.
    private static readonly int[] AlwaysOccupiedQueues = [10, 30, 29, 31];

    private void SeedJob(int jobId, DateTime created, int? stopAt = null)
    {
        var (customerId, _) = _f.PickRandom(_customers);
        var unit = _f.PickRandom(_equipmentByCompany[customerId]);
        var contact = _f.PickRandom(_contactsByCompany[customerId]);
        var route = stopAt is null
            ? _f.Random.WeightedRandom([Route.Repaired, Route.InHouse, Route.Declined], [0.62f, 0.23f, 0.15f])
            : Route.Repaired; // the one route through every queue

        var steps = route switch
        {
            Route.Repaired => new[] { 4, 34, 16, 37, 46, 10, 30, 29, 43, 45, 31, 32, 33, 42, 2 },
            Route.InHouse => [4, 34, 16, 37, 46, 10, 30, 40, 32, 33, 42, 2],
            _ => [4, 34, 16, 37, 46, 10, _f.PickRandom(35, 36), 33, 42, 2],
        };

        // Some older jobs stalled part-way (parts on back-order, a customer gone quiet) — they're
        // what the stale-job pages and "overdue" badges find.
        if (stopAt is { } queue)
            steps = steps[..(Array.IndexOf(steps, queue) + 1)];
        else if (created < _today.AddDays(-30) && _f.Random.Bool(0.05f))
        {
            steps = steps.Take(_f.Random.Int(3, steps.Length - 2)).ToArray();
            if (_f.Random.Bool(0.3f)) steps = [.. steps, 44];
        }

        // Walk the route from creation, a few days per step, stopping at today.
        var history = new List<(int Status, DateTime At)>();
        var at = created;
        foreach (var status in steps)
        {
            if (history.Count > 0) at = at.AddDays(StepDays(status)).AddHours(_f.Random.Int(0, 6));
            if (at > _now && stopAt is null) break;
            history.Add((status, at));
        }

        // A parked job walks its whole route, then moves back in time so it reached its queue recently.
        if (stopAt is not null && history[^1].At > _now)
        {
            var shift = history[^1].At - _now.AddHours(-_f.Random.Int(1, 48));
            created -= shift;
            history = history.Select(h => (h.Status, h.At - shift)).ToList();
        }

        var current = history[^1];
        var completed = current.Status == 2 ? current.At : (DateTime?)null;
        var staff = _f.PickRandom(_staffIds);
        // Most open jobs were worked on recently; a few have gone quiet (the stale-job pages).
        DateTime? lastWorked = completed ?? (_f.Random.Bool(0.12f) ? null
            : current.At.AddDays(_f.Random.Int(0, Math.Max(0, (int)(_now - current.At).TotalDays))));
        if (lastWorked > _now) lastWorked = _now.AddHours(-_f.Random.Int(1, 48));

        Insert("job", new()
        {
            ["job_id"] = jobId, ["job_no"] = jobId, ["owner_company_id"] = customerId, ["job_status_id"] = current.Status,
            ["user_id"] = staff, ["contact_name"] = contact.ToString(CultureInfo.InvariantCulture), ["notes"] = _f.PickRandom(Faults),
            ["creation_date"] = Stamp(created), ["start_date"] = Stamp(created), ["completed_date"] = Stamp(completed),
            ["last_worked_date"] = Stamp(lastWorked), ["job_priority_id"] = _f.Random.WeightedRandom([3, 2, 1], [0.7f, 0.2f, 0.1f]),
            ["standard_labour_rate"] = 165.0, ["order_number"] = "",
        });
        Insert("job_equipment", new()
        {
            ["job_equipment_id"] = _jobEquipmentId++, ["job_id"] = jobId, ["equipment_id"] = unit.EquipmentId,
            ["creation_date"] = Stamp(created), ["active_status"] = "t",
        });

        foreach (var (status, when) in history)
        {
            Insert("job_status_history", new()
            {
                ["job_status_history_id"] = _historyId++, ["job_id"] = jobId, ["job_status_id"] = status,
                ["creation_date"] = Stamp(when), ["user_id"] = _f.PickRandom(_staffIds), ["notes"] = HistoryNote(status),
            });
            OnStatus(jobId, status, when, unit, contact, history);
        }

        if (completed is { } done && route != Route.Declined && _f.Random.Bool(0.85f))
            Invoice(jobId, done);
    }

    private int StepDays(int status) => status switch
    {
        34 or 16 => _f.Random.Int(1, 4),
        10 => _f.Random.Int(2, 6),
        30 => _f.Random.Int(3, 21),   // waiting on the customer's approval / PO
        29 or 43 => _f.Random.Int(1, 5),
        45 => _f.Random.Int(5, 20),   // away at the repairer
        _ => _f.Random.Int(1, 4),
    };

    private string HistoryNote(int status) => status switch
    {
        42 => $"DHL {_f.Random.Replace("##########")}",
        33 => _f.Random.Bool(0.3f) ? "Booked with courier" : "",
        _ => "",
    };

    private decimal _lastQuoteTotal;

    // What each step leaves behind, beyond its history row.
    private void OnStatus(int jobId, int status, DateTime at, (int EquipmentId, int ModelId, bool Rigid) unit, int contact,
        List<(int Status, DateTime At)> history)
    {
        var reached = history.Select(h => h.Status).ToHashSet();
        switch (status)
        {
            case 16:
                Service(jobId, unit.EquipmentId, 11, at, "[Tracking] Unit received and logged in", 0m);
                break;

            case 10: // quoted
                var lines = _f.Random.ListItems(QuoteLines.ToList(), _f.Random.Int(1, 3));
                var total = 0m;
                foreach (var line in lines)
                {
                    var price = Math.Round(_f.Random.Decimal(unit.Rigid ? 350 : 900, unit.Rigid ? 1200 : 4800) / 5) * 5;
                    total += price;
                    Service(jobId, unit.EquipmentId, 9, at, $"{line}\n@ ${price:0.00}", price);
                }
                var freight = 35m;
                Service(jobId, unit.EquipmentId, 10, at, "Freight & handling", freight);
                total += freight;
                _lastQuoteTotal = total;

                var accepted = reached.Contains(30);
                Insert("job_quote", new()
                {
                    ["job_quote_id"] = _quoteId, ["job_id"] = jobId, ["contact_id"] = contact,
                    ["date_created"] = Stamp(at), ["total_amount"] = (double)total, ["accepted"] = accepted ? "t" : "f",
                    ["order_no"] = accepted ? $"PO-{_f.Random.Number(100000, 999999)}" : "",
                    ["date_accepted"] = accepted ? Stamp(history.First(h => h.Status == 30).At) : null,
                    ["file_location"] = $"/demo/quote-{jobId}-{_quoteId}.pdf",
                });
                _quoteId++;
                break;

            case 30: // approved
                Service(jobId, unit.EquipmentId, 11, at, $"Approval received from customer {at:dd/MM/yyyy}", 0m);
                if (_f.Random.Bool(0.7f))
                    Resource(jobId, "Purchase Order", at);
                if (!reached.Contains(29) && !reached.Contains(40) || _f.Random.Bool(0.5f))
                    Service(jobId, unit.EquipmentId, 11, at.AddHours(2), $"ETA - {at.AddDays(_f.Random.Int(7, 28)):yyyy-MM-dd}", 0m);
                break;

            case 29: // sent to a repairer
                Service(jobId, unit.EquipmentId, 10, at, $"DHL: {_f.Random.Replace("##########")}", 0m);
                break;

            case 31: // repairer shipping it back
                Service(jobId, unit.EquipmentId, 11, at, $"TNT: {_f.Random.Replace("#########")}", 0m);
                break;

            case 32:
                Inspection(jobId, unit.EquipmentId, at);
                Labour(jobId, unit.EquipmentId, at, "Final inspection and leak test");
                break;
        }
    }

    private void Service(int jobId, int equipmentId, int type, DateTime at, string description, decimal cost) =>
        Insert("services", new()
        {
            ["service_id"] = _serviceId++, ["job_id"] = jobId, ["equipment_id"] = equipmentId, ["service_type_id"] = type,
            ["creation_date"] = Stamp(at), ["user_id"] = _f.PickRandom(_staffIds), ["description"] = description,
            ["cost_price"] = (double)cost, ["quantity"] = 1, ["deleted"] = "f", ["approved"] = "t",
        });

    private void Resource(int jobId, string title, DateTime at)
    {
        Insert("online_resource", new()
        {
            ["online_resource_id"] = _resourceId, ["title"] = title, ["file_location"] = $"/demo/resource-{_resourceId}.pdf",
            ["creation_date"] = Stamp(at), ["user_id"] = DemoUserId, ["active_status"] = "t",
        });
        Insert("online_resource_link", new()
        {
            ["id"] = _resourceId, ["online_resource_id"] = _resourceId, ["link_table_key_name"] = "job_id",
            ["link_table_key_value"] = jobId,
        });
        _resourceId++;
    }

    private void Inspection(int jobId, int equipmentId, DateTime at)
    {
        Insert("inspection", new()
        {
            ["inspection_id"] = _inspectionId, ["equipment_id"] = equipmentId, ["user_id"] = _f.PickRandom(_staffIds),
            ["job_id"] = jobId, ["creation_date"] = Stamp(at), ["operational_status_id"] = 1,
            ["comments"] = "Leak test passed. Image and angulation within specification.", ["asset_condition"] = "Good",
        });
        var items = new (int Item, string Value)[] { (1, _f.Random.Int(20, 200) + ""), (2, _f.Random.Int(1, 40) + ""), (3, "Pass") };
        foreach (var (item, value) in items)
            Insert("test_script_result", new()
            {
                ["test_script_result_id"] = _inspectionId * 10 + item, ["inspection_id"] = _inspectionId,
                ["test_script_item_id"] = item, ["creation_date"] = Stamp(at), ["result_value"] = value, ["comment"] = "",
            });
        _inspectionId++;
    }

    private void Labour(int jobId, int equipmentId, DateTime at, string description)
    {
        var hours = _f.PickRandom(0.25, 0.5, 0.75, 1.0);
        Insert("labour", new()
        {
            ["labour_id"] = _labourId++, ["user_id"] = _f.PickRandom(_staffIds), ["creation_date"] = Stamp(at), ["start_date"] = Stamp(at),
            ["end_date"] = Stamp(at.AddHours(hours)), ["job_id"] = jobId, ["equipment_id"] = equipmentId,
            ["description"] = description, ["timesheet_duration"] = hours, ["approved"] = "t",
        });
    }

    private void Invoice(int jobId, DateTime at)
    {
        var total = (double)Math.Round(_lastQuoteTotal * 1.1m, 2);
        Insert("job_invoice", new()
        {
            ["invoice_id"] = _invoiceId++, ["job_id"] = jobId, ["job_no"] = jobId, ["creation_date"] = Stamp(at),
            ["total_amount"] = total, ["paid_amount"] = _f.Random.Bool(0.8f) ? total : 0.0, ["file_location"] = $"/demo/invoice-{jobId}.pdf",
        });
    }

    // ── Purchasing and recalls ───────────────────────────────────────────────

    private void SeedPurchasing()
    {
        for (var i = 0; i < 12; i++)
        {
            var orderId = 5001 + i;
            var vendor = _f.PickRandom(Repairers).Id;
            var ordered = _today.AddDays(-_f.Random.Int(3, 60));
            var delivered = _f.Random.Bool(0.5f);
            Insert("vendor_item_order", new()
            {
                ["vendor_item_order_id"] = orderId, ["company_id"] = vendor, ["notes"] = "Repair parts",
                ["date_ordered"] = Stamp(ordered), ["date_expected"] = Stamp(ordered.AddDays(14)), ["user_id"] = _f.PickRandom(_staffIds),
                ["order_status_id"] = delivered ? 3 : 1, ["fully_paid"] = "f", ["fully_delivered"] = delivered ? "t" : "f",
                ["date_last_delivered"] = delivered ? Stamp(ordered.AddDays(10)) : null,
            });
            Insert("item_master", new()
            {
                ["item_master_id"] = 8001 + i, ["description"] = _f.PickRandom(QuoteLines), ["track_serial"] = "f",
                ["creation_date"] = Stamp(ordered), ["active_status"] = "t",
            });
            Insert("vendor_item_order_vuom", new()
            {
                ["vendor_item_order_vuom_id"] = 6001 + i, ["vendor_item_order_id"] = orderId, ["vendor_company_id"] = vendor,
                ["master_item_id"] = 8001 + i, ["vendor_part_code"] = _f.Random.Replace("??-####").ToUpperInvariant(),
                ["vendor_part_description"] = _f.PickRandom(QuoteLines), ["order_qty_vuom"] = 1, ["delivered_qty_vuom"] = delivered ? 1 : 0,
                ["invoiced_qty_vuom"] = 0, ["total_order_cost_sd"] = (double)Math.Round(_f.Random.Decimal(80, 900), 2),
                // Matches the job DemoMonday's board gives the same PO, except order 6, which is the
                // one the purchasing page flags as "JMS has this PO under a different job".
                ["job_no"] = FirstJobId + 380 + i + (i == 5 ? 7 : 0), ["active_status"] = "t", ["creation_date"] = Stamp(ordered),
            });
        }
    }

    private void SeedRecalls()
    {
        for (var i = 0; i < 3; i++)
            Insert("recall", new()
            {
                ["recall_id"] = i + 1, ["model_id"] = _f.PickRandom(_models).ModelId,
                ["tga_recall_reference"] = $"RC-{_today.Year - 1}-{_f.Random.Number(10000, 99999)}",
                ["notes"] = "Manufacturer field safety notice — inspect and update reprocessing instructions.",
                ["active"] = "t", ["user_id"] = DemoUserId, ["creation_date"] = Stamp(_today.AddDays(-_f.Random.Int(30, 200))),
            });
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void Insert(string table, Dictionary<string, object?> row)
    {
        using var cmd = _db.CreateCommand();
        cmd.Transaction = _tx;
        cmd.CommandText = $"INSERT INTO `{table}` ({string.Join(", ", row.Keys)}) VALUES ({string.Join(", ", row.Keys.Select(k => "@" + k))})";
        foreach (var (key, value) in row)
            cmd.Parameters.AddWithValue("@" + key, value ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public static string? Stamp(DateTime? at) => at?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static string Slug(string name) =>
        new string(name.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray()) is { Length: > 0 } s ? s[..Math.Min(s.Length, 24)] : "company";
}
