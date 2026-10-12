namespace WorkshopDashboard.Models;

// One row in the "Average turnaround" panel on WorkshopStatus — e.g. "Quote
// response" / 2.3 days. Manually entered for now, same as RuleAlert — no
// live job_status_history query behind these yet. Stored in the database via TurnaroundStatStore.
public class TurnaroundStat
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Label { get; set; } = "";
    public double Days { get; set; }
}
