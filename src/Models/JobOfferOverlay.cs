namespace WorkshopDashboard.Models;

/// <summary>
/// Staff-entered repair-cost and offer notes for a job on the Model Watchlist page
/// (/model-watchlist), keyed by job_id. JMS has no field for either of these — they only
/// make sense for a job JMS hasn't quoted yet, or as our own competing offer against a
/// quote JMS already sent — so, same reasoning as LoanEquipmentOverlay, they're kept here
/// rather than pushed into JMS.
/// </summary>
public class JobOfferOverlay
{
    public int JobId { get; set; }

    /// <summary>Only meaningful when JMS hasn't quoted the job yet — our own rough repair-cost
    /// guess, entered by hand since there's nothing in JMS to read it from.</summary>
    public decimal? RepairCostEstimate { get; set; }

    /// <summary>Free text — the offer being considered/made for this job, however staff want to
    /// phrase it (a dollar figure, a negotiation note, a status update).</summary>
    public string? OfferEstimate { get; set; }

    public DateTime UpdatedAt { get; set; }
}
