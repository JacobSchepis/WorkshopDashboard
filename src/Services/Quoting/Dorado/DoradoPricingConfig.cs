namespace WorkshopDashboard.Services.Quoting.Dorado;

public class DoradoPricingGroup
{
    /// <summary>In-house Level 0 (exterior clean, scope not opened) — the same for every group.
    /// Dorado never quotes it, so it's only on QuoteInHouseRigid.</summary>
    public const decimal Level0 = 220m;

    public decimal Level1 { get; init; }
    public decimal Level2 { get; init; }
    public decimal Level3 { get; init; }
    public decimal MaxPrice { get; init; }

    /// <summary>
    /// Returns the price for a recognised level name, or null when the quote has no level
    /// repair at all (semi-rigid / specialty scopes) or the string isn't a known level.
    /// </summary>
    public decimal? TryGetLevel(string? level)
    {
        if (string.IsNullOrWhiteSpace(level)) return null;

        return level.Trim().ToLower() switch
        {
            "level 0" => Level0,
            "level 1" => Level1,
            "level 2" => Level2,
            "level 3" => Level3,
            "unrepairable" => 0.00m,
            _ => null
        };
    }

    /// <summary>
    /// Non-nullable convenience wrapper — treats "no level" as $0.00.
    /// </summary>
    public decimal GetLevel(string? level) => TryGetLevel(level) ?? 0.00m;
}

public static class DoradoLevels
{
    public const string None = "";

    /// <summary>Canonical level names, in the order they should appear in a picker.</summary>
    public static readonly string[] All = { "Level 1", "Level 2", "Level 3", "Unrepairable" };

    /// <summary>QuoteInHouseRigid's picker: Dorado's levels plus our own Level 0, which Dorado never quotes.</summary>
    public const string Level0 = "Level 0";
    public static readonly string[] InHouse = [Level0, .. All];

    public static bool IsKnown(string? level) =>
        !string.IsNullOrWhiteSpace(level)
        && All.Any(l => string.Equals(l, level.Trim(), StringComparison.OrdinalIgnoreCase));

    public static bool IsKnownInHouse(string? level) =>
        !string.IsNullOrWhiteSpace(level)
        && InHouse.Any(l => string.Equals(l, level.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Level 0 never opens the scope, so it can't take rod lenses.</summary>
    public static bool IsLevel0(string? level) =>
        string.Equals(level?.Trim(), Level0, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Maps whatever the extractor returned onto a canonical level name.
    /// Returns "" (DoradoLevels.None) when there's no level repair on the quote.
    /// </summary>
    public static string Normalise(string? level)
    {
        if (string.IsNullOrWhiteSpace(level)) return None;
        var match = All.FirstOrDefault(l => string.Equals(l, level.Trim(), StringComparison.OrdinalIgnoreCase));
        return match ?? None;
    }
}

public static class DoradoPricingConfig
{
    public static readonly Dictionary<int, DoradoPricingGroup> Groups = new()
    {
        {
            1, new DoradoPricingGroup { Level1 = 650, Level2 = 750, Level3 = 850, MaxPrice = 950 }
        },
        {
            2, new DoradoPricingGroup { Level1 = 750, Level2 = 850, Level3 = 950, MaxPrice = 950 }
        },
        {
            3, new DoradoPricingGroup { Level1 = 850, Level2 = 850, Level3 = 850, MaxPrice = 850 }
        },
    };

    // Company ID → group number
    public static readonly Dictionary<int, int> CompanyGroups = new()
    {
        {2, 2}
    };

    public static DoradoPricingGroup? GetGroupForCompany(int companyId)
    {
        if (!CompanyGroups.TryGetValue(companyId, out var groupId)) return null;
        return Groups.GetValueOrDefault(groupId);
    }
}
