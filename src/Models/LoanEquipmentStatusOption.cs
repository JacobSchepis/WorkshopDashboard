namespace WorkshopDashboard.Models;

/// <summary>
/// One entry in the configurable list of loan-equipment workflow statuses (Ready, Repairing,
/// Damaged, etc.) — see LoanEquipmentStatusStore and LoanEquipmentManager.razor's "Manage
/// Statuses" panel. Replaces what used to be a fixed enum so staff can add/rename/recolor/
/// remove/reorder statuses without a code change.
/// </summary>
public class LoanEquipmentStatusOption
{
    /// <summary>Stable identity, referenced by LoanEquipmentOverlay.Status — never shown to the
    /// user and never changes once created, so renaming Label doesn't orphan units already set
    /// to this status.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Label { get; set; } = "";

    /// <summary>A BlazorBootstrap BadgeColor name (Success/Info/Primary/Warning/Danger/
    /// Secondary) — kept as a string here so new colors don't require a model change, resolved
    /// to the enum at display time.</summary>
    public string Color { get; set; } = "Secondary";

    /// <summary>Display order — lower sorts first, in the filter chips, dropdowns, and the
    /// Manage Statuses panel.</summary>
    public int Order { get; set; }
}
