using WorkshopDashboard.Models;

namespace WorkshopDashboard.Services.External.Jms;

// Report and certificate downloads used by InspectionDocumentBuilder's service report.
// All return the raw bytes as JMS served them — an expired session comes back as an HTML
// login page with a 200, so callers check for a PDF themselves.
public sealed partial class JmsClient
{
    public Task<byte[]> GetWorkSummaryReportAsync(int jobId, int reportId, int companyId, CancellationToken ct = default) =>
        SendForBytesAsync(HttpMethod.Post, E.WorkSummaryReport, BuildReportForm(jobId, reportId, companyId, "submit_work"), ct);

    public Task<byte[]> GetNotSeenReportAsync(int jobId, int reportId, int companyId, CancellationToken ct = default) =>
        SendForBytesAsync(HttpMethod.Post, E.NotSeenReport, BuildReportForm(jobId, reportId, companyId, "submit_notseen"), ct);

    public Task<byte[]> GetErfaReportAsync(int jobId, int reportId, int companyId, CancellationToken ct = default) =>
        SendForBytesAsync(HttpMethod.Post, E.ErfaReport, BuildReportForm(jobId, reportId, companyId, "submit_erfa"), ct);

    /// <summary>Same link JMS's equipment page uses, keyed by equipment_certificate.filename.</summary>
    public Task<byte[]> GetCertificateAsync(string certificateFilename, CancellationToken ct = default) =>
        SendForBytesAsync(HttpMethod.Get, $"{E.OpenCertificate}?file={Uri.EscapeDataString(certificateFilename)}", null, ct);

    /// <summary>Same link JMS's inspection Print button ends up on.</summary>
    public Task<byte[]> GetDetailedInspectionReportAsync(int inspectionId, CancellationToken ct = default) =>
        SendForBytesAsync(HttpMethod.Get, $"{E.DetailedInspectionReport}?inspection_ids={inspectionId}", null, ct);

    public async Task<HtmlResponse> QueueDetailedInspectionReportAsync(int jobId, int reportId, int companyId, CancellationToken ct = default)
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent("report_detailed_inspection_cli"), "job_type"   },
            { new StringContent(reportId.ToString()),              "report_id"  },
            { new StringContent(jobId.ToString()),                 "job_id"     },
            { new StringContent(companyId.ToString()),             "company_id" },
            { new StringContent("Detailed Inspection Report"),     "submit_det" },
        };

        return await SendAsync(HttpMethod.Post, E.BackgroundJobSubmission, form, ct);
    }

    /// <summary>Background report files can be big, so these get ReportTimeout.</summary>
    public Task<byte[]> DownloadAsync(string relativePath, CancellationToken ct = default) =>
        SendForBytesAsync(HttpMethod.Get, relativePath, null, ct, ReportTimeout);

    private static MultipartFormDataContent BuildReportForm(int jobId, int reportId, int companyId, string submitName) => new()
    {
        { new StringContent(reportId.ToString()),  "report_id"  },
        { new StringContent(jobId.ToString()),     "job_id"     },
        { new StringContent(companyId.ToString()), "company_id" },
        { new StringContent(submitName),           submitName   },
    };
}
