using CsvHelper;
using CsvHelper.Configuration;
using System.Globalization;
using System.Text;
using System.Text.Json;
using WorkshopDashboard.Models;
using WorkshopDashboard.Services.External.Google;
using WorkshopDashboard.Services.External.Jms;
using WorkshopDashboard.Services.Jobs;
using WorkshopDashboard.Services.Workshop;
using WorkshopDashboard.Services.Admin;
using WorkshopDashboard.Services.Stores;

namespace WorkshopDashboard.Services.Workshop;

// ═══════════════════════════════════════════════════════════════════════════
//  WaitingQuoteReport
//
//  Everything needed to produce a "Waiting Quote / Open Summary" report for a
//  set of company IDs, WITHOUT any UI concerns. Shared by:
//
//    1. WaitingQuote.razor        (dashboard — per-company count cards)
//    2. WaitingQuoteDetail.razor  (interactive page — hide rows, pick send
//       mode, choose quote attachments, then send/copy)
//    3. A future automated email job (build queries → run via Jms → parse →
//       group → BuildReportHtml → Gmail.Compose()...SendAsync())
//
//  The class is static and takes/returns plain data. It does NOT execute
//  queries itself — the caller runs the SQL through its own Jms client.
// ═══════════════════════════════════════════════════════════════════════════

#region Data models

/// <summary>One job in the report, with all its (unaccepted) quotes collapsed onto it.</summary>
public class WqJob
{
    public string JobId { get; set; } = "";
    public int JobStatusId { get; set; }

    /// <summary>Model of the (first) equipment on the job. Used by the
    /// "exclude this model" feature. Nullable because the equipment/model
    /// joins are LEFT JOINs — a job may have no equipment attached.</summary>
    public int? ModelId { get; set; }

    /// <summary>Kept for equipment-name abbreviation lookups.</summary>
    public int? ManufactureId { get; set; }

    public DateTime JobCreated { get; set; }
    public string Equipment { get; set; } = "";
    public string Fault { get; set; } = "";
    public string Status { get; set; } = "";

    // ── Job contact (job.contact_name → company_people) ──────────────────────
    // Used by send-mode 3 (send separately per contact). Nullable because a
    // job may have no contact set, or the person row may be missing.
    public int? ContactId { get; set; }
    public string? ContactName { get; set; }
    public string? ContactEmail { get; set; }

    /// <summary>PO numbers (job_quote.order_no) of every accepted quote on this job, comma-joined,
    /// or null if none is accepted (or the only accepted quote(s) are placeholder "requote"
    /// approvals used to clear the job out of the outstanding-quote list rather than a real
    /// PO — those are excluded entirely, not just flagged).</summary>
    public string? ApprovedPoNumbers { get; set; }

    /// <summary>Latest "ETA - yyyy-MM-dd" note (service_type_id 11), same convention as
    /// WaitingWorkshopProcessingService. Only meaningful once the job is in that status.</summary>
    public DateTime? Eta { get; set; }
    public int? EtaServiceId { get; set; }

    // UI-only — mutated directly by WaitingQuoteDetail.razor's inline ETA editor, same
    // pattern as Status above.
    public DateTime? EtaInput { get; set; }
    public bool SavingEta { get; set; }

    /// <summary>Lifetime invoiced spend on this job's own currently-active equipment
    /// unit(s), across every job those units have ever been on (not just this one) — see
    /// JmsQueries.LifetimeSpendTotalCol's doc comment. Shown on WaitingQuoteDetail.razor's
    /// Pending Quote Approval table behind a page-level toggle, same figure
    /// ModelWatchlist.razor calls "Lifetime repair spend."</summary>
    public decimal TotalSpent { get; set; }
    public int TotalSpentJobs { get; set; }

    public List<WqQuote> Quotes { get; set; } = new();
}

public struct WqQuote
{
    public string QuoteId;

    /// <summary>GST-inclusive total, as stored in job_quote.total_amount.</summary>
    public decimal Price;

    public DateTime DateSent;

    /// <summary>Price with the flat 10% GST component backed out.</summary>
    public readonly decimal PriceExGst => Math.Round(Price / 1.1m, 2, MidpointRounding.AwayFromZero);

