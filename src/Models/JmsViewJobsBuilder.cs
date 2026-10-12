namespace WorkshopDashboard.Models;

public sealed class JmsViewJobsBuilder
{
    private JmsViewJobsFilters _filters;

    public JmsViewJobsBuilder()
    {
        _filters = new JmsViewJobsFilters();
    }

    public static JmsViewJobsBuilder Create() => new JmsViewJobsBuilder();

    public FormUrlEncodedContent Build() => new FormUrlEncodedContent(new Dictionary<string, string>
    {
        ["owner"] = _filters.Owner,
        ["bill_to"] = _filters.BillTo,
        ["ship_to"] = _filters.ShipTo,
        ["job_id"] = _filters.JobID,
        ["rs_number"] = _filters.RsNumber,
        ["serial_number"] = _filters.SerialNumber,
        ["asset_number"] = _filters.AssetNumber,
        ["invoice_number"] = _filters.InvoiceNumber,
        ["quote_number"] = _filters.QuoteNumber,
        ["order_number"] = _filters.OrderNumber,
        ["user"] = _filters.User,
        ["job_status[]"] = _filters.JobStatus,
        ["not_completed"] = _filters.NotCompleted,
        ["for_workshop_repair"] = _filters.ForWorkshopRepair,
        ["manufacturer"] = _filters.Manufacturer,
        ["job_notes"] = _filters.JobNotes,
        ["model_name"] = _filters.ModelName,
        ["creation_date_start"] = _filters.CreationDateStart,
        ["creation_date_end"] = _filters.CreationDateEnd,
        ["booked_date_start"] = _filters.BookedDateStart,
        ["booked_date_end"] = _filters.BookedDateEnd,
        ["start_date_start"] = _filters.StartDateStart,
        ["start_date_end"] = _filters.StartDateEnd,
        ["complete_date_start"] = _filters.CompleteDateStart,
        ["complete_date_end"] = _filters.CompleteDateEnd,
        ["quote_date_start"] = _filters.QuoteDateStart,
        ["quote_date_end"] = _filters.QuoteDateEnd,
        ["invoiced_date_start"] = _filters.InvoicedDateStart,
        ["invoiced_date_end"] = _filters.InvoicedDateEnd,
        ["status_date_start"] = _filters.StatusDateStart,
        ["status_date_end"] = _filters.StatusDateEnd,
        ["submit"] = _filters.Submit,
        ["limit"] = _filters.Limit
    });

    // --- helpers ---
    private static string S(string? value) => value?.Trim() ?? "";

    // --- string filters ---
    public JmsViewJobsBuilder WithOwner(string? owner) { _filters.Owner = S(owner); return this; }
    public JmsViewJobsBuilder WithBillTo(string? billTo) { _filters.BillTo = S(billTo); return this; }
    public JmsViewJobsBuilder WithShipTo(string? shipTo) { _filters.ShipTo = S(shipTo); return this; }
    public JmsViewJobsBuilder WithJobId(string? jobId) { _filters.JobID = S(jobId); return this; }
    public JmsViewJobsBuilder WithRsNumber(string? rsNumber) { _filters.RsNumber = S(rsNumber); return this; }
    public JmsViewJobsBuilder WithSerialNumber(string? serialNumber) { _filters.SerialNumber = S(serialNumber); return this; }
    public JmsViewJobsBuilder WithAssetNumber(string? assetNumber) { _filters.AssetNumber = S(assetNumber); return this; }
    public JmsViewJobsBuilder WithInvoiceNumber(string? invoiceNumber) { _filters.InvoiceNumber = S(invoiceNumber); return this; }
    public JmsViewJobsBuilder WithQuoteNumber(string? quoteNumber) { _filters.QuoteNumber = S(quoteNumber); return this; }
    public JmsViewJobsBuilder WithOrderNumber(string? orderNumber) { _filters.OrderNumber = S(orderNumber); return this; }
    public JmsViewJobsBuilder WithUser(string? user) { _filters.User = S(user); return this; }
    public JmsViewJobsBuilder WithNotCompleted(bool notCompleted) { _filters.NotCompleted = notCompleted ? "t" : ""; return this; }
    public JmsViewJobsBuilder WithJobStatus(JobStatus jobStatus) { _filters.JobStatus = ((int)jobStatus).ToString(); return this; }
    public JmsViewJobsBuilder WithManufacturer(string? manufacturer) { _filters.Manufacturer = S(manufacturer); return this; }
    public JmsViewJobsBuilder WithJobNotes(string? jobNotes) { _filters.JobNotes = S(jobNotes); return this; }
    public JmsViewJobsBuilder WithModelName(string? modelName) { _filters.ModelName = S(modelName); return this; }
    public JmsViewJobsBuilder WithSubmit(string? submit) { _filters.Submit = S(submit); return this; }
    public JmsViewJobsBuilder WithLimit(string? limit) { _filters.Limit = S(limit); return this; }
    public JmsViewJobsBuilder WithForWorkshopRepair(string? forWorkshopRepair) { _filters.ForWorkshopRepair = S(forWorkshopRepair); return this; }

