using CsvHelper;
using CsvHelper.Configuration;
using HtmlAgilityPack;
using System.Globalization;
using WorkshopDashboard.Models;
using WorkshopDashboard.Services.External.Jms;
using WorkshopDashboard.Services.Jobs;
using WorkshopDashboard.Services.Workshop;
using WorkshopDashboard.Services.Admin;
using WorkshopDashboard.Services.Stores;

namespace WorkshopDashboard.Services.Monday;

public record MondayStockUnit(int StockId, string SerialNo);

public record MondayEquipmentOption(int EquipmentId, string ModelName, string SerialNo);

/// <summary>
/// JMS data access for MondayIntegration.razor's Purchasing tab — cross-referencing
/// Monday PO ids against JMS, receiving stock, reassigning a PO line's job, and adding a
/// part to a job. One class because it's all "this page's JMS calls," not because the
/// operations are related to each other (they aren't — different keys, no shared state).
/// Pages keep building their own form payloads and mapping rows to display models; this
/// only runs the query/HTTP call.
/// </summary>
public class MondayPurchasingService
{
    private readonly JmsReadSession _jmsRead;
    private readonly JmsUserSession _jmsSession;

    public MondayPurchasingService(JmsReadSession jmsRead, JmsUserSession jmsSession)
    {
        _jmsRead = jmsRead;
        _jmsSession = jmsSession;
    }

    // ── JMS purchase order cross-reference ──────────────────────────────────

    public async Task<Dictionary<int, JmsPurchaseOrder>> LoadPurchaseOrdersByIdsAsync(IEnumerable<int> poIds)
    {
        var ids = poIds.ToList();
        var result = new Dictionary<int, JmsPurchaseOrder>();
        if (ids.Count == 0) return result;

        var jms = await _jmsRead.GetClientAsync();
        if (jms == null) return result;

        var res = await jms.PostQueryForCsvAsync(JmsQueries.VendorItemOrdersByIdsQuery(ids));
        if (!res.Success) return result;

        var config = WaitingQuoteReport.MakeCsvConfig();
        using var reader = new StringReader(res.Content);
        using var csv = new CsvReader(reader, config);
        var rows = csv.GetRecords<VendorOrderCsvRow>().ToList();

        foreach (var group in rows.GroupBy(r => r.vendor_item_order_id))
        {
            var first = group.First();

            result[group.Key] = new JmsPurchaseOrder
            {
                VendorItemOrderId = group.Key,
                OrderStatus = first.order_status ?? "",
                DateOrdered = ParseDate(first.date_ordered ?? ""),
                DateExpected = ParseDate(first.date_expected ?? ""),
                DateLastDelivered = ParseDate(first.date_last_delivered ?? ""),
                FullyPaid = string.Equals(first.fully_paid, "t", StringComparison.OrdinalIgnoreCase),
                FullyDelivered = string.Equals(first.fully_delivered, "t", StringComparison.OrdinalIgnoreCase),
                VendorName = first.vendor_name ?? "",
                VendorCompanyId = first.vendor_company_id ?? 0,
                JobNumbers = group.Where(r => r.job_no.HasValue).Select(r => r.job_no!.Value).Distinct().ToList(),
                LineCount = group.Count(r => r.order_qty_vuom.HasValue || r.delivered_qty_vuom.HasValue),
                OrderQty = group.Sum(r => r.order_qty_vuom ?? 0),
                DeliveredQty = group.Sum(r => r.delivered_qty_vuom ?? 0),
                InvoicedQty = group.Sum(r => r.invoiced_qty_vuom ?? 0),
                TotalCost = group.Sum(r => r.total_order_cost_sd ?? 0),
                Lines = group
                    .Where(r => r.vendor_item_order_vuom_id.HasValue)
                    .Select(r => new JmsPurchaseOrderLine
                    {
                        VendorItemOrderVuomId = r.vendor_item_order_vuom_id!.Value,
                        MasterItemId = r.master_item_id ?? 0,
                        JobNumber = r.job_no,
                        PartCode = r.vendor_part_code ?? "",
                        PartDescription = r.vendor_part_description ?? "",
                        OrderQty = r.order_qty_vuom ?? 0,
                        DeliveredQty = r.delivered_qty_vuom ?? 0,
                        InvoicedQty = r.invoiced_qty_vuom ?? 0,
                        TotalCost = r.total_order_cost_sd ?? 0,
                        TrackSerial = string.Equals(r.track_serial, "t", StringComparison.OrdinalIgnoreCase)
                    })
                    .ToList()
            };
        }

        return result;
    }

