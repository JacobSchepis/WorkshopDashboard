namespace WorkshopDashboard.Models;

// One continuous stretch of JMS not responding. EndedAt is null while the
// outage is still ongoing.
public class JmsOutagePeriod
{
    public DateTime StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
}
