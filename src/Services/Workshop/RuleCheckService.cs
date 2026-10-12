using WorkshopDashboard.Abstract;
using WorkshopDashboard.Models;
using WorkshopDashboard.Services.Admin;
using WorkshopDashboard.Services.Stores;

namespace WorkshopDashboard.Services.Workshop;

/// <summary>
/// One "notify me if this crosses a threshold" rule. Most rules are just a scope-job status
/// name (StatusName) — they're free, since RuleCheckService fetches DashboardCountsQuery's
/// per-status breakdown once and every StatusName rule reads its own row out of it, rather
/// than each running its own query. ComputeCount is the escape hatch for anything that isn't
/// "how many scope jobs are sitting at status X" — a custom query, an age threshold, a
/// different table, a multi-condition check — given the same jms client and exclusions
/// everything else uses (plus whatever else the definition closes over — see
/// RuleCheckDefinitions.BuildAll's loan-coverage rule for an example needing a local store too).
/// </summary>
public record RuleCheckDefinition(
    string Label,
    string Icon,
    bool Severe,
    int MinCount,
    string? StatusName = null,
    Func<IJmsReader, ScopeJobExclusions, Task<int>>? ComputeCount = null);

/// <summary>
/// The actual list of rules. Add one entry here to add a rule to the "Rule alerts" panel on
/// WorkshopNotifications — that's the whole job for the common "N+ scope jobs sitting at
/// status X" case. No new page, no new query, no new service.
/// </summary>
public static class RuleCheckDefinitions
{
    // A method, not a plain field: the loan-coverage rule below needs LoanEquipmentOverlayStore
    // (local JSON data — which jobs a loan is covering for — not something jms/exclusions alone
    // can answer), closed over as a parameter. Every other rule ignores it. Still just one list
    // to edit to add a rule.
    public static IReadOnlyList<RuleCheckDefinition> BuildAll(LoanEquipmentOverlayStore loans) => new[]
    {
        new RuleCheckDefinition(
            Label: "Jobs preparing a quote",
            Icon: "bi-file-earmark-text",
            Severe: false,
            MinCount: 1,
            StatusName: "Preparing Quote"),

        // LoanEquipmentManager.razor's CoveringForJobIds is a locally-entered list per loan
        // unit — "this loan is standing in while job N's own unit is out for repair". Once job
        // N reaches Completed, the customer's own unit is presumably back/done and the loan can
        // be recalled — nothing currently flags that on its own.
        new RuleCheckDefinition(
            Label: "Loan units covering a completed job",
            Icon: "bi-arrow-return-left",
            Severe: true,
            MinCount: 1,
            ComputeCount: async (jms, _) =>
            {
                var overlays = await loans.LoadAsync();
                var jobIds = overlays.SelectMany(o => o.CoveringForJobIds).Distinct().ToList();
                if (jobIds.Count == 0) return 0;

                var covering = await LoanEquipmentLookup.GetCoveringJobsAsync(jms, jobIds);
                return covering.Count(c => string.Equals(c.JobStatus, "Completed", StringComparison.OrdinalIgnoreCase));
            }),

        // The courtesy loan period is 20 business days (~4 calendar weeks) — flagging at 3
        // weeks (21 calendar days) gives about a week's notice before it's due back, while the
        // job it's covering for still doesn't have a confirmed quote. CoveringJobRow.QuotedDate
        // (despite the name) is the date the job FIRST entered Waiting Quote Confirmation — the
        // same MIN(job_status_history.creation_date) WHERE job_status_id = 10 subquery
        // WaitingQuoteConfirmationQuery uses, and the value LoanEquipmentManager.razor's own
        // "(Xd since quoted)" text already reads — not literally when a quote was made.
        new RuleCheckDefinition(
            Label: "Loan units covering a job waiting on quote confirmation 3+ weeks",
            Icon: "bi-hourglass-split",
            Severe: true,
            MinCount: 1,
            ComputeCount: async (jms, _) =>
            {
                var overlays = await loans.LoadAsync();
                var jobIds = overlays.SelectMany(o => o.CoveringForJobIds).Distinct().ToList();
                if (jobIds.Count == 0) return 0;

                var covering = await LoanEquipmentLookup.GetCoveringJobsAsync(jms, jobIds);
                var cutoff = DateTime.Today.AddDays(-21);

                return covering.Count(c =>
                    string.Equals(c.JobStatus, "Waiting Quote Confirmation", StringComparison.OrdinalIgnoreCase)
                    && DateTime.TryParse(c.QuotedDate, out var firstSetDate)
                    && firstSetDate.Date <= cutoff);
            }),

        // Whole loan fleet, not just units currently covering a job — same "overdue" line
        // LoanEquipmentManager.razor's own inspection-age colour already draws: never
        // inspected, or more than 90 days since the last one, is flagged red there.
        new RuleCheckDefinition(
            Label: "Loan units overdue for inspection",
            Icon: "bi-clipboard2-pulse",
            Severe: true,
            MinCount: 1,
            ComputeCount: async (jms, _) =>
            {
                var equipment = await LoanEquipmentLookup.GetLoanEquipmentAsync(jms);
                if (equipment is null) return 0;

                var cutoff = DateTime.Today.AddDays(-90);
                return equipment.Count(e =>
                    !DateTime.TryParse(e.LastInspectedDate, out var lastInspected) || lastInspected.Date <= cutoff);
            }),
    };
}

/// <summary>
/// Runs every RuleCheckDefinition and returns the ones that tripped, as live RuleAlert rows for
/// WorkshopNotifications's "Rule alerts" panel. Static, matching DashboardLookup's static
/// overload: WorkshopNotifications.razor is a kiosk page (AllowAnonymous, paired by device
/// code) that hands in the IJmsReader from its kiosk base class.
/// </summary>
public static class RuleCheckService
{
    // Overdue isn't used by any rule check today — 0 is a throwaway value for that column,
    // not a real threshold.
    private const int UnusedOverdueDays = 0;

    public static async Task<List<RuleAlert>> RunAsync(IJmsReader jms, ScopeJobExclusions exclusions, LoanEquipmentOverlayStore loans)
    {
        var definitions = RuleCheckDefinitions.BuildAll(loans);
        var alerts = new List<RuleAlert>();

        Dictionary<string, int>? countsByStatus = null;
        if (definitions.Any(d => d.StatusName != null))
        {
            var counts = await DashboardLookup.GetStatusCountsAsync(jms, UnusedOverdueDays, exclusions);
            countsByStatus = counts.ToDictionary(c => c.StatusName, c => c.Total, StringComparer.OrdinalIgnoreCase);
        }

        foreach (var def in definitions)
        {
            int count;
            if (def.StatusName != null)
            {
                count = countsByStatus!.TryGetValue(def.StatusName, out var c) ? c : 0;
            }
            else if (def.ComputeCount != null)
            {
                count = await def.ComputeCount(jms, exclusions);
            }
            else
            {
                continue; // misconfigured — neither a status name nor a custom check
            }

            if (count < def.MinCount) continue;

            alerts.Add(new RuleAlert
            {
                Icon = def.Icon,
                Label = def.Label,
                Count = count,
                Severe = def.Severe
            });
        }

        return alerts;
    }
}
