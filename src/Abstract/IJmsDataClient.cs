using WorkshopDashboard.Models;
using WorkshopDashboard.Services.External.Jms;

namespace WorkshopDashboard.Abstract;

// Both interfaces are implemented by the one JmsClient. The split is by whose JMS login a
// call runs under, not by topic: IJmsReader is what the shared service account can do
// (JmsReadSession), IJmsDataClient adds everything that must run as the logged-in user
// (JmsUserSession) — writes, and the few reads JMS scopes to the session's own user.

public interface IJmsReader
{
    // ---------------------------
    // SQL query endpoint
    // ---------------------------

    Task<HtmlResponse> PostQueryForCsvAsync(string query);

    /// <summary>Same query, but never answered from CachedJmsReader's fallback cache — for
    /// polling, where last hour's answer would be wrong rather than merely stale.</summary>
    Task<HtmlResponse> PostUncachedQueryForCsvAsync(string query);

    // ---------------------------
    // Lookups
    // ---------------------------

    Task<List<JmsUser>> SearchUsersAsync(string q, CancellationToken ct = default);

    /// <summary>Raw JSON from the live part-pricing lookup the "Add Part" form makes.</summary>
    Task<string?> GetItemPricingJsonAsync(int masterItemId, CancellationToken ct = default);

    /// <summary>HTML fragment listing a vendor's not-yet-received PO items.</summary>
    Task<HtmlResponse> GetNotYetReceivedItemsAsync(int vendorCompanyId, CancellationToken ct = default);

    // ---------------------------
    // Documents
    // ---------------------------

    Task<byte[]?> GetQuotePdfAsync(string fileLocation, string type = "quote", CancellationToken ct = default);
    Task<JobStickerFile?> GetJobStickerPdfAsync(int jobId, int copies = 1, CancellationToken ct = default);
    Task<byte[]> GetWorkSummaryReportAsync(int jobId, int reportId, int companyId, CancellationToken ct = default);
    Task<byte[]> GetNotSeenReportAsync(int jobId, int reportId, int companyId, CancellationToken ct = default);
    Task<byte[]> GetErfaReportAsync(int jobId, int reportId, int companyId, CancellationToken ct = default);
    Task<byte[]> GetCertificateAsync(string certificateFilename, CancellationToken ct = default);
    Task<byte[]> GetDetailedInspectionReportAsync(int inspectionId, CancellationToken ct = default);

    // Monthly site reports (MonthlyReportBuilder) — slow, see JmsClient.ReportTimeout.
    Task<byte[]> GetRsScheduleReportAsync(int companyId, CancellationToken ct = default);
    Task<byte[]> GetJobRepairSummaryReportAsync(int companyId, DateTime date, CancellationToken ct = default);
    Task<byte[]> GetRsPerformanceReportAsync(int companyId, DateTime from, DateTime to, CancellationToken ct = default);

    /// <summary>Submits Job Progress as a JMS background job (the direct download times out for
    /// big sites). Returns whether JMS accepted it (null when JMS is unavailable); the PDF arrives later as a "PDF Complete"
    /// notification — see JmsQueries.JobProgressNotificationQuery.</summary>
    Task<bool?> QueueJobProgressReportAsync(int companyId, string companyName, DateTime from, DateTime to, CancellationToken ct = default);

    /// <summary>Downloads a file by the site-relative path JMS itself handed back (e.g. a
    /// finished background report's notification link). Empty while JMS is unavailable.</summary>
    Task<byte[]> DownloadAsync(string relativePath, CancellationToken ct = default);
}

public interface IJmsDataClient : IJmsReader, IDisposable
{
    // ---------------------------
    // Auth / session
    // ---------------------------

    Task<bool> Login(string username, string password, CancellationToken ct = default);
    Task<bool> VerifyAuthenticationAsync(CancellationToken ct = default);

    // ---------------------------
    // Reads scoped to the session's own user
    // ---------------------------

    Task<string?> GetLastLabourEndTimeAsync(DateTime date, CancellationToken ct = default);

    // ---------------------------
    // Raw SQL writes
    // ---------------------------

    /// <summary>Runs a non-SELECT statement through the query endpoint. The endpoint's own
    /// response is meaningless for these (it always reports failure), so callers verify by
    /// reading the row back.</summary>
    Task ExecuteStatementAsync(string statement);

    // ---------------------------
    // Job service lines
    // ---------------------------

    Task<HtmlResponse> AddJobServiceAsync(int jobId, JobServiceRequest request, CancellationToken ct = default);
    Task<HtmlResponse> UpdateJobServiceAsync(int jobId, int serviceId, JobServiceUpdateRequest request, CancellationToken ct = default);
    Task<HtmlResponse> DeleteJobServiceAsync(int jobId, int serviceId, CancellationToken ct = default);

    // Typed convenience wrappers over AddJobServiceAsync for the three service_type_id
    // values this app actually posts — see JmsClient.Services.cs for the full type table.
    Task<HtmlResponse> AddQuoteLineServiceAsync(int jobId, string description, decimal costPrice, string equipmentId = "", CancellationToken ct = default);
    Task<HtmlResponse> AddFreightServiceAsync(int jobId, string description, decimal costPrice, string equipmentId = "", CancellationToken ct = default);
    Task<HtmlResponse> AddNoteServiceAsync(int jobId, string description, string equipmentId = "", CancellationToken ct = default);

    // ---------------------------
    // Jobs
    // ---------------------------

    Task<HtmlResponse> ChangeJobStatusAsync(int jobId, JobStatus newStatus, string notes = "", int reminderHours = 24, CancellationToken ct = default);
    Task<HtmlResponse> CreateJobAsync(CreateJobRequest request, CancellationToken ct = default);
    Task<HtmlResponse> AddJobEquipmentAsync(AddJobEquipmentRequest request, CancellationToken ct = default);
    Task<HtmlResponse> AddJobTimesheetAsync(AddJobTimesheetRequest request, CancellationToken ct = default);
    Task<HtmlResponse> AddJobPartsAsync(int jobId, IDictionary<string, string> form, CancellationToken ct = default);
    Task<HtmlResponse> UploadJobResourceAsync(int jobId, HttpContent content, CancellationToken ct = default);

    // ---------------------------
    // Other forms
    // ---------------------------

    Task<HtmlResponse> CreateRecallAsync(HttpContent content, CancellationToken ct = default);
    Task<HtmlResponse> CreateDeliveryDocketAsync(IDictionary<string, string> form, CancellationToken ct = default);

    /// <summary>Queues JMS's detailed inspection report as a background job. JMS posts a
    /// notification to the submitting user when it's ready.</summary>
    Task<HtmlResponse> QueueDetailedInspectionReportAsync(int jobId, int reportId, int companyId, CancellationToken ct = default);
}
