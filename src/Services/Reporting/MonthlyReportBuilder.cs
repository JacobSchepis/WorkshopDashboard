using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CsvHelper;
using WorkshopDashboard.Abstract;
using WorkshopDashboard.Services.External.Jms;
using WorkshopDashboard.Util;

namespace WorkshopDashboard.Services.Reporting;

/// <summary>
/// Builds one site's monthly pack: four JMS reports for the month, merged into one PDF in
/// this order —
///   1. RS Progress (the site's routine service schedule, as it stands now)
///   2. Job Repair Summary (as at the last day of the month)
///   3. Job Progress (first to last day of the month)
///   4. RS Performance (first to last day of the month)
/// Everything runs as the service account (JmsReadSession — the default user's login). The
/// direct downloads can take minutes (JmsClient.ReportTimeout); Job Progress times out even
/// then for big sites, so it's submitted as a JMS background job and collected from its
/// "PDF Complete" notification once JMS has built it.
/// </summary>
public class MonthlyReportBuilder
{
    private readonly JmsReadSession _jmsRead;

    public MonthlyReportBuilder(JmsReadSession jmsRead) => _jmsRead = jmsRead;

    /// <summary>How often to check whether the Job Progress background job has finished.</summary>
    internal TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>How long to wait for the Job Progress background job before giving up.</summary>
    internal TimeSpan MaxBackgroundWait { get; set; } = TimeSpan.FromHours(1);

    internal static readonly string[] PartNames = { "RS Progress", "Job Repair Summary", "Job Progress", "RS Performance" };

    /// <summary>First and last day of the month <paramref name="month"/> falls in.</summary>
    public static (DateTime First, DateTime Last) MonthRange(DateTime month)
    {
        var first = new DateTime(month.Year, month.Month, 1);
        return (first, first.AddMonths(1).AddDays(-1));
    }

    /// <summary>Fetches the four reports and merges them. Throws with the failing report's name
    /// if JMS is unavailable, returns something that isn't a PDF, or never finishes Job Progress.</summary>
    public async Task<byte[]> BuildAsync(int companyId, string companyName, DateTime month,
        Func<string, Task>? onStep = null, CancellationToken ct = default)
    {
        var jms = await _jmsRead.GetClientAsync(ct) ?? throw new Exception("JMS session unavailable.");
        var (first, last) = MonthRange(month);
        onStep ??= _ => Task.CompletedTask;

        var pdfs = new List<byte[]>();
        for (var i = 0; i < PartNames.Length; i++)
        {
            var name = PartNames[i];
            var step = $"{name} ({i + 1}/{PartNames.Length})";
            await onStep(step);

            var bytes = i switch
            {
                0 => await DirectAsync(name, () => jms.GetRsScheduleReportAsync(companyId, ct), ct),
                1 => await DirectAsync(name, () => jms.GetJobRepairSummaryReportAsync(companyId, last, ct), ct),
                2 => await BackgroundJobProgressAsync(jms, companyId, companyName, first, last, s => onStep($"{step} — {s}"), ct),
                _ => await DirectAsync(name, () => jms.GetRsPerformanceReportAsync(companyId, first, last, ct), ct),
            };

            pdfs.Add(CheckPdf(name, bytes));
        }

        await onStep("Combining");
        return PdfTools.MergePdfs(pdfs);
    }

    private static async Task<byte[]> DirectAsync(string name, Func<Task<byte[]>> fetch, CancellationToken ct)
    {
        try
        {
            return await fetch();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new Exception($"{name}: JMS didn't respond within {JmsClient.ReportTimeout.TotalMinutes:0} minutes.");
        }
        catch (HttpRequestException ex) when (ex.StatusCode == null)
        {
            // Couldn't connect at all, as opposed to JMS answering with an error status.
            throw new JmsUnavailableException($"{name}: couldn't reach JMS ({ex.Message}).");
        }
    }