    // --- date ranges (string-based, since your struct is strings) ---
    public JmsViewJobsBuilder WithCreationDateRange(string? start, string? end)
    {
        _filters.CreationDateStart = S(start);
        _filters.CreationDateEnd = S(end);
        return this;
    }

    public JmsViewJobsBuilder WithBookedDateRange(string? start, string? end)
    {
        _filters.BookedDateStart = S(start);
        _filters.BookedDateEnd = S(end);
        return this;
    }

    public JmsViewJobsBuilder WithStartDateRange(string? start, string? end)
    {
        _filters.StartDateStart = S(start);
        _filters.StartDateEnd = S(end);
        return this;
    }

    public JmsViewJobsBuilder WithCompleteDateRange(string? start, string? end)
    {
        _filters.CompleteDateStart = S(start);
        _filters.CompleteDateEnd = S(end);
        return this;
    }

    public JmsViewJobsBuilder WithQuoteDateRange(string? start, string? end)
    {
        _filters.QuoteDateStart = S(start);
        _filters.QuoteDateEnd = S(end);
        return this;
    }

    public JmsViewJobsBuilder WithInvoicedDateRange(string? start, string? end)
    {
        _filters.InvoicedDateStart = S(start);
        _filters.InvoicedDateEnd = S(end);
        return this;
    }

    public JmsViewJobsBuilder WithStatusDateRange(string? start, string? end)
    {
        _filters.StatusDateStart = S(start);
        _filters.StatusDateEnd = S(end);
        return this;
    }

    // --- optional: granular setters for dates if you prefer ---
    public JmsViewJobsBuilder WithCreationDateStart(string? start) { _filters.CreationDateStart = S(start); return this; }
    public JmsViewJobsBuilder WithCreationDateEnd(string? end) { _filters.CreationDateEnd = S(end); return this; }

    public JmsViewJobsBuilder WithBookedDateStart(string? start) { _filters.BookedDateStart = S(start); return this; }
    public JmsViewJobsBuilder WithBookedDateEnd(string? end) { _filters.BookedDateEnd = S(end); return this; }

    public JmsViewJobsBuilder WithStartDateStart(string? start) { _filters.StartDateStart = S(start); return this; }
    public JmsViewJobsBuilder WithStartDateEnd(string? end) { _filters.StartDateEnd = S(end); return this; }

    public JmsViewJobsBuilder WithCompleteDateStart(string? start) { _filters.CompleteDateStart = S(start); return this; }
    public JmsViewJobsBuilder WithCompleteDateEnd(string? end) { _filters.CompleteDateEnd = S(end); return this; }

    public JmsViewJobsBuilder WithQuoteDateStart(string? start) { _filters.QuoteDateStart = S(start); return this; }
    public JmsViewJobsBuilder WithQuoteDateEnd(string? end) { _filters.QuoteDateEnd = S(end); return this; }

    public JmsViewJobsBuilder WithInvoicedDateStart(string? start) { _filters.InvoicedDateStart = S(start); return this; }
    public JmsViewJobsBuilder WithInvoicedDateEnd(string? end) { _filters.InvoicedDateEnd = S(end); return this; }

    public JmsViewJobsBuilder WithStatusDateStart(string? start) { _filters.StatusDateStart = S(start); return this; }
    public JmsViewJobsBuilder WithStatusDateEnd(string? end) { _filters.StatusDateEnd = S(end); return this; }

    // --- optional: reset ---
    public JmsViewJobsBuilder Clear()
    {
        _filters = new JmsViewJobsFilters();
        return this;
    }
}


public struct JmsViewJobsFilters
{
    public JmsViewJobsFilters() {}

    public string Owner = "";
    public string BillTo = "";
    public string ShipTo = "";
    public string JobID = "";
    public string RsNumber = "";
    public string SerialNumber = "";
    public string AssetNumber = "";
    public string InvoiceNumber = "";
    public string QuoteNumber = "";
    public string OrderNumber = "";
    public string User = "";
    public string NotCompleted = "";
    public string JobStatus = "";
    public string ForWorkshopRepair = "";
    public string Manufacturer = "";
    public string JobNotes = "";
    public string ModelName = "";
    public string CreationDateStart = "";
    public string CreationDateEnd = "";
    public string BookedDateStart = "";
    public string BookedDateEnd = "";
    public string StartDateStart = "";
    public string StartDateEnd = "";
    public string CompleteDateStart = "";
    public string CompleteDateEnd = "";
    public string QuoteDateStart = "";
    public string QuoteDateEnd = "";
    public string InvoicedDateStart = "";
    public string InvoicedDateEnd = "";
    public string StatusDateStart = "";
    public string StatusDateEnd = "";
    public string Submit = "";
    public string Limit =  "";
}