    /// <summary>True once the customer has accepted this quote. The report
    /// only ever loads unaccepted quotes (accepted = 'f'), so this is 'false'
    /// for everything currently — kept on the model so the attach UI and any
    /// future "show accepted too" toggle have it available.</summary>
    public bool Accepted;

    /// <summary>Server path to the quote PDF (job_quote.file_location), e.g.
    /// "/media/data/quote/quote-140105-1102.pdf". Fetched via
    /// JmsClient.GetQuotePdfAsync when the user ticks it to attach.</summary>
    public string? FileLocation;
}

public enum WqUrgency { InDate, Medium, Urgent }

/// <summary>One job this loan is standing in for while the customer's own unit is out for
/// repair — resolved from LoanEquipmentOverlay.CoveringForJobIds via
/// LoanEquipmentLookup.GetCoveringJobsAsync's equipment column.</summary>
public record WqLoanCoveringJob(int JobId, string Equipment);

/// <summary>One loan unit currently with a customer, for WaitingQuoteDetail.razor's "Loans"
/// section. Built page-side by merging LoanEquipmentLookup's live JMS data with
/// LoanEquipmentOverlayStore's local annotations (see LoanEquipmentOverlay — "who currently has
/// it" is LoanedToCompanyId, which only exists in the overlay, not in JMS) and filtering to the
/// customer's company ids — WaitingQuoteReport itself doesn't know about loans or JMS.</summary>
public record WqLoan(string Equipment, int? JobId, DateTime? LoanedDate, string StatusLabel, bool AgreementSigned,
    IReadOnlyList<WqLoanCoveringJob>? CoveringFor = null);

#endregion

public static class WaitingQuoteReport
{
    // ── CSV parsing ──────────────────────────────────────────────────────────

    public static List<WqJob> ParseJobs(string csvContent)
    {
        var config = MakeCsvConfig();

        using var reader = new StringReader(csvContent);
        using var csv = new CsvReader(reader, config);

        return csv.GetRecords<JobCsvRow>().ToList()
            .GroupBy(r => r.job_id)
            .Select(g =>
            {
                var first = g.First();
                var eta = ParseEtaNote(first.eta_raw);
                return new WqJob
                {
                    JobId = first.job_id.ToString(),
                    JobStatusId = first.job_status_id,
                    ModelId = first.model_id,
                    ManufactureId = first.manufacture_id,
                    JobCreated = DateTime.TryParse(first.creation_date, out var d) ? d : DateTime.MinValue,
                    Equipment = CleanEquipment(first.equipment, first.fault),
                    Fault = first.fault ?? "",
                    Status = first.job_status ?? "",
                    ContactId = first.contact_id,
                    ContactName = string.IsNullOrWhiteSpace(first.contact_name) ? null : first.contact_name,
                    ContactEmail = string.IsNullOrWhiteSpace(first.contact_email) ? null : first.contact_email,
                    ApprovedPoNumbers = string.IsNullOrWhiteSpace(first.approved_po_numbers) ? null : first.approved_po_numbers,
                    Eta = eta,
                    EtaServiceId = first.eta_service_id,
                    EtaInput = eta,
                    TotalSpent = first.total_spent,
                    TotalSpentJobs = first.total_spent_jobs,
                    Quotes = g
                        .Where(r => r.job_quote_id.HasValue)
                        .Select(r => new WqQuote
                        {
                            QuoteId = r.job_quote_id!.Value.ToString(),
                            Price = r.total_amount ?? 0,
                            DateSent = DateTime.TryParse(r.quote_date, out var qd) ? qd : DateTime.MinValue,
                            Accepted = ParseBool(r.accepted),
                            FileLocation = string.IsNullOrWhiteSpace(r.file_location) ? null : r.file_location
                        })
                        .DistinctBy(q => q.QuoteId)
                        .ToList()
                };
            })
            .ToList();
    }

    /// <summary>Postgres/MySQL 'f'/'t' (and 0/1) → bool.</summary>
    private static bool ParseBool(string? s) =>
        s is not null && (s.Equals("t", StringComparison.OrdinalIgnoreCase) || s == "1"
            || s.Equals("true", StringComparison.OrdinalIgnoreCase));

