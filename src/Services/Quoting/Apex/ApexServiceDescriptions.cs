namespace WorkshopDashboard.Services.Quoting.Apex;

/// <summary>The service line text for an Apex-priced quote — shared by QuoteApex (lists extracted
/// from Apex's PDF) and QuoteInHouseFlex (lists typed in by hand), so both quotes read the same.
/// Freight (Apex only — in-house scopes don't ship) is folded into the repair line's price.</summary>
public static class ApexServiceDescriptions
{
    public static string GetTechnicalObservationsDescription(IEnumerable<string> observations) =>
        "Technical Observations:\n\n" + string.Join("\n", Clean(observations).Select(o => "• " + o));

    public static string GetRepairsDescription(IEnumerable<string> repairs, decimal subTotal, decimal labourRemoved, decimal discount, decimal total, decimal freight = 0m)
    {
        var lines = new List<string>();
        lines.Add("Repairs Required:");
        lines.Add("");

        foreach (var r in Clean(repairs))
            lines.Add("• " + r);

        lines.Add("");
        lines.Add($"Sub Total:  ${subTotal:N0}");

        if (labourRemoved != 0)
            lines.Add($"Labour Component Removed:  -${Math.Abs(labourRemoved):N0}");

        if (discount != 0)
            lines.Add($"Discount:  -${Math.Abs(discount):N0}");

        if (freight != 0)
            lines.Add($"Freight:  ${freight:N0}");

        lines.Add($"Total Cost: ");

        return string.Join("\n", lines);
    }

    public static string GetWorkOrderNotesDescription(int estimatedCompletionDays) =>
        QuoteServiceText.WorkOrderNotes(estimatedCompletionDays);

    // Blank rows left over from "+ Add" shouldn't post as empty bullets.
    private static IEnumerable<string> Clean(IEnumerable<string> lines) =>
        lines.Select(l => l.Trim()).Where(l => l.Length > 0);
}
