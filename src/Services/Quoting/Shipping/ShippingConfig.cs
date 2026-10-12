namespace WorkshopDashboard.Services.Quoting.Shipping;

public class ShippingGroup
{
    public required string Label { get; init; }
    public decimal RigidPrice { get; init; }
    public decimal FlexPrice { get; init; }
    public string Description { get; init; } = string.Empty;
}

public static class ShippingConfig
{
    public static readonly Dictionary<int, ShippingGroup> Groups = new()
    {
        { 1, new ShippingGroup { Label = "NSW Metro",                RigidPrice = 35m, FlexPrice = 35m,  Description = "Specialised Freight & Handling Charges NSW Metro" } },
        { 2, new ShippingGroup { Label = "NSW Regional",             RigidPrice = 35m, FlexPrice = 35m,  Description = "Specialised Freight & Handling Charges NSW Regional" } },
        { 3, new ShippingGroup { Label = "Interstate SA/VIC/QLD",    RigidPrice = 45m, FlexPrice = 45m,  Description = "Specialised Freight & Handling Charges Interstate" } },
        { 4, new ShippingGroup { Label = "Interstate TAS/WA",        RigidPrice = 80m, FlexPrice = 80m,  Description = "Specialised Freight & Handling Charges VIC Regional" } },
    };

    /// <summary>The freight folded into a flex scope or probe quote's repair line (Apex, Ember) —
    /// 0 when no group is picked yet.</summary>
    public static decimal FreightFor(int? groupId) =>
        groupId != null && Groups.TryGetValue(groupId.Value, out var group) ? group.FlexPrice : 0m;
}