    public static CsvConfiguration MakeCsvConfig() => new(CultureInfo.InvariantCulture)
    {
        HeaderValidated = null,
        MissingFieldFound = null,
        PrepareHeaderForMatch = args => args.Header.ToLower().Trim()
    };

    private class JobCsvRow
    {
        public int job_id { get; set; }
        public int job_status_id { get; set; }
        public string? creation_date { get; set; }
        public string? fault { get; set; }
        public string? job_status { get; set; }
        public int? model_id { get; set; }
        public int? manufacture_id { get; set; }
        public string? equipment { get; set; }
        public int? contact_id { get; set; }
        public string? contact_name { get; set; }
        public string? contact_email { get; set; }
        public int? job_quote_id { get; set; }
        public decimal? total_amount { get; set; }
        public string? quote_date { get; set; }
        public string? accepted { get; set; }
        public string? file_location { get; set; }
        public string? approved_po_numbers { get; set; }
        public string? eta_raw { get; set; }
        public int? eta_service_id { get; set; }
        public decimal total_spent { get; set; }
        public int total_spent_jobs { get; set; }
    }

    /// <summary>Parses the "ETA - yyyy-MM-dd" note description, same convention as
    /// WaitingWorkshopProcessingService.ParseEta.</summary>
    private static DateTime? ParseEtaNote(string? raw)
    {
        const string marker = "ETA - ";
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var i = raw.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;

        var datePart = raw[(i + marker.Length)..].Trim();
        return DateTime.TryParse(datePart, out var d) ? d : null;
    }

    // ── Grouping for per-contact send (mode 3) ──────────────────────────────

