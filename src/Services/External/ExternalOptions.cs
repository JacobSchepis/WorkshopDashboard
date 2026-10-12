namespace WorkshopDashboard.Services.External;

// ═══════════════════════════════════════════════════════════════════════════
//  ExternalOptions
//
//  Credentials and ids for every outside service except JMS (see JmsOptions),
//  one class per configuration section. All of them come from configuration:
//  Secrets/secrets.json (gitignored — see Secrets.example/secrets.json), then
//  user secrets in Development, then environment variables, each overriding
//  the last (e.g. OpenAI__ApiKey).
//
//  Everything here is optional: a service whose settings are missing logs a
//  warning and reports itself unconfigured rather than stopping the app.
// ═══════════════════════════════════════════════════════════════════════════

public class OpenAiOptions
{
    public const string SectionName = "OpenAI";

    public string ApiKey { get; set; } = "";
}

public class DhlOptions
{
    public const string SectionName = "Dhl";

    public string ApiKey { get; set; } = "";
}

/// <summary>ClientId/ClientSecret are the "API Key"/"Secret Key" on a developer.fedex.com
/// project with the Track API added. UseSandbox picks apis-sandbox.fedex.com over
/// apis.fedex.com — leave it on until the account has production access.</summary>
public class FedExOptions
{
    public const string SectionName = "FedEx";

    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public bool UseSandbox { get; set; } = true;
}

/// <summary>Login for the Transvirtual portal JMLA consignments are tracked through.</summary>
public class JmlaOptions
{
    public const string SectionName = "Jmla";

    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    /// <summary>The portal's id for the account whose consignments are listed (its idSearchQueries).</summary>
    public string AccountId { get; set; } = "";
}

public class MondayOptions
{
    public const string SectionName = "Monday";

    public string ApiToken { get; set; } = "";

    /// <summary>The purchasing board MondayIntegration.razor shows, and the two groups
    /// (monday's sub-tables) on it that matter — the rest are completed/archived orders.</summary>
    public string PurchasingBoardId { get; set; } = "";
    public string OpenOrdersGroupId { get; set; } = "topics";
    public string FinalisationGroupId { get; set; } = "";
}

/// <summary>An OAuth "Desktop app" client from Google Cloud Console (Gmail send + Sheets).
/// The first call through each scope opens a browser for one-time consent; the granted
/// tokens are kept under TokenStorePath (relative to the content root).</summary>
public class GoogleOptions
{
    public const string SectionName = "Google";

    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string TokenStorePath { get; set; } = "Secrets";

    /// <summary>From each sheet's URL: docs.google.com/spreadsheets/d/{id}/edit?gid={gid}</summary>
    public string CustomerCreditSheetId { get; set; } = "";
    public string LoanEquipmentSheetId { get; set; } = "";
    public int LoanEquipmentTabGid { get; set; }
}

public class DevToolsOptions
{
    public const string SectionName = "DevTools";

    /// <summary>The one login that sees the Dev Tools pages. Empty means nobody.</summary>
    public string AllowedUsername { get; set; } = "";
}