    private static DateTime? ParseDate(string s) => DateTime.TryParse(s, out var d) ? d : null;

    private class VendorOrderCsvRow
    {
        public int vendor_item_order_id { get; set; }
        public string? order_status { get; set; }
        public string? date_ordered { get; set; }
        public string? date_expected { get; set; }
        public string? date_last_delivered { get; set; }
        public string? fully_paid { get; set; }
        public string? fully_delivered { get; set; }
        public string? vendor_name { get; set; }
        public int? vendor_company_id { get; set; }
        public int? vendor_item_order_vuom_id { get; set; }
        public int? master_item_id { get; set; }
        public int? job_no { get; set; }
        public string? vendor_part_code { get; set; }
        public string? vendor_part_description { get; set; }
        public int? order_qty_vuom { get; set; }
        public int? delivered_qty_vuom { get; set; }
        public int? invoiced_qty_vuom { get; set; }
        public decimal? total_order_cost_sd { get; set; }
        public string? track_serial { get; set; }
    }

    // ── Receive items ────────────────────────────────────────────────────────

    /// <summary>Fetches a vendor's not-yet-received items and scrapes the
    /// item_number_N / stock_number_N hidden-input pairs out of the returned HTML
    /// fragment — the same data JMS's own create-docket page injects into #results.</summary>
    public async Task<Dictionary<int, int>> FetchNotYetReceivedPairsAsync(int vendorCompanyId)
    {
        var pairs = new Dictionary<int, int>();

        var jms = await _jmsRead.GetClientAsync();
        if (jms == null) return pairs;

        var res = await jms.GetNotYetReceivedItemsAsync(vendorCompanyId);
        if (!res.Success || string.IsNullOrWhiteSpace(res.Content)) return pairs;

        var doc = new HtmlDocument();
        doc.LoadHtml(res.Content);

        var itemInputs = doc.DocumentNode.SelectNodes("//input[starts-with(@name,'item_number_')]");
        if (itemInputs is null) return pairs;

        foreach (var itemInput in itemInputs)
        {
            var suffix = itemInput.GetAttributeValue("name", "").Replace("item_number_", "");
            var stockInput = doc.DocumentNode.SelectSingleNode($"//input[@name='stock_number_{suffix}']");
            if (stockInput is null) continue;

            if (int.TryParse(itemInput.GetAttributeValue("value", ""), out var itemNumber)
                && int.TryParse(stockInput.GetAttributeValue("value", ""), out var stockNumber))
            {
                pairs[itemNumber] = stockNumber;
            }
        }

        return pairs;
    }

    public async Task<HtmlResponse> SubmitReceiveDocketAsync(Dictionary<string, string> form)
    {
        var jms = await _jmsSession.GetClientAsync();
        if (jms == null) return Unavailable;

        return await jms.CreateDeliveryDocketAsync(form);
    }

    // ── Reassign job ─────────────────────────────────────────────────────────