    /// <summary>
    /// Group jobs by their resolved contact for send-mode 3. Jobs with no
    /// contact email are returned under a null-key bucket so the caller can
    /// surface them (they can't be auto-sent). Both the quote list and the
    /// other list are grouped together so each contact gets one email
    /// covering all their jobs.
    /// </summary>
    public static List<ContactGroup> GroupByContact(List<WqJob> quoteJobs, List<WqJob> otherJobs)
    {
        var all = quoteJobs.Select(j => (Job: j, IsQuote: true))
            .Concat(otherJobs.Select(j => (Job: j, IsQuote: false)));

        return all
            .GroupBy(x => x.Job.ContactEmail)
            .Select(g => new ContactGroup
            {
                ContactEmail = g.Key,
                ContactName = g.Select(x => x.Job.ContactName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)),
                QuoteJobs = g.Where(x => x.IsQuote).Select(x => x.Job).ToList(),
                OtherJobs = g.Where(x => !x.IsQuote).Select(x => x.Job).ToList()
            })
            .OrderBy(cg => cg.ContactName ?? "\uffff") // unknown contacts last
            .ToList();
    }

    public class ContactGroup
    {
        public string? ContactEmail { get; set; }
        public string? ContactName { get; set; }
        public List<WqJob> QuoteJobs { get; set; } = new();
        public List<WqJob> OtherJobs { get; set; } = new();

        /// <summary>Can this group actually be emailed? (has a contact address)</summary>
        public bool CanSend => !string.IsNullOrWhiteSpace(ContactEmail);
    }

    // ── Urgency ──────────────────────────────────────────────────────────────

    public static WqUrgency GetUrgency(DateTime quoteDate)
    {
        if (quoteDate == DateTime.MinValue) return WqUrgency.InDate;
        var age = DateTime.Today - quoteDate.Date;
        if (age.TotalDays >= 30) return WqUrgency.Urgent;
        if (age.TotalDays >= 21) return WqUrgency.Medium;
        return WqUrgency.InDate;
    }

    public static DateTime LatestQuoteDate(WqJob job) =>
        job.Quotes.Count > 0 ? job.Quotes.Max(q => q.DateSent) : DateTime.MaxValue;

    // ── Presentation helpers ─────────────────────────────────────────────────

    public static string FriendlyStatus(string raw) =>
        StatusMap.TryGetValue(raw, out var friendly) ? friendly : raw;

    public static string AbbreviateEquipment(string equipment, Dictionary<string, string> abbreviations)
    {
        foreach (var (full, abbrev) in abbreviations)
        {
            if (equipment.StartsWith(full, StringComparison.OrdinalIgnoreCase))
                return abbrev + equipment[full.Length..];
        }
        return equipment;
    }

    /// <summary>
    /// The equipment string is built by SQL as
    /// "manufacture_id model_name_no (serial_no)". When a job has no equipment
    /// attached, the LEFT JOINs leave those columns null and MySQL's CONCAT
    /// collapses to junk like " ()" (or empty). In that case fall back to the
    /// job notes so the row still says something meaningful.
    /// </summary>
    private static string CleanEquipment(string? equipment, string? fault)
    {
        var e = (equipment ?? "").Trim();

        // Strip a lone "()" left behind by a fully-null CONCAT, then re-check.
        var stripped = e.Replace("()", "").Trim();

        if (stripped.Length == 0)
            return string.IsNullOrWhiteSpace(fault) ? "\u2014" : fault.Trim();

        return e;
    }

    private static readonly Dictionary<string, string> StatusMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Waiting Quote Confirmation"] = "Pending Quote Approval",
        ["Preparing Quote"] = "Preparing Quote",
        ["Quote declined (return unrepaired)"] = "Quote Declined \u2013 Return Unrepaired",
        ["Quote declined (waiting customer resp)"] = "Quote Declined \u2013 Awaiting Response",
        ["Quote declined (waiting sales assessment)"] = "Quote Declined \u2013 Sales Assessment",
        ["Waiting Warranty Approval"] = "Pending Warranty Approval",
        ["Waiting More Info"] = "Awaiting More Information",
        ["Waiting Parts"] = "Waiting on Parts",
        ["Waiting Service"] = "Under Repair",
        ["Work In Progress"] = "Under Repair",
        ["Work in progress with Third Party"] = "Third Party Repair in Progress",
        ["With Technician"] = "With Technician",
        ["Unit arrived at Workshop"] = "Received at Workshop",
        ["Unit sent for repair"] = "Sent for Repair",
        ["Unit returned to service"] = "Returned to Service",
        ["Waiting Outward Shipping"] = "Awaiting Outward Shipping",
        ["Waiting Dispatch Booking"] = "Awaiting Dispatch Booking",
        ["Waiting Workshop Assignment"] = "Quotation Delay",
        ["Waiting Workshop Processing"] = "Under Repair",
        ["Assessment Complete"] = "Preparing Quote",
        ["Queued for Assessment"] = "Preparing Quote",
        ["Repair booked with Third Party"] = "Third Party Repair Booked",
        ["Repair on hold"] = "On Hold",
        ["Waiting for arrival at Workshop"] = "In Transit to Workshop",
        ["Unit departed JMS"] = "In transit to customer",
        ["Return Authorisation Sent"] = "Return Authorisation Sent",
        ["Waiting Picking"] = "Awaiting Picking",
        ["Waiting Invoicing"] = "Awaiting Invoicing",
        ["Waiting Invoice Approval"] = "Pending Invoice Approval",
        ["Job Finalisation Request"] = "Finalisation Requested",
        ["Loan Unit Provided"] = "Loan Unit Provided",
        ["Waiting WwaitAssessment"] = "Awaiting Assessment",
        ["Waiting Quality Control Check"] = "Quality Control Check",
        ["Workshop Finalisation Complete"] = "Waiting Quality Control",
        ["Parts Allocated"] = "Parts Allocated",
        ["Waiting Order No."] = "Awaiting Order Number",
    };

    // ── Report HTML ──────────────────────────────────────────────────────────

    /// <summary>
    /// Build the email-ready report HTML for one company.
    /// messageText is the optional note shown above the whole report (the caller has already
    /// filled in {firstName}) — each line becomes a paragraph, HTML-escaped, so plain text
    /// only. includeLifetimeSpend adds a
    /// "Lifetime Spend" column (WqJob.TotalSpent/TotalSpentJobs) to the Pending Quote
    /// Approval table only, not Other Open Jobs — off by default since it's an internal
    /// figure staff opt into per-send, not something every customer report should carry.
    /// The credit summary renders only when includeCreditSummary is set AND credit (read
    /// from the shared Google Sheet by CustomerCreditLookup) is supplied. includeEta drops the
    /// ETA column from Other Open Jobs when false.
    /// </summary>
    public static string BuildReportHtml(string companyName, List<WqJob> quoteJobs, List<WqJob> otherJobs,
        bool includeCreditSummary = false, string? messageText = null,
        bool includeLoans = false, List<WqLoan>? loans = null,
        bool includeLifetimeSpend = false, CustomerCredit? credit = null,
        bool includeEta = true)
    {
        var sb = new StringBuilder();

        sb.Append("<div style=\"font-family:Arial,sans-serif;font-size:14px;color:#1a1a2e;\">");

        if (!string.IsNullOrWhiteSpace(messageText))
        {
            AppendBodyParagraphs(sb, messageText);
            // Gap between the message and the report title below it.
            sb.Append("<div style=\"height:8px;\"></div>");
        }

        sb.Append($"<p style=\"font-size:16px;font-weight:bold;margin:0 0 4px 0;\">{H(companyName)} \u2014 Open Summary Report</p>");
        sb.Append($"<p style=\"font-size:12px;color:#666;margin:0 0 20px 0;\">{DateTime.Today:dd MMMM yyyy}</p>");

        // Every section is always shown, with a "none" line when empty, so a customer can see at a
        // glance that nothing is waiting rather than wondering if a section went missing.
        if (quoteJobs.Count == 0)
        {
            AppendEmptySection(sb, "Pending Quote Approval", "No jobs waiting quote approval.");
        }
        else
        {
            sb.Append(SectionHeadingHtml("Pending Quote Approval"));
            var quoteCols = new List<string> { "Job", "Equipment", "Quote", "Price", "Sent" };
            if (includeLifetimeSpend) quoteCols.Add("Lifetime Spend");

            sb.Append("<table style=\"width:100%;border-collapse:collapse;font-size:13px;margin-bottom:20px;\">");
            sb.Append("<thead><tr style=\"background:#f0f2f5;\">");
            foreach (var col in quoteCols)
                sb.Append($"<th style=\"text-align:left;padding:6px 10px;border:1px solid #d0d4dd;font-size:11px;text-transform:uppercase;\">{col}</th>");
            sb.Append("</tr></thead><tbody>");

            // One row per unaccepted quote (a job with several unapproved
            // quotes shows several rows). Jobs sort by their latest quote date;
            // within a job, quotes sort oldest-first. rowIndex drives the
            // alternating shading across ALL rows, not per job.
            var orderedJobs = quoteJobs.OrderBy(LatestQuoteDate).ToList();
            var td = "padding:6px 10px;border:1px solid #d0d4dd;";
            var rowIndex = 0;

            foreach (var job in orderedJobs)
            {
                if (job.Quotes.Count == 0)
                {
                    // Job waiting quote confirmation but with no unaccepted
                    // quote attached — still list it, with blank quote fields.
                    var rowBg = rowIndex++ % 2 == 1 ? "background:#f8f9fb;" : "";
                    sb.Append($"<tr style=\"{rowBg}\">");
                    sb.Append($"<td style=\"{td}\">{H(job.JobId)}</td>");
                    sb.Append($"<td style=\"{td}\">{H(job.Equipment)}</td>");
                    sb.Append($"<td style=\"{td}\">\u2014</td>");
                    sb.Append($"<td style=\"{td}\">\u2014</td>");
                    sb.Append($"<td style=\"{td}\">\u2014</td>");
                    if (includeLifetimeSpend) sb.Append($"<td style=\"{td}\">{LifetimeSpendCellHtml(job)}</td>");
                    sb.Append("</tr>");
                    continue;
                }

                foreach (var q in job.Quotes.OrderBy(q => q.DateSent))
                {
                    var urgency = GetUrgency(q.DateSent);
                    var rowBg = rowIndex++ % 2 == 1 ? "background:#f8f9fb;" : "";
                    var ageStyle = urgency switch
                    {
                        WqUrgency.Urgent => "color:#dc2626;font-weight:bold;",
                        WqUrgency.Medium => "color:#d97706;",
                        _ => ""
                    };
                    sb.Append($"<tr style=\"{rowBg}\">");
                    sb.Append($"<td style=\"{td}\">{H(job.JobId)}</td>");
                    sb.Append($"<td style=\"{td}\">{H(job.Equipment)}</td>");
                    sb.Append($"<td style=\"{td}\">Q-{H(q.QuoteId)}</td>");
                    sb.Append($"<td style=\"{td}\">${q.Price:N2}</td>");
                    sb.Append($"<td style=\"{td}{ageStyle}\">{q.DateSent.ToString("dd MMM yyyy")}</td>");
                    if (includeLifetimeSpend) sb.Append($"<td style=\"{td}\">{LifetimeSpendCellHtml(job)}</td>");
                    sb.Append("</tr>");
                }
            }
            sb.Append("</tbody></table>");
        }

        if (otherJobs.Count == 0)
        {
            AppendEmptySection(sb, "Other Open Jobs", "No other open jobs.");
        }
        else
        {
            sb.Append(SectionHeadingHtml("Other Open Jobs"));
            sb.Append("<table style=\"width:100%;border-collapse:collapse;font-size:13px;margin-bottom:20px;\">");
            sb.Append("<thead><tr style=\"background:#f0f2f5;\">");
            var otherCols = new List<string> { "Job", "Equipment", "Status", "PO Number" };
            if (includeEta) otherCols.Add("ETA");
            foreach (var col in otherCols)
                sb.Append($"<th style=\"text-align:left;padding:6px 10px;border:1px solid #d0d4dd;font-size:11px;text-transform:uppercase;\">{col}</th>");
            sb.Append("</tr></thead><tbody>");

            var otherRows = otherJobs.OrderBy(j => j.JobId).ToList();
            for (int i = 0; i < otherRows.Count; i++)
            {
                var job = otherRows[i];
                var rowBg = i % 2 == 1 ? "background:#f8f9fb;" : "";
                var td = "padding:6px 10px;border:1px solid #d0d4dd;";
                sb.Append($"<tr style=\"{rowBg}\">");
                sb.Append($"<td style=\"{td}\">{H(job.JobId)}</td>");
                sb.Append($"<td style=\"{td}\">{H(job.Equipment)}</td>");
                sb.Append($"<td style=\"{td}\">{H(FriendlyStatus(job.Status))}</td>");
                sb.Append($"<td style=\"{td}\">{PoNumberCellHtml(job.ApprovedPoNumbers)}</td>");
                if (includeEta) sb.Append($"<td style=\"{td}\">{EtaCellHtml(job)}</td>");
                sb.Append("</tr>");
            }
            sb.Append("</tbody></table>");
        }

        if (includeLoans && loans is not { Count: > 0 })
        {
            AppendEmptySection(sb, "Loan Equipment", "No loan equipment currently with you.");
        }
        else if (includeLoans && loans is not null)
        {
            sb.Append(SectionHeadingHtml("Loan Equipment"));
            sb.Append("<table style=\"width:100%;border-collapse:collapse;font-size:13px;margin-bottom:20px;\">");
            sb.Append("<thead><tr style=\"background:#f0f2f5;\">");
            foreach (var col in new[] { "Equipment", "Job", "Loaned", "Status", "Covering For" })
                sb.Append($"<th style=\"text-align:left;padding:6px 10px;border:1px solid #d0d4dd;font-size:11px;text-transform:uppercase;\">{col}</th>");
            sb.Append("</tr></thead><tbody>");

            for (int i = 0; i < loans.Count; i++)
            {
                var loan = loans[i];
                var rowBg = i % 2 == 1 ? "background:#f8f9fb;" : "";
                var td = "padding:6px 10px;border:1px solid #d0d4dd;";
                sb.Append($"<tr style=\"{rowBg}\">");
                sb.Append($"<td style=\"{td}\">{H(loan.Equipment)}</td>");
                sb.Append($"<td style=\"{td}\">{(loan.JobId.HasValue ? H(loan.JobId.Value.ToString()) : "—")}</td>");
                sb.Append($"<td style=\"{td}\">{(loan.LoanedDate.HasValue ? loan.LoanedDate.Value.ToString("dd MMM yyyy") : "—")}</td>");
                sb.Append($"<td style=\"{td}\">{H(loan.StatusLabel)}</td>");
                sb.Append($"<td style=\"{td}\">{CoveringForCellHtml(loan.CoveringFor)}</td>");
                sb.Append("</tr>");
            }
            sb.Append("</tbody></table>");
        }

        if (includeCreditSummary && credit is not null)
        {
            sb.Append(BuildCreditSummaryHtml(credit));
        }

        sb.Append("</div>");
        return sb.ToString();
    }

    private static string SectionHeadingHtml(string title) =>
        $"<p style=\"font-size:12px;font-weight:bold;text-transform:uppercase;letter-spacing:1px;color:#333;margin:0 0 8px 0;\">{H(title)}</p>";

    /// <summary>A section's heading with a muted "nothing here" box in place of its table.</summary>
    private static void AppendEmptySection(StringBuilder sb, string title, string message)
    {
        sb.Append(SectionHeadingHtml(title));
        sb.Append(EmptyBoxHtml(message));
    }

    private static string EmptyBoxHtml(string message) =>
        $"<div style=\"border:1px dashed #c0c4cd;border-radius:6px;padding:10px 12px;color:#666;font-size:13px;margin-bottom:20px;\">{H(message)}</div>";

    /// <summary>
    /// Each non-blank line of the text as its own paragraph. Renders nothing when blank.
    /// </summary>
    private static void AppendBodyParagraphs(StringBuilder sb, string? bodyText)
    {
        if (string.IsNullOrWhiteSpace(bodyText)) return;

        var para = "font-size:14px;margin:0 0 12px 0;";
        foreach (var line in bodyText.Replace("\r\n", "\n")
                     .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            sb.Append($"<p style=\"{para}\">{H(line)}</p>");
        }
    }

    /// <summary>First word of a full name — "John Smith" → "John". Null when blank.</summary>
    public static string? FirstName(string? fullName) =>
        string.IsNullOrWhiteSpace(fullName)
            ? null
            : fullName.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];

    /// <summary>Credit summary block: the sheet's two totals, then every credit line. Values
    /// are shown exactly as the sheet formats them (they're already currency strings).</summary>
    private static string BuildCreditSummaryHtml(CustomerCredit credit)
    {
        var sb = new StringBuilder();
        sb.Append(SectionHeadingHtml("Credit Summary"));

        var label = "font-size:11px;text-transform:uppercase;color:#666;";
        var value = "font-size:16px;font-weight:bold;";
        sb.Append("<table style=\"border-collapse:collapse;margin-bottom:12px;\"><tr>");
        sb.Append($"<td style=\"padding:0 32px 0 0;\"><div style=\"{label}\">Total Accrued</div><div style=\"{value}\">{CreditCellHtml(credit.TotalAccrued)}</div></td>");
        sb.Append($"<td style=\"padding:0;\"><div style=\"{label}\">Total Remaining</div><div style=\"{value}\">{CreditCellHtml(credit.TotalRemaining)}</div></td>");
        sb.Append("</tr></table>");

        if (credit.Lines.Count == 0)
        {
            sb.Append(EmptyBoxHtml("No credit lines recorded."));
            return sb.ToString();
        }

        sb.Append("<table style=\"width:100%;border-collapse:collapse;font-size:13px;margin-bottom:20px;\">");
        sb.Append("<thead><tr style=\"background:#f0f2f5;\">");
        foreach (var col in new[] { "Serial No.", "Model", "Gained", "Deducted", "Date", "Quote No.", "Purchase Offer", "Balance" })
            sb.Append($"<th style=\"text-align:left;padding:6px 10px;border:1px solid #d0d4dd;font-size:11px;text-transform:uppercase;\">{col}</th>");
        sb.Append("</tr></thead><tbody>");

        var td = "padding:6px 10px;border:1px solid #d0d4dd;";
        for (int i = 0; i < credit.Lines.Count; i++)
        {
            var line = credit.Lines[i];
            var rowBg = i % 2 == 1 ? "background:#f8f9fb;" : "";
            sb.Append($"<tr style=\"{rowBg}\">");
            foreach (var cell in new[] { line.SerialNumber, line.Model, line.CreditGained, line.CreditDeducted,
                                         line.Date, line.QuoteNo, line.PurchaseOfferNo, line.RemainingBalance })
                sb.Append($"<td style=\"{td}\">{CreditCellHtml(cell)}</td>");
            sb.Append("</tr>");
        }
        sb.Append("</tbody></table>");

        return sb.ToString();
    }

    private static string CreditCellHtml(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "—" : H(value);

    /// <summary>"Other Open Jobs" PO Number cell — blank when no quote's accepted yet (or every
    /// accepted quote was a "requote" placeholder, already excluded by the query), otherwise
    /// every genuine accepted quote's PO number.</summary>
    private static string PoNumberCellHtml(string? poNumbers) =>
        string.IsNullOrWhiteSpace(poNumbers) ? "—" : H(poNumbers);

    /// <summary>"Other Open Jobs" ETA cell — only meaningful for jobs Waiting Workshop
    /// Processing; blank for everything else.</summary>
    private static string EtaCellHtml(WqJob job)
    {
        if (job.JobStatusId != (int)JobStatus.WaitingWorkshopProcessing) return "—";
        return job.Eta.HasValue ? job.Eta.Value.ToString("dd MMM yyyy") : "Not set";
    }

    /// <summary>Lifetime invoiced spend on the job's own equipment (WqJob.TotalSpent),
    /// shown when includeLifetimeSpend is on.</summary>
    private static string LifetimeSpendCellHtml(WqJob job) =>
        job.TotalSpentJobs == 0 ? "—" : $"${job.TotalSpent:N2} ({job.TotalSpentJobs} invoice{(job.TotalSpentJobs == 1 ? "" : "s")})";

    /// <summary>"Loans" Covering For cell — the equipment (job) this loan is standing in for
    /// while it's out for repair, blank when the loan isn't covering anything on record.</summary>
    private static string CoveringForCellHtml(IReadOnlyList<WqLoanCoveringJob>? coveringFor) =>
        coveringFor is not { Count: > 0 }
            ? "—"
            : string.Join("<br/>", coveringFor.Select(c => H($"Job {c.JobId} — {c.Equipment}")));

    private static string H(string? s) =>
        string.IsNullOrEmpty(s) ? "" :
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}

