namespace WorkshopDashboard.Models;

// A "scope customer" groups one or more (often duplicate) company ids under a
// single display name — e.g. a hospital that has several company records in
// JMS. Stored in the database via ScopeCustomerStore.
public class ScopeCustomer
{
    public string DisplayName { get; set; } = "";
    public List<int> CompanyIds { get; set; } = new();
    public string? State { get; set; }

    // Reference company_people rows in JMS rather than storing contact details
    // locally — the contact must already exist in the database. Resolved to a
    // name/email/phone on demand via JmsQueries.ContactDetailsQuery. Primary
    // contacts are the email recipients on Send Email To All; secondary
    // contacts are CC'd.
    public List<int> PrimaryContactIds { get; set; } = new();
    public List<int> SecondaryContactIds { get; set; } = new();

    // Equipment types this customer sends in, e.g. "Flex", "Rigid", "Surg",
    // "Probes" — free text, not a fixed enum, so new categories don't need a
    // code change. Used to filter recipients on Send Email To All.
    public List<string> Tags { get; set; } = new();

    // Exact tab title (case-sensitive) in the shared credit-tracking Google Sheet — credit is
    // no longer managed on JMS, so this is the only link between a scope customer and their
    // credit data. Set by hand here since sheet tab names don't necessarily match JMS company
    // names or this DisplayName. Null/blank means this customer has no credit tab (or isn't
    // tracked in the sheet). See CustomerCreditLookup for how it's read.
    public string? CreditSheetTabName { get; set; }

    // Optional split of this customer's jobs by who looks after them at the hospital (e.g.
    // Flex Scopes / Rigid Scopes / Surgical). Used by the Waiting Quote detail page to filter
    // jobs and send each department its own summary. Empty = the whole customer is one group.
    // See DepartmentForJobContact for how a job is assigned.
    public List<ScopeDepartment> Departments { get; set; } = new();

    // The department a job belongs to: the first one listing the job's contact, otherwise the
    // first catch-all department (no job contacts listed), otherwise none.
    public ScopeDepartment? DepartmentForJobContact(int? jobContactId) =>
        Departments.FirstOrDefault(d => jobContactId is int id && d.JobContactIds.Contains(id))
        ?? Departments.FirstOrDefault(d => d.IsCatchAll);
}

// All contact ids are JMS company_people ids, same as ScopeCustomer's own contacts.
public class ScopeDepartment
{
    public string Name { get; set; } = "";

    // Jobs whose JMS job contact (job.contact_name) is one of these belong to this department.
    // Empty makes it a catch-all: it takes every job no other department claims — which is
    // also how a customer where "every job goes to the same people" is set up.
    public List<int> JobContactIds { get; set; } = new();

    // Summary recipients: primary contacts are emailed directly, secondary contacts are CC'd.
    public List<int> PrimaryContactIds { get; set; } = new();
    public List<int> SecondaryContactIds { get; set; } = new();

    // Our own contact(s) for this department, CC'd on its summaries.
    public List<int> OurContactIds { get; set; } = new();

    // Overrides the customer's CreditSheetTabName when this department's credit is tracked on
    // its own tab. Blank = use the customer's tab.
    public string? CreditSheetTabName { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsCatchAll => JobContactIds.Count == 0;
}
