namespace WorkshopDashboard.Services.Quoting;

/// <summary>Service line text that's the same whichever repairer a quote is for.</summary>
public static class QuoteServiceText
{
    /// <summary>The ETA line — posted by the Apex, Dorado rigid and in-house quote pages.</summary>
    public static string WorkOrderNotes(int estimatedCompletionDays) =>
        "Work Order Notes:\n\n" +
        $"Estimated time of completion: {estimatedCompletionDays} working days.";
}