// ═══════════════════════════════════════════════════════════════════════════
//  WaitingQuoteEmailConfig
//
//  Config for send-mode 2 ("send all to a predefined person"). Read from
//  wwwroot/data/waiting-quote-email-config.json. Kept dead simple for now —
//  a single default recipient — but shaped so you can grow it into per-company
//  overrides later without changing call sites.
//
//  Register in Program.cs:
//      builder.Services.AddSingleton<WaitingQuoteEmailConfig>();
//
//  Example wwwroot/data/waiting-quote-email-config.json:
//  {
//    "defaultRecipientName": "Alex Example",
//    "defaultRecipientEmail": "alex@example.com",
//    "fromName": "Workshop Dashboard",
//    "fromAddress": "workshop@example.com",
//    "defaultBodyText": "Hi {firstName},\nPlease see your Open Summary Report below."
//  }
//
//  defaultBodyText pre-fills the editable "Message" box on the detail page
//  ({firstName} is filled per email). Use \n in the JSON for paragraph breaks.
// ═══════════════════════════════════════════════════════════════════════════

public class WaitingQuoteEmailConfig
{
    private readonly string _path;
    private readonly ILogger<WaitingQuoteEmailConfig> _logger;

    public WaitingQuoteEmailConfig(IWebHostEnvironment env, ILogger<WaitingQuoteEmailConfig> logger)
    {
        _path = Path.Combine(env.WebRootPath, "data", "waiting-quote-email-config.json");
        _logger = logger;
    }

    public async Task<EmailConfigData> LoadAsync()
    {
        try
        {
            if (!File.Exists(_path))
                return new EmailConfigData();

            var json = await File.ReadAllTextAsync(_path);
            return JsonSerializer.Deserialize<EmailConfigData>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? new EmailConfigData();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not load {Path}", _path);
            return new EmailConfigData();
        }
    }

    public class EmailConfigData
    {
        public string? DefaultRecipientName { get; set; }
        public string? DefaultRecipientEmail { get; set; }
        public string? FromName { get; set; }
        public string? FromAddress { get; set; }

        /// <summary>Pre-fills the "Message" box on the detail page ({firstName} allowed).</summary>
        public string? DefaultBodyText { get; set; }
    }
}
