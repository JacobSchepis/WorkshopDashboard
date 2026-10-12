using Microsoft.Extensions.Options;

namespace WorkshopDashboard.Services.External.Jms;

// ═══════════════════════════════════════════════════════════════════════════
//  JmsOptions
//
//  Everything that identifies the JMS site — its base URL, every page this
//  app posts to, and the service account used for reads. Bound from the "Jms"
//  configuration section (Secrets/secrets.json, user secrets or environment
//  variables), so no JMS address ever lives in source. See
//  Secrets.example/secrets.json for the shape.
//
//  Endpoint values are paths relative to BaseUrl, without query strings —
//  JmsClient appends any ?id=... itself.
// ═══════════════════════════════════════════════════════════════════════════

public class JmsOptions
{
    public const string SectionName = "Jms";

    public string BaseUrl { get; set; } = "";

    /// <summary>Where links for people (JmsLinks) point, when that isn't BaseUrl — demo mode
    /// sends them to the app's own stand-in job page. Usually left unset.</summary>
    public string? LinkBaseUrl { get; set; }

    /// <summary>Coordinates the login form posts alongside the credentials.</summary>
    public string LoginLatitude { get; set; } = "";
    public string LoginLongitude { get; set; } = "";

    /// <summary>How long any single JMS request may take before it counts as JMS being unreachable.</summary>
    public int RequestTimeoutSeconds { get; set; } = 15;

    public JmsServiceAccount ServiceAccount { get; set; } = new();
    public JmsEndpoints Endpoints { get; set; } = new();

    public Uri BaseUri => new(BaseUrl.EndsWith('/') ? BaseUrl : BaseUrl + "/");
}

/// <summary>The shared login every read goes through (see JmsReadSession).</summary>
public class JmsServiceAccount
{
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
}

public class JmsEndpoints
{
    // ── Auth ──
    public string Login { get; set; } = "";
    /// <summary>Any page that bounces to Login when the session has expired.</summary>
    public string AuthCheck { get; set; } = "";

    // ── Reads ──
    /// <summary>Raw SQL in, CSV out.</summary>
    public string Query { get; set; } = "";
    public string OpenFile { get; set; } = "";
    public string UserSearch { get; set; } = "";
    public string JobSticker { get; set; } = "";
    public string PartPricingSearch { get; set; } = "";
    public string NotYetReceivedItems { get; set; } = "";
    public string WorkSummaryReport { get; set; } = "";
    public string NotSeenReport { get; set; } = "";
    public string ErfaReport { get; set; } = "";
    public string DetailedInspectionReport { get; set; } = "";
    public string OpenCertificate { get; set; } = "";

    // ── Monthly site reports (MonthlyReportBuilder) — each returns a PDF, sometimes after minutes; Job Progress goes through BackgroundJobSubmission instead ──
    public string RsScheduleReport { get; set; } = "";
    public string JobRepairSummaryReport { get; set; } = "";
    public string RsPerformanceReport { get; set; } = "";

    // ── User-session reads (scoped to whoever is logged in) ──
    public string LabourConflictCheck { get; set; } = "";

    // ── Writes ──
    /// <summary>Job page — also the target of the change-status form, and what job links open.</summary>
    public string JobSummary { get; set; } = "";
    public string CreateJob { get; set; } = "";
    public string AddJobEquipment { get; set; } = "";
    public string AddJobTimesheet { get; set; } = "";
    public string AddJobService { get; set; } = "";
    public string UpdateJobService { get; set; } = "";
    public string AddJobParts { get; set; } = "";
    public string CreateRecall { get; set; } = "";
    public string JobResourceUpload { get; set; } = "";
    public string CreateDeliveryDocket { get; set; } = "";
    public string BackgroundJobSubmission { get; set; } = "";
}

/// <summary>Fails startup with a readable list of whatever Jms settings are missing,
/// rather than the first request to that endpoint failing mid-form.</summary>
public class JmsOptionsValidator : IValidateOptions<JmsOptions>
{
    public ValidateOptionsResult Validate(string? name, JmsOptions options)
    {
        var missing = new List<string>();

        if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out _)) missing.Add("BaseUrl");
        if (string.IsNullOrWhiteSpace(options.ServiceAccount.Username)) missing.Add("ServiceAccount:Username");
        if (string.IsNullOrWhiteSpace(options.ServiceAccount.Password)) missing.Add("ServiceAccount:Password");

        missing.AddRange(typeof(JmsEndpoints).GetProperties()
            .Where(p => p.PropertyType == typeof(string) && string.IsNullOrWhiteSpace((string?)p.GetValue(options.Endpoints)))
            .Select(p => $"Endpoints:{p.Name}"));

        return missing.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail($"Missing Jms settings (set them in Secrets/secrets.json, user secrets or environment variables): {string.Join(", ", missing)}");
    }
}
