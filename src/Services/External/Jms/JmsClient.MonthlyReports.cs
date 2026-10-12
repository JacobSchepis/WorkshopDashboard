namespace WorkshopDashboard.Services.External.Jms;

// The per-site reports MonthlyReportBuilder combines. Three are plain GETs that return the PDF
// directly, but JMS can take minutes to build them, so they get ReportTimeout rather than the
// usual DefaultTimeout. Like the other document downloads, an expired session comes back as an
// HTML page with a 200, so the caller checks the bytes are a PDF. Job Progress is too slow even
// for that on big sites, so it goes through JMS's background jobs instead (the form's "Submit
// Background Job" button) and turns up later as a notification.
public sealed partial class JmsClient
{
    public static readonly TimeSpan ReportTimeout = TimeSpan.FromMinutes(10);

    /// <summary>Date format these report pages take.</summary>
    internal const string ReportDateFormat = "yyyy-MM-dd";

    public Task<byte[]> GetRsScheduleReportAsync(int companyId, CancellationToken ct = default) =>
        GetReportAsync($"{E.RsScheduleReport}?cid={companyId}", ct);

    public Task<byte[]> GetJobRepairSummaryReportAsync(int companyId, DateTime date, CancellationToken ct = default) =>
        GetReportAsync($"{E.JobRepairSummaryReport}?company_id={companyId}&date={D(date)}", ct);

    public Task<byte[]> GetRsPerformanceReportAsync(int companyId, DateTime from, DateTime to, CancellationToken ct = default) =>
        GetReportAsync($"{E.RsPerformanceReport}?start_date={D(from)}&end_date={D(to)}&company_id={companyId}", ct);

    /// <summary>Same fields the Job Progress form posts, plus its background job type.</summary>
    public async Task<bool?> QueueJobProgressReportAsync(int companyId, string companyName, DateTime from, DateTime to, CancellationToken ct = default)
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent(companyName),                           "company_name" },
            { new StringContent(companyId.ToString()),                  "company_id"   },
            { new StringContent(from.ToString(ReportDateFormat)),       "start_date"   },
            { new StringContent(to.ToString(ReportDateFormat)),         "end_date"     },
            { new StringContent("report_job_progress_cli"),             "job_type"     },
        };

        var response = await SendAsync(HttpMethod.Post, E.BackgroundJobSubmission, form, ct);
        return response.Success;
    }

    private Task<byte[]> GetReportAsync(string pathAndQuery, CancellationToken ct) =>
        SendForBytesAsync(HttpMethod.Get, pathAndQuery, null, ct, ReportTimeout);

    private static string D(DateTime date) => Uri.EscapeDataString(date.ToString(ReportDateFormat));
}
