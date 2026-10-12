namespace WorkshopDashboard.Services.Quoting.Ember;

public static class EmberServiceDescriptions
{
    public static string GetTechnicalObservationsDescription(EmberQuoteExtraction quote) =>
        "Technical Observations:\n\n" + BulletsOrNone(quote.TechnicalObservations);

    /// <summary>Freight is folded into this line's price, so the breakdown shows it — same
    /// layout as Apex's repair line.</summary>
    public static string GetRepairsDescription(EmberQuoteExtraction quote, decimal subTotal, decimal freight)
    {
        var lines = new List<string> { "Repairs Required:", "", BulletsOrNone(quote.RepairsRequired), "" };
        lines.Add($"Sub Total:  ${subTotal:N2}");

        if (freight != 0)
            lines.Add($"Freight:  ${freight:N2}");

        lines.Add("Total Cost: ");
        return string.Join("\n", lines);
    }

    public static string GetWorkOrderNotesDescription(EmberQuoteExtraction quote)
    {
        var lines = new List<string> { "Work Order Notes:", "" };

        if (!string.IsNullOrWhiteSpace(quote.TurnaroundEstimate))
            lines.Add($"Estimated time of completion: {quote.TurnaroundEstimate}.");

        if (quote.WarrantyDays > 0)
            lines.Add($"Warranty: {quote.WarrantyDays} days.");

        return string.Join("\n", lines);
    }

    private static string BulletsOrNone(List<string> lines)
    {
        var cleaned = lines.Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => "• " + l.Trim()).ToList();
        return cleaned.Count > 0 ? string.Join("\n", cleaned) : "None noted.";
    }
}