    private async Task<byte[]> BackgroundJobProgressAsync(IJmsReader jms, int companyId, string companyName,
        DateTime first, DateTime last, Func<string, Task> onStep, CancellationToken ct)
    {
        const string name = "Job Progress";

        // Note the newest notification first, so an older "PDF Complete" for the same site
        // (last month's, or one run by hand) can't be mistaken for this one.
        var latest = await jms.PostUncachedQueryForCsvAsync(JmsQueries.LatestAnyNotificationIdQuery());
        if (!latest.Success)
            throw new JmsUnavailableException($"{name}: couldn't reach JMS to read its notifications.");
        if (ReadFirstRow(latest.Content) is not { } maxRow || !int.TryParse(maxRow[0], out var afterId))
            throw new Exception($"{name}: couldn't read JMS's notifications before submitting.");

        await onStep("submitting to JMS");
        switch (await jms.QueueJobProgressReportAsync(companyId, companyName, first, last, ct))
        {
            case null: throw new JmsUnavailableException($"{name}: JMS is unavailable.");
            case false: throw new Exception($"{name}: JMS didn't accept the background job.");
        }

        // JMS builds it whether or not we can see it, so a blip mid-wait just means checking
        // again. Only if JMS is still unreachable when time's up is it treated as an outage.
        var started = DateTime.UtcNow;
        while (true)
        {
            var waited = DateTime.UtcNow - started;
            await onStep($"waiting for JMS ({(int)waited.TotalMinutes}m)");

            var reachable = false;
            try
            {
                var poll = await jms.PostUncachedQueryForCsvAsync(JmsQueries.JobProgressNotificationQuery(companyName, afterId));
                reachable = poll.Success;
                if (poll.Success && ReadFirstRow(poll.Content) is { } row && !string.IsNullOrWhiteSpace(row[0]))
                {
                    await onStep("downloading");
                    return await jms.DownloadAsync(row[0].Trim(), ct);
                }
            }
            catch (HttpRequestException ex) when (ex.StatusCode == null)
            {
                // unreachable this time round
            }

            if (waited >= MaxBackgroundWait)
                throw reachable
                    ? new Exception($"{name}: JMS's background job didn't finish within {MaxBackgroundWait.TotalMinutes:0} minutes.")
                    : new JmsUnavailableException($"{name}: JMS went down while building it.");
            await Task.Delay(PollInterval, ct);
        }
    }

    // First data row of a query result, or null if there isn't one. Real CSV parsing — the
    // notification message itself contains a comma.
    private static string[]? ReadFirstRow(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return null;
        using var reader = new StringReader(csv);
        using var parser = new CsvReader(reader, CultureInfo.InvariantCulture);
        if (!parser.Read() || !parser.ReadHeader() || !parser.Read()) return null;
        return parser.Parser.Record;
    }

    /// <summary>Returns an importable copy of the report, or throws explaining what came back instead.</summary>
    internal static byte[] CheckPdf(string name, byte[] bytes)
    {
        if (bytes.Length == 0)
            throw new JmsUnavailableException($"{name}: JMS is unavailable.");
        if (!PdfTools.LooksLikePdf(bytes))
            throw new Exception($"{name}: JMS didn't return a PDF — {Snippet(bytes)}");

        try
        {
            return PdfTools.EnsureImportable(bytes);
        }
        catch (Exception ex)
        {
            throw new Exception($"{name}: the PDF couldn't be read ({ex.Message}).");
        }
    }

    // What JMS said instead of a PDF (usually a login page or an error), as plain text.
    private static string Snippet(byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 4000));
        text = Regex.Replace(text, "<(script|style)[^>]*>.*?</\\1>", " ", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<[^>]+>", " ");
        text = Regex.Replace(System.Net.WebUtility.HtmlDecode(text), "\\s+", " ").Trim();
        return text.Length == 0 ? "empty page." : text.Length <= 200 ? text : text[..200] + "…";
    }
}

/// <summary>JMS couldn't be reached — an outage, not a problem with the pack itself, so
/// MonthlyReportService waits for JMS rather than using up one of the pack's attempts.</summary>
public class JmsUnavailableException : Exception
{
    public JmsUnavailableException(string message) : base(message) { }
}
