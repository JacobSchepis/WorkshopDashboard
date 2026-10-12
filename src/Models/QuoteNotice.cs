using WorkshopDashboard.Services.Quoting;

namespace WorkshopDashboard.Models;

/// <summary>An admin-editable disclaimer/advisory posted as a $0 line item on quotes matching
/// its targets (e.g. Apex's "Leak Advisory" for flex scopes) — see QuoteNoticeStore.</summary>
public class QuoteNotice
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;

    /// <summary>Applies to every quote regardless of <see cref="Targets"/>.</summary>
    public bool IsGlobal { get; set; }

    public List<QuoteNoticeTarget> Targets { get; set; } = new();

    /// <summary>Where this notice's line falls relative to the other notices — JMS prints quote
    /// lines in the order they're created, so this controls the printed order. Lower sorts first.
    /// Notices always come after the repair lines.</summary>
    public int Order { get; set; }
}
