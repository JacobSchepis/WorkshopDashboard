namespace WorkshopDashboard.Models;

// A short staff-posted note shown on the "Team notices" panel of
// WorkshopNotifications — the forklift's out of service, a customer's
// visiting today, that kind of thing. Author is captured from whoever is
// logged in when they add it, not typed by hand. Stored in the database via TeamNoticeStore.
public class TeamNotice
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Text { get; set; } = "";
    public string Author { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
