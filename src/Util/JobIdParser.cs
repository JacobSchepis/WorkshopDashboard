namespace WorkshopDashboard.Util;

public static class JobIdParser
{
    public static bool TryParseSingle(string? raw, out int jobId)
    {
        jobId = 0;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        return int.TryParse(raw.Trim(), out jobId) && jobId > 0;
    }

    
}