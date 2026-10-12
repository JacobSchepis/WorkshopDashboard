namespace WorkshopDashboard.Models;

// Which customer equipment models a loan fleet model can stand in for — e.g. a GIF-HQ190 loan
// unit can be loaned out while a customer's GIF-H190 is in for repair. JMS has no notion of
// this, so it's maintained by hand on LoanEquipmentManager.razor's "Model Substitutes" tab and
// used by its "Customer Jobs" tab: a loaned-out unit's candidate jobs are the customer's open
// jobs on its own model plus every model listed here. Both sides are real JMS model_ids.
// Stored in the database via LoanModelSubstituteStore.
public class LoanModelSubstitute
{
    public int LoanModelId { get; set; }
    public List<int> SubstituteModelIds { get; set; } = new();
}
