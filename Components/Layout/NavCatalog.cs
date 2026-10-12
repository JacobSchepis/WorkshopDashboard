using System.Security.Claims;
using WorkshopDashboard.Components.Helpers;

namespace WorkshopDashboard.Components.Layout;

// ═══════════════════════════════════════════════════════════════════════
// NavCatalog — the single list of every tool in the app, grouped into
// sections. NavMenu.razor renders it as the sidebar accordion and Home.razor
// renders it as the launcher grid, so adding a page here puts it in both
// places with the same name and description.
//
// Routes are relative (no leading slash) to match NavLink's href style.
// Drill-down pages (WaitingQuoteDetail, ScopeReportingState/Customer) aren't
// listed — they're only reachable from their parent page.
// ═══════════════════════════════════════════════════════════════════════

public record NavTool(string Title, string Href, string Description);

public record NavSection(string Icon, string Title, string Blurb, IReadOnlyList<NavTool> Tools, bool DevOnly = false);

public static class NavCatalog
{
    public static readonly IReadOnlyList<NavSection> Sections =
    [
        new("📋", "Job Queues", "Where every open job is sitting, oldest first.",
        [
            new("Waiting Workshop Assignment", "waiting-workshop-assignment", "Jobs waiting to be assigned, with live inbound tracking."),
            new("Waiting Workshop Processing", "waiting-workshop-processing", "Jobs being worked on — edit ETAs, approvals and PO status."),
            new("Waiting Quote Confirmation", "waiting-quote-confirmation", "Unapproved quotes waiting on the customer, with follow-up dates."),
            new("Workshop Finalisation Complete", "workshop-finalisation-complete", "Repaired units on their way back to the workshop."),
            new("Stale Jobs", "stale-jobs", "Open jobs with no status change in over a month."),
            new("Stale Scope Jobs", "stale-jobs-filtered", "Stale jobs with excluded Scope models hidden."),
        ]),
        new("📄", "Quoting", "Turn repairer quotes into JMS quotes, and chase the ones outstanding.",
        [
            new("Dorado Quote", "quote-dorado", "Rigid / semi-rigid / specialty scope repair quotes."),
            new("Falcon Quote", "quote-falcon", "Per-instrument surgical repair quotes."),
            new("Apex Quote", "quote-apex", "Flexible endoscope repair quotes."),
            new("In-house - Flex", "quote-inhouse-flex", "Our own flex repairs, priced from Apex's price list."),
            new("In-house - Rigid", "quote-inhouse-rigid", "Our own rigid repairs, priced like a Dorado rigid quote."),
            new("Apex Price Sheet", "apex-price-sheet", "Apex's flex repair prices, for pricing in-house flex quotes."),
            new("Cobalt Quote", "quote-cobalt", "Cobalt quotes, converted from USD."),
            new("Ember Quote", "quote-ember", "Ember transducer repair quotes."),
            new("Bayside Quote", "quote-bayside", "Bayside quotes, converted from USD."),
            new("Quotes Pending", "waiting-quote", "Open quotes per customer, with a printable summary."),
            new("Quote Config", "quote-config", "Notices the quote pages offer, and their print order."),
        ]),
        new("🔧", "Repairs", "Create, update and ship repair jobs.",
        [
            new("Repair Request", "international-shipping", "Customs info, DHL CSV and repair-request PDFs for a job."),
            new("Bulk Create Jobs", "bulk-create-jobs", "Create several jobs for one owner in one go."),
            new("Bulk Update", "bulk-status-update", "Apply one status change to a list of jobs."),
            new("Upload Document", "upload-document", "Attach a file to a job and optionally change its status."),
            new("Loan Equipment", "loan-equipment", "The loan fleet — where each unit is and who has it."),
            new("Customs Info Manager", "customs-info-manager", "Manufacturer / 510(k) data used for customs paperwork."),
        ]),
        new("🚚", "Shipping", "Track units moving in and out.",
        [
            new("Shipping Checks", "shipping-checks", "Arrivals, outward shipments and departed units with live tracking."),
        ]),
        new("👥", "Customers", "Scope customer records and customer communication.",
        [
            new("Scope Customer Manager", "scope-customers", "Customer groups, contacts, states and tags."),
            new("Customer Credit", "customer-credit", "A customer's credit, read live from the shared sheet."),
            new("Send Email To All", "send-customer-email", "Bulk email scope customer contacts by state or tag."),
            new("Recalls", "bulk-create-recall", "Import a TGA recall export into JMS."),
        ]),
        new("🔭", "Scope Management", "Which jobs, models and repairers count as Scope.",
        [
            new("Scope Job Manager", "scope-jobs", "The exclusion list every Scope page filters against."),
            new("Repairer Manager", "repairer-manager", "Repairers and which models / jobs they handle."),
            new("Model Watchlist", "model-watchlist", "Most-repaired models and buy-back opportunities."),
        ]),
        new("📊", "Reporting", "Reports and stats.",
        [
            new("Service Report", "inspection-equipment-report", "Check a job's inspections and generate its service report PDF."),
            new("Scope Reporting", "scope-reporting", "Invoiced, paid and job stats by state and customer."),
            new("Repairer Reporting", "repairer-reporting", "Job counts and turnaround per repairer."),
            new("Monthly Reports", "monthly-reports", "Each site's monthly report pack, built on the 1st."),
            new("Monthly Report Sites", "monthly-report-sites", "Which hospitals get the monthly report pack."),
            new("JMS Uptime", "jms-uptime", "JMS outage history by day."),
        ]),
        new("🗓️", "Monday.com", "The purchasing board, cross-checked against JMS.",
        [
            new("Purchasing", "monday", "Monday purchasing items matched to JMS purchase orders."),
        ]),
        new("📺", "Workshop TVs", "The wall-mounted workshop screens and what they show.",
        [
            new("Status Monitor", "workshop-dashboard", "Live job counts and throughput for the workshop TV."),
            new("Notifications Monitor", "workshop-notifications", "Team notices and checklist for the second workshop TV."),
            new("Pair Device", "pair-device", "Approve the code shown on a new workshop screen."),
            new("Turnaround Config", "turnaround-config", "Average turnaround figures shown on the status monitor."),
            new("Team Board", "team-board", "Notices and checklist shown on the notifications monitor."),
        ]),
        new("🔒", "Dev Tools", "Developer-only scratchpads.",
        [
            new("Database Test", "database-test", "Run raw SQL against JMS."),
            new("Usage Analytics", "usage-analytics", "Who's using which pages, and the time it's saved."),
            new("Timesheet Test", "timesheet-test", "Check timesheet logging against live jobs."),
            new("Repair Suggestion Test", "repair-suggest-test", "Paste an inspection note, see which Apex price lines it suggests."),
        ], DevOnly: true),
    ];

    public static IEnumerable<NavSection> VisibleTo(ClaimsPrincipal user) =>
        Sections.Where(s => !s.DevOnly || DevOnlyComponentBase.IsDeveloper(user.Identity?.Name));
}
