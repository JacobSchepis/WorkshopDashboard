namespace WorkshopDashboard.Services.Quoting.Falcon;

public static class FalconPricingConfig
{
    // Each entry is (falcon cost, our charge to customer)
    private static readonly (decimal Cost, decimal Charge)[] PriceTable =
    {
        (19.95m,   40.00m),
        (21.95m,   40.00m),
        (24.60m,   45.00m),
        (29.95m,   50.00m),
        (32.80m,   55.00m),
        (36.90m,   60.00m),
        (49.20m,   75.00m),
        (65.60m,   100.00m),
        (73.80m,  110.00m),
        (84.50m,  120.00m),
        (98.40m,  140.00m),
        (110.00m, 155.00m),
        (116.00m, 165.00m),
        (123.00m, 170.00m),
        (147.60m, 205.00m),
        (159.00m, 220.00m),
        (172.20m, 235.00m),
        (184.50m, 250.00m),
        (189.90m, 260.00m),
        (196.80m, 270.00m),
        (198.00m, 270.00m),
        (210.00m, 285.00m),
        (221.40m, 300.00m),
        (246.00m, 330.00m),
        (260.00m, 350.00m),
        (299.00m, 400.00m),
        (360.00m, 480.00m),
        (395.00m, 525.00m),
        (590.00m, 780.00m),
        (600.00m, 790.00m),
        (695.00m, 915.00m),
        (895.00m, 1175.00m),
        (995.00m, 1305.00m),
    };

    /// <summary>
    /// Given a Falcon cost price, returns our charge to the customer.
    /// Exact matches return the table value directly.
    /// Values between entries are linearly interpolated.
    /// Values below the first entry or above the last entry are clamped.
    /// </summary>
    public static decimal GetCustomerPrice(decimal cost)
    {
        if (cost <= 0) return 0;

        // Exact match
        foreach (var (c, charge) in PriceTable)
            if (c == cost) return charge;

        // Below lowest entry
        if (cost < PriceTable[0].Cost)
            return PriceTable[0].Charge;

        // Above highest entry
        if (cost > PriceTable[^1].Cost)
            return PriceTable[^1].Charge;

        // Find surrounding entries and interpolate
        for (int i = 0; i < PriceTable.Length - 1; i++)
        {
            var (lowCost, lowCharge) = PriceTable[i];
            var (highCost, highCharge) = PriceTable[i + 1];

            if (cost >= lowCost && cost <= highCost)
            {
                var t = (cost - lowCost) / (highCost - lowCost);
                var interpolated = lowCharge + t * (highCharge - lowCharge);

                // Round to nearest $5 to keep prices clean
                return Math.Round(interpolated / 5, MidpointRounding.AwayFromZero) * 5;
            }
        }

        return cost; // fallback — shouldn't be reached
    }
}
