namespace WorkshopDashboard.Models;

/// <summary>Aggregated view of a vendor_item_order + its vendor_item_order_vuom lines —
/// one of these per PO, built by grouping the line-level rows JmsQueries.VendorItemOrdersByIdsQuery
/// returns (mirrors the job/history grouping pattern used elsewhere in this app).</summary>
public sealed class JmsPurchaseOrder
{
    public int VendorItemOrderId { get; set; }
    public string OrderStatus { get; set; } = "";
    public DateTime? DateOrdered { get; set; }
    public DateTime? DateExpected { get; set; }
    public DateTime? DateLastDelivered { get; set; }
    public bool FullyPaid { get; set; }
    public bool FullyDelivered { get; set; }
    public string VendorName { get; set; } = "";
    public int VendorCompanyId { get; set; }
    public List<int> JobNumbers { get; set; } = new();
    public int LineCount { get; set; }
    public int OrderQty { get; set; }
    public int DeliveredQty { get; set; }
    public int InvoicedQty { get; set; }
    public decimal TotalCost { get; set; }
    public List<JmsPurchaseOrderLine> Lines { get; set; } = new();
}

/// <summary>One vendor_item_order_vuom row — the job it's earmarked for lives at this
/// level (a PO can span multiple jobs), so it has to be shown per line, not on the header.</summary>
public sealed class JmsPurchaseOrderLine
{
    public int VendorItemOrderVuomId { get; set; }
    public int? JobNumber { get; set; }
    public string PartCode { get; set; } = "";
    public string PartDescription { get; set; } = "";
    public int OrderQty { get; set; }
    public int DeliveredQty { get; set; }
    public int InvoicedQty { get; set; }
    public decimal TotalCost { get; set; }

    /// <summary>item_master.track_serial for this line's part — when true, receiving it
    /// requires a serial number per unit (see the Receive modal's per-line serial input).</summary>
    public bool TrackSerial { get; set; }

    /// <summary>vendor_item_order_vuom.master_item_id — the item_master catalog entry,
    /// needed for AddJobParts's live pricing lookup (PartPricingSearch).</summary>
    public int MasterItemId { get; set; }
}
