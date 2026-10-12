namespace WorkshopDashboard.Models;

public readonly record struct JmsUser(int UserId, string FirstName, string LastName)
{
    public string FullName => $"{FirstName} {LastName}".Trim();
}
