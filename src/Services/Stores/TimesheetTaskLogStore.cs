using Microsoft.EntityFrameworkCore;
using WorkshopDashboard.Data;

namespace WorkshopDashboard.Services.Stores;

// Tracks which (technician, task key, day) combinations have already been logged, so
// TimesheetLoggingService's dedupe-guarded calls (e.g. "checked DHL tracking") only ever
// create one timesheet entry per technician per day no matter how many times the triggering
// page/button is hit.
public class TimesheetTaskLogStore
{
    private readonly IDbContextFactory<WorkshopDbContext> _dbFactory;

    public TimesheetTaskLogStore(IDbContextFactory<WorkshopDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<bool> HasLoggedTodayAsync(int technicianUserId, string taskKey)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.TimesheetTaskLog.AnyAsync(x =>
            x.TechnicianUserId == technicianUserId && x.TaskKey == taskKey && x.Date == today);
    }

    public async Task MarkLoggedTodayAsync(int technicianUserId, string taskKey)
    {
        if (await HasLoggedTodayAsync(technicianUserId, taskKey)) return;

        await using var db = await _dbFactory.CreateDbContextAsync();
        db.TimesheetTaskLog.Add(new TimesheetTaskLogEntry
        {
            TechnicianUserId = technicianUserId,
            TaskKey = taskKey,
            Date = DateOnly.FromDateTime(DateTime.Today)
        });
        await db.SaveChangesAsync();
    }
}
