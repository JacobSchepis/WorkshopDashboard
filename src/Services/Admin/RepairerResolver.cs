using WorkshopDashboard.Models;

namespace WorkshopDashboard.Services.Admin;

/// <summary>
/// Resolves which repairer (if any) a job belongs to, using the same rules
/// RepairerManager.razor uses to decide a job is "assigned": an explicit job-number
/// override wins, then a match on the model id of the job's active equipment, then
/// jobs with no equipment at all default to Falcon by convention (never written to
/// RepairerStore — see RepairerManager.razor's header comment). Returns null when
/// none of those apply, i.e. the job still needs a job-level assignment.
/// </summary>
public static class RepairerResolver
{
    public const string NoEquipmentDefault = "Falcon";

    public static string? Resolve(IReadOnlyList<Repairer> repairers, List<int> modelIds, int jobId)
    {
        var byJobId = repairers.FirstOrDefault(r => r.JobIds.Contains(jobId));
        if (byJobId is not null) return byJobId.Name;

        var byModel = repairers.FirstOrDefault(r => modelIds.Any(r.ModelIds.Contains));
        if (byModel is not null) return byModel.Name;

        return modelIds.Count == 0 ? NoEquipmentDefault : null;
    }
}
