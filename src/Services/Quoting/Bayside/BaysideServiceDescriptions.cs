namespace WorkshopDashboard.Services.Quoting.Bayside;

public static class BaysideServiceDescriptions
{
    public static string GetTechnicalObservationsDescription(BaysideLineItem item) =>
        "Technical Observations:\n\n" + BulletsOrNone(item.TechnicalObservations);

    public static string GetRepairsDescription(BaysideLineItem item) =>
        "Repairs Required:\n\n" + BulletsOrNone(item.RepairsRequired);

    private static string BulletsOrNone(List<string> lines)
    {
        var cleaned = lines.Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => "• " + l.Trim()).ToList();
        return cleaned.Count > 0 ? string.Join("\n", cleaned) : "None noted.";
    }
}
