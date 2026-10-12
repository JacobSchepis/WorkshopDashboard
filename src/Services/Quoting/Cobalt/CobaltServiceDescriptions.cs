namespace WorkshopDashboard.Services.Quoting.Cobalt;

public static class CobaltServiceDescriptions
{
    public static string GetTechnicalObservationsDescription(CobaltLineItem item) =>
        "Technical Observations:\n\n" + BulletsOrNone(item.TechnicalObservations);

    public static string GetRepairsDescription(CobaltLineItem item) =>
        "Repairs Required:\n\n" + BulletsOrNone(item.RepairsRequired);

    private static string BulletsOrNone(List<string> lines)
    {
        var cleaned = lines.Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => "• " + l.Trim()).ToList();
        return cleaned.Count > 0 ? string.Join("\n", cleaned) : "None noted.";
    }
}
