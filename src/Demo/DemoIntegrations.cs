using WorkshopDashboard.Services.External.Google;
using WorkshopDashboard.Services.External.Monday;

namespace WorkshopDashboard.Demo;

// Demo mode's stand-ins for the Google Sheets and monday.com integrations: fixed, invented data
// shaped like the real responses, so the pages that read them render normally.

/// <summary>A credit statement for whichever customer tab is asked for.</summary>
public sealed class DemoCustomerCredit : ICustomerCreditSource
{
    public Task<CustomerCredit> GetCreditAsync(string tabName)
    {
        var lines = new List<CreditLine>
        {
            new("4821937", "GIF-H190", "$1,250.00", "", "14/03/2026", "Q-50112", "", "$1,250.00"),
            new("5531024", "CF-HQ190L", "$860.00", "", "02/05/2026", "Q-50240", "", "$2,110.00"),
            new("4821937", "GIF-H190", "", "$600.00", "19/07/2026", "Q-50377", "PO-417263", "$1,510.00"),
        };
        return Task.FromResult(new CustomerCredit(tabName, "$2,110.00", "$1,510.00", lines));
    }
}

/// <summary>Accepts the export and logs it — there's no sheet to write to in the demo.</summary>
public sealed class DemoLoanEquipmentExporter(ILogger<DemoLoanEquipmentExporter> logger) : ILoanEquipmentExporter
{
    public Task ExportAsync(List<LoanEquipmentExportRow> rows)
    {
        logger.LogInformation("Loan equipment export of {Count} rows (not written anywhere in demo mode)", rows.Count);
        return Task.CompletedTask;
    }
}

/// <summary>A purchasing board with open and finalising orders. PO numbers match the demo JMS
/// purchase orders (DemoSeeder.SeedPurchasing) so the page's PO lookups find them.</summary>
public sealed class DemoMonday : IMondayClient
{
    public const string BoardId = "1000001";
    public const string OpenGroupId = "topics";
    public const string FinalisationGroupId = "finalisation";

    private static readonly string[] Titles =
    [
        "Status", "PO", "Job Number", "Vendor/ Supplier", "Qty / Part No / Description", "Requester's Name",
        "Requested Date", "Date Required", "URGENCY", "ASSIGNED", "Approx Cost", "Actual Cost to Us", "Connote",
        "Invoice No.", "Invoiced Amount", "Transfer Type", "Purchase Category", "Summarize updates", "Last updated",
    ];

    public Task<List<MondayBoard>> GetBoardsAsync(CancellationToken ct = default) =>
        Task.FromResult(new List<MondayBoard> { new() { Id = BoardId, Name = "Purchasing" } });

    public Task<List<MondayGroup>> GetGroupsAsync(string boardId, CancellationToken ct = default) =>
        Task.FromResult(new List<MondayGroup>
        {
            new() { Id = OpenGroupId, Title = "Open Orders" },
            new() { Id = FinalisationGroupId, Title = "Order Finalisation" },
        });

    public Task<MondayBoardDetail> GetBoardDetailAsync(string boardId, IReadOnlyCollection<string>? groupIds = null, CancellationToken ct = default)
    {
        var finalising = groupIds?.Contains(FinalisationGroupId) == true;
        var columns = Titles.Select((t, i) => new MondayColumn { Id = $"col{i}", Title = t, Type = "text" }).ToList();
        var items = Enumerable.Range(0, finalising ? 4 : 8).Select(i => Item(i + (finalising ? 8 : 0), finalising)).ToList();
        return Task.FromResult(new MondayBoardDetail { Columns = columns, Items = items });
    }

    private static MondayItem Item(int i, bool finalising)
    {
        var (_, supplier) = DemoSeeder.Repairers[i % DemoSeeder.Repairers.Length];
        var requested = DateTime.Today.AddDays(-3 - i * 4);
        var values = new[]
        {
            finalising ? "Received" : (i % 3 == 0 ? "Awaiting Approval" : "Ordered"),
            $"{5001 + i % 12}",
            $"{DemoSeeder.FirstJobId + 380 + i}",
            supplier,
            $"1 x {(i % 2 == 0 ? "Bending section rubber" : "Light guide bundle")}",
            i % 2 == 0 ? "Demo User" : "Workshop Automation",
            requested.ToString("yyyy-MM-dd"),
            requested.AddDays(14).ToString("yyyy-MM-dd"),
            i % 4 == 0 ? "Urgent" : "Normal",
            "Demo User",
            $"{120 + i * 35}.00",
            finalising ? $"{110 + i * 33}.00" : "",
            finalising || i % 2 == 0 ? $"{4300000000 + i * 7919}" : "",
            finalising ? $"INV-{88200 + i}" : "",
            finalising ? $"{110 + i * 33}.00" : "",
            "Purchase",
            i % 2 == 0 ? "Repair Parts" : "Consumables",
            finalising ? "Goods received, invoice to match" : "Supplier confirmed order",
            DateTime.Today.AddDays(-i).ToString("yyyy-MM-dd"),
        };
        return new MondayItem
        {
            Id = $"{900000 + i}",
            Name = $"Order {i + 1:00} — {supplier}",
            ColumnValues = values.Select((v, c) => (v, c)).ToDictionary(x => $"col{x.c}", x => x.v),
        };
    }
}
