namespace WorkshopDashboard.Services.Stores;

// Small persisted settings for the automation timesheet logger — currently just the JMS job
// id that general/non-job-specific tasks log against (see
// TimesheetLoggingService.LogAdminTaskAsync). Kept as a named wrapper over SettingsStore so
// callers don't deal in setting keys.
public class TimesheetSettingsStore
{
    internal const string AdminJobIdKey = "Timesheet.AdminJobId";

    private readonly SettingsStore _settings;

    public TimesheetSettingsStore(SettingsStore settings) => _settings = settings;

    public async Task<int?> GetAdminJobIdAsync() =>
        int.TryParse(await _settings.GetAsync(AdminJobIdKey), out var id) ? id : null;

    public Task SetAdminJobIdAsync(int? jobId) =>
        _settings.SetAsync(AdminJobIdKey, jobId?.ToString());
}
