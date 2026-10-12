using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WorkshopDashboard.Data;

namespace WorkshopDashboard.Tests.Fakes;

/// <summary>
/// A private in-memory SQLite database for one test, built from the app's real migrations.
/// The connection stays open for the test's lifetime — an in-memory SQLite database only
/// exists while a connection to it does.
/// </summary>
public sealed class TestDb : IDbContextFactory<WorkshopDbContext>, IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public TestDb()
    {
        _connection.Open();
        using var db = CreateDbContext();
        db.Database.Migrate();
    }

    public WorkshopDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<WorkshopDbContext>().UseSqlite(_connection).Options);

    public void Dispose() => _connection.Dispose();
}
