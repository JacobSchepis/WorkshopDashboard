namespace WorkshopDashboard.Models;

// A repairer that a job gets assigned to. Most jobs are assigned automatically
// by the model id of their attached equipment; some repairers (e.g. Falcon)
// work on equipment that never gets an individual job_equipment row (dozens of
// small items like scissors wouldn't each be added to the job), so those are
// assigned by an explicit job id override instead. Stored in the database via RepairerStore.
public class Repairer
{
    public string Name { get; set; } = "";
    public List<int> ModelIds { get; set; } = new();
    public List<int> JobIds { get; set; } = new();
}
