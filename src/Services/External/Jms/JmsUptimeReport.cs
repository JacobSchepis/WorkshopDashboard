using WorkshopDashboard.Models;

namespace WorkshopDashboard.Services.External.Jms;

// Turns the raw outage-period history (JmsUptimeStore) into per-day/range downtime totals for
// the JMS Uptime page's calendar and week/month summaries. Only the 8am-5pm work-day window
// counts — JMS being down at midnight doesn't matter to anyone. A period spanning midnight (or
// running past 5pm into the next day) is split so each calendar day only gets the portion of
// the outage that fell within its own work hours. An ongoing outage (EndedAt null) counts up
// to "now" for total-so-far purposes.
public static class JmsUptimeReport
{
    private static readonly TimeSpan WorkDayStart = TimeSpan.FromHours(8);
    private static readonly TimeSpan WorkDayEnd = TimeSpan.FromHours(17);

    public static Dictionary<DateTime, TimeSpan> DailyDowntime(
        IEnumerable<JmsOutagePeriod> periods, DateTime rangeStart, DateTime rangeEndExclusive, DateTime? now = null)
    {
        var totals = new Dictionary<DateTime, TimeSpan>();
        var clock = now ?? DateTime.Now;

        foreach (var period in periods)
        {
            var (start, end) = ClipToRange(period, rangeStart, rangeEndExclusive, clock);
            if (end <= start) continue;

            for (var day = start.Date; day < end; day = day.AddDays(1))
            {
                var workStart = day + WorkDayStart;
                var workEnd = day + WorkDayEnd;

                var sliceStart = start > workStart ? start : workStart;
                var sliceEnd = end < workEnd ? end : workEnd;
                if (sliceEnd <= sliceStart) continue;

                totals[day] = totals.GetValueOrDefault(day) + (sliceEnd - sliceStart);
            }
        }

        return totals;
    }

    public static TimeSpan TotalDowntime(
        IEnumerable<JmsOutagePeriod> periods, DateTime rangeStart, DateTime rangeEndExclusive, DateTime? now = null) =>
        DailyDowntime(periods, rangeStart, rangeEndExclusive, now).Values.Aggregate(TimeSpan.Zero, (a, b) => a + b);

    private static (DateTime Start, DateTime End) ClipToRange(JmsOutagePeriod period, DateTime rangeStart, DateTime rangeEndExclusive, DateTime clock)
    {
        var start = period.StartedAt;
        var end = period.EndedAt ?? clock;

        if (end <= rangeStart || start >= rangeEndExclusive) return (start, start); // no overlap

        if (start < rangeStart) start = rangeStart;
        if (end > rangeEndExclusive) end = rangeEndExclusive;

        return (start, end);
    }
}
