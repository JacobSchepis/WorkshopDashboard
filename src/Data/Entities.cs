namespace WorkshopDashboard.Data;

// Rows that only exist because of the database — the stores' public APIs still hand out
// the same shapes they always did (HashSet<int>, Dictionary<int, int>, ...). Richer records
// (ScopeCustomer, Repairer, QuoteNotice, ...) are mapped straight from src/Models instead.

public class HighlightedJob
{
    public int JobId { get; set; }
}

public class InterestedModel
{
    public int ModelId { get; set; }
}

public class IgnoredJobStatus
{
    public int JobStatusId { get; set; }
}

public enum ScopeExclusionKind
{
    Job,
    Company,
    Model
}

/// <summary>One job/company/model id excluded from "scope jobs" — see ScopeJobStore.</summary>
public class ScopeExclusion
{
    public ScopeExclusionKind Kind { get; set; }
    public int Id { get; set; }
}

/// <summary>One (technician, task, day) the timesheet logger has already logged.</summary>
public class TimesheetTaskLogEntry
{
    public int TechnicianUserId { get; set; }
    public string TaskKey { get; set; } = "";
    public DateOnly Date { get; set; }
}

/// <summary>A single named setting. Small config values that don't justify a table of
/// their own (timesheet admin job id, quote freight options).</summary>
public class AppSetting
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}

/// <summary>A company's saved pricing/shipping group on one of the quote pages. Scope names
/// which page/group kind it belongs to, so groups don't collide across repairers.</summary>
public class CompanyGroupOverride
{
    public string Scope { get; set; } = "";
    public int CompanyId { get; set; }
    public int GroupId { get; set; }
}

/// <summary>Preferred short display name for a manufacturer, keyed by its JMS company_id.</summary>
public class ManufacturerAbbreviation
{
    public int ManufacturerCompanyId { get; set; }
    public string Abbreviation { get; set; } = "";
}

/// <summary>A customer model a loan fleet model can stand in for — see LoanModelSubstitute.</summary>
public class LoanModelSubstitutePair
{
    public int LoanModelId { get; set; }
    public int SubstituteModelId { get; set; }
}

/// <summary>A cached carrier tracking lookup — see TrackingService.</summary>
public class TrackingCacheEntry
{
    public string Key { get; set; } = "";
    public string? TrackingNumber { get; set; }
    public string Carrier { get; set; } = "";
    public string Status { get; set; } = "";
    public string? LastEvent { get; set; }
    public string? LastLocation { get; set; }
    public DateTime? LastEventTime { get; set; }
    public DateTime? FirstEventTime { get; set; }
    public bool IsDelivered { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime FetchedUtc { get; set; }
}

/// <summary>
/// Someone who has logged into this app. Created on their first login, once JMS has
/// confirmed the credentials — see UserAccountService. PasswordHash lets them log in while
/// JMS is unreachable; EncryptedJmsPassword (Data Protection, reversible) lets the app
/// silently log them back into JMS when their JMS session expires.
/// </summary>
public class AppUser
{
    public int Id { get; set; }
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string EncryptedJmsPassword { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
    public DateTime LastLoginAtUtc { get; set; }
}

public enum JmsOutboxState
{
    /// <summary>Waiting to be sent — JmsWriteQueueWorker retries it with backoff.</summary>
    Pending,
    /// <summary>JMS refused it, or it ran out of attempts. Stays until the user retries or
    /// discards it on /jms-pending.</summary>
    Failed
}

/// <summary>
/// A JMS write that couldn't be sent straight away (JMS down, or an earlier write for the
/// same user still waiting) — see JmsWriteQueue. Sent as <see cref="Username"/>, in Id order
/// per user. Rows are deleted once JMS accepts them.
/// </summary>
public class JmsOutboxItem
{
    public int Id { get; set; }
    public string Username { get; set; } = "";

    /// <summary>The JmsWrite, serialized with its type (see JmsWrite's JSON attributes).</summary>
    public string WriteJson { get; set; } = "";

    /// <summary>Plain-English summary for the pending-changes page.</summary>
    public string Description { get; set; } = "";

    public JmsOutboxState State { get; set; }
    public int Attempts { get; set; }

    /// <summary>A send may have reached JMS without us seeing the reply (timeout) — check
    /// whether it already landed before sending again, so nothing is created twice.</summary>
    public bool NeedsCheck { get; set; }

    public string? LastError { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime NextAttemptAtUtc { get; set; }
}

/// <summary>A hospital that gets the monthly site report pack — see MonthlyReportService.
/// Keyed by its JMS company_id.</summary>
public class MonthlyReportSite
{
    public int CompanyId { get; set; }
    public string CompanyName { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

public enum MonthlyReportState
{
    Pending,
    Running,
    Done,
    Failed
}

/// <summary>One site's report pack for one month. <see cref="Month"/> is the month the
/// reports cover ("2026-09"), built on the 1st of the month after. The finished PDF lives
/// on disk at <see cref="FilePath"/> (relative to App_Data), not in the database.</summary>
public class MonthlyReportRun
{
    public int Id { get; set; }
    public string Month { get; set; } = "";
    public int CompanyId { get; set; }
    public string CompanyName { get; set; } = "";

    public MonthlyReportState State { get; set; }
    public int Attempts { get; set; }

    /// <summary>Which report it's on while Running, e.g. "Job Progress (2/4)".</summary>
    public string? Step { get; set; }
    public string? LastError { get; set; }
    public string? FilePath { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime NextAttemptAtUtc { get; set; }
    public DateTime? FinishedAtUtc { get; set; }
}

/// <summary>One line of Apex's flexible repair price sheet — see ApexPriceSheetStore.
/// Keyed by Apex's own repair code. Confidential: lives only in the database (imported
/// from App_Data/apex-pricing.csv), never in the repo.</summary>
public class ApexPriceLine
{
    public string Code { get; set; } = "";
    public int LineItem { get; set; }
    public string Description { get; set; } = "";
    public string Category { get; set; } = "";
    public decimal PriceUsd { get; set; }
}

/// <summary>How much time one use of a tracked action (an AnalyticsEvent "Click", by name) saves
/// over doing it by hand — set on UsageTimeSaved.razor, totted up on UsageAnalytics.razor.
/// PerItem: the minutes are per item (the event's Quantity — jobs created, pages generated...)
/// rather than per use.</summary>
public class ActionTimeSaving
{
    public string ActionName { get; set; } = "";
    public double Minutes { get; set; }
    public bool PerItem { get; set; }
}
