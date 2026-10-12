namespace WorkshopDashboard.Services.Admin;

/// <summary>
/// Display formatting for ScopeLookupService's raw rows — "Acme Ltd (123)" style labels
/// and contact summaries. Was copy-pasted identically across ScopeCustomerManager.razor,
/// ScopeJobManager.razor, RepairerManager.razor, and LoanEquipmentManager.razor; pulled out
/// here since it's pure display logic with no per-page variation.
/// </summary>
public static class ScopeLabelFormatter
{
    public static string FormatCompanyLabel(int id, string? name) =>
        string.IsNullOrWhiteSpace(name) ? $"({id})" : $"{name} ({id})";

    public static string FormatModelLabel(int id, string? label) =>
        string.IsNullOrWhiteSpace(label) ? $"({id})" : $"{label} ({id})";

    public static string FormatContactDetails(string? name, string? email, string? phone)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(name)) parts.Add(name);
        if (!string.IsNullOrWhiteSpace(email)) parts.Add(email);
        if (!string.IsNullOrWhiteSpace(phone)) parts.Add(phone);
        return parts.Count > 0 ? string.Join(" · ", parts) : "(no details)";
    }
}
