using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace WorkshopDashboard.Data;

/// <summary>Lets `dotnet ef migrations add` build the context without starting the whole app
/// (which needs the Jms settings and would run the startup import).</summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<WorkshopDbContext>
{
    public WorkshopDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<WorkshopDbContext>().UseSqlite("Data Source=design-time.db").Options);
}
