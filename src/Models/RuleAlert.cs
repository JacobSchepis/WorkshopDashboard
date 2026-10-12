namespace WorkshopDashboard.Models;

// One entry in the "Rule alerts" panel on WorkshopNotifications — the display shape
// RuleCheckService.RunAsync produces for each RuleCheckDefinition that tripped.
// Not persisted anywhere; built fresh on every load.
public class RuleAlert
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Icon { get; set; } = "bi-bell";
    public string Label { get; set; } = "";
    public int Count { get; set; }
    public bool Severe { get; set; }
}