    /// <summary>Updates vendor_item_order_vuom.job_no then reads it straight back to
    /// confirm — see IJmsDataClient.ExecuteStatementAsync for why the update's own
    /// response can't be trusted.</summary>
    public async Task<int?> UpdateAndVerifyJobNoAsync(int vendorItemOrderVuomId, int newJobNo)
    {
        var jms = await _jmsSession.GetClientAsync();
        if (jms == null) return null;

        await jms.ExecuteStatementAsync(JmsQueries.UpdateVendorItemOrderVuomJobNoQuery(vendorItemOrderVuomId, newJobNo));

        var verify = await jms.PostQueryForCsvAsync(JmsQueries.VendorItemOrderVuomJobNoQuery(vendorItemOrderVuomId));
        return ParseSingleJobNo(verify.Content);
    }

    private class JobNoCsvRow
    {
        public int? job_no { get; set; }
    }

    private static int? ParseSingleJobNo(string? csvContent)
    {
        if (string.IsNullOrWhiteSpace(csvContent)) return null;

        try
        {
            var config = WaitingQuoteReport.MakeCsvConfig();
            using var reader = new StringReader(csvContent);
            using var csv = new CsvReader(reader, config);
            return csv.GetRecords<JobNoCsvRow>().FirstOrDefault()?.job_no;
        }
        catch
        {
            return null;
        }
    }

    // ── Add to job ───────────────────────────────────────────────────────────

    public async Task<List<MondayStockUnit>> LoadAvailableStockUnitsAsync(int vuomId)
    {
        var jms = await _jmsRead.GetClientAsync();
        if (jms == null) return new();

        var res = await jms.PostQueryForCsvAsync(JmsQueries.StockUnitsForLineQuery(vuomId));
        if (!res.Success) return new();

        try
        {
            var config = WaitingQuoteReport.MakeCsvConfig();
            using var reader = new StringReader(res.Content);
            using var csv = new CsvReader(reader, config);
            return csv.GetRecords<StockUnitCsvRow>()
                .Select(r => new MondayStockUnit(r.stock_id, r.serial_no ?? ""))
                .ToList();
        }
        catch
        {
            return new();
        }
    }

    private class StockUnitCsvRow
    {
        public int stock_id { get; set; }
        public string? serial_no { get; set; }
    }

    public async Task<List<MondayEquipmentOption>> LoadEquipmentOptionsAsync(int jobId)
    {
        var jms = await _jmsRead.GetClientAsync();
        if (jms == null) return new();

        try
        {
            var res = await jms.PostQueryForCsvAsync(JmsQueries.EquipmentForJobQuery(jobId));
            if (!res.Success) return new();

            var config = WaitingQuoteReport.MakeCsvConfig();
            using var reader = new StringReader(res.Content);
            using var csv = new CsvReader(reader, config);
            return csv.GetRecords<EquipmentCsvRow>()
                .Select(r => new MondayEquipmentOption(r.equipment_id, r.model_name_no ?? "", r.serial_no ?? ""))
                .ToList();
        }
        catch
        {
            return new();
        }
    }

    private class EquipmentCsvRow
    {
        public int equipment_id { get; set; }
        public string? model_name_no { get; set; }
        public string? serial_no { get; set; }
    }

    /// <summary>Raw JSON from JMS's live part-pricing lookup — the same
    /// call the real "Add Part" form makes. Parsing stays in the page (display shaping).</summary>
    public async Task<string?> GetItemPricingJsonAsync(int masterItemId)
    {
        if (masterItemId <= 0) return null;

        var jms = await _jmsRead.GetClientAsync();
        if (jms == null) return null;

        return await jms.GetItemPricingJsonAsync(masterItemId);
    }

    public async Task<HtmlResponse> SubmitAddPartAsync(int jobId, Dictionary<string, string> form)
    {
        var jms = await _jmsSession.GetClientAsync();
        if (jms == null) return Unavailable;

        return await jms.AddJobPartsAsync(jobId, form);
    }

    private static readonly HtmlResponse Unavailable =
        new(false, 0, "JMS session unavailable.", "", null, null, null, new Dictionary<string, string>());
}
