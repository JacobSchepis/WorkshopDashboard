using System.Text.Json;
using WorkshopDashboard.Data;
using WorkshopDashboard.Models;

namespace WorkshopDashboard.Services.Analytics;

// Append-only NDJSON log for AnalyticsEvent (one JSON object per line),
// rotated into one file per month under App_Data/analytics/. Unlike the
// database-backed stores (TurnaroundStatStore etc.), appending never reads
// the file back first — page-view/click volume would make a read-modify-write
// cycle on every event far too slow.
public class AnalyticsWriter
{
    private readonly string _dir;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public AnalyticsWriter(AppDataDirectory appData)
    {
        _dir = Path.Combine(appData.Path, "analytics");
    }

    public async Task AppendAsync(AnalyticsEvent evt)
    {
        var line = JsonSerializer.Serialize(evt);
        var path = FilePathFor(evt.Timestamp);

        await _lock.WaitAsync();
        try
        {
            Directory.CreateDirectory(_dir);
            await File.AppendAllTextAsync(path, line + Environment.NewLine);
        }
        finally
        {
            _lock.Release();
        }
    }

    // Reads every event on/after `since` (default: everything) across all
    // monthly files, for the Usage Analytics report page. Volume here is
    // expected to stay small enough (internal tool, a handful of users) that
    // loading it all into memory to aggregate is fine.
    public async Task<List<AnalyticsEvent>> ReadAllAsync(DateTime? since = null)
    {
        var events = new List<AnalyticsEvent>();
        if (!Directory.Exists(_dir)) return events;

        foreach (var file in Directory.EnumerateFiles(_dir, "events-*.ndjson").OrderBy(f => f))
        {
            foreach (var line in await File.ReadAllLinesAsync(file))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    var evt = JsonSerializer.Deserialize<AnalyticsEvent>(line);
                    if (evt != null && (since == null || evt.Timestamp >= since))
                        events.Add(evt);
                }
                catch (JsonException)
                {
                    // Skip a malformed line (e.g. a partial write) rather than failing the whole report.
                }
            }
        }

        return events;
    }

    private string FilePathFor(DateTime timestamp) =>
        Path.Combine(_dir, $"events-{timestamp:yyyy-MM}.ndjson");
}
