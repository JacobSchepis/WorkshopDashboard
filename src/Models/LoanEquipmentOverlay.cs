namespace WorkshopDashboard.Models;

// Local, per-unit annotations layered on top of the live loan fleet (queried from JMS by
// LoanEquipmentManager.razor via JmsQueries.LoanEquipmentQuery — every equipment row tagged
// barcode_no = 'LOAN-UNIT' under company 1). JMS has no field for most of these — the workflow
// state and what was paid for the unit are things this team tracks itself, so they're kept here
// rather than in JMS. Model/serial/inspection date/Loan Job/loaned date/agreement-signed all come
// live from JMS instead and are never stored here (loaned date is the first time the loan job
// hit job_status_id 20, "Loan Unit Provided").
// Keyed by JMS's equipment_id (not a locally-generated id) so an entry is meaningless without a
// live equipment row behind it — there's no "add" here, only edits to units JMS already knows
// about. Stored in the database via LoanEquipmentOverlayStore.
public class LoanEquipmentOverlay
{
    public int EquipmentId { get; set; }

    // The Id (not Label) of a LoanEquipmentStatusOption from LoanEquipmentStatusStore's
    // configurable status list — storing the stable Id rather than the display label means
    // renaming a status in "Manage Statuses" doesn't orphan units already set to it.
    public string Status { get; set; } = "";

    // Real JMS company ids (not free text) so these resolve to live company names/details —
    // picked via CompanyPicker, same company search used by RepairerManager/
    // ScopeCustomerManager's company pickers. Status/CurrentLocationCompanyId/LoanedToCompanyId
    // are edited inline in the table; OriginalOwnerCompanyId is edited via the Edit panel.
    public int? CurrentLocationCompanyId { get; set; }
    public int? LoanedToCompanyId { get; set; }
    public int? OriginalOwnerCompanyId { get; set; }

    // What we paid to acquire the unit from its original owner — entered via the Edit
    // panel alongside OriginalOwnerCompanyId.
    public decimal? BuyPrice { get; set; }

    // Job(s) this loan is covering for while the customer's own unit is out for repair — not
    // something JMS tracks (it's a business relationship, not a stored link), so it's a
    // locally-entered list of job ids, edited inline in the table. Their status and quote date
    // are resolved live via JmsQueries.CoveringJobsQuery — see LoanEquipmentManager.razor for
    // the 30-day quote-to-decision clock that starts once one of these jobs is quoted.
    public List<int> CoveringForJobIds { get; set; } = new();
}
