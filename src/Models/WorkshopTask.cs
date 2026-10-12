namespace WorkshopDashboard.Models;

public enum TaskFrequency
{
    Daily,
    Weekly,
    Monthly
}

// One check-off item on the "Tasks" panel of WorkshopNotifications, grouped
// by Frequency. Done is a plain flag someone toggles — there's no automatic
// reset on a new day/week/month yet, so a completed daily task stays ticked
// until someone unticks it. That's a known gap, not an oversight; add a
// reset job if it turns out to matter in practice. Stored in the database via WorkshopTaskStore.
public class WorkshopTask
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Label { get; set; } = "";
    public TaskFrequency Frequency { get; set; } = TaskFrequency.Daily;
    public bool Done { get; set; }
}
