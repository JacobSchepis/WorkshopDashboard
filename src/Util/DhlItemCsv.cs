using System.Text;

namespace WorkshopDashboard.Util;

public class DhlItemCsvRow
{
    public string Description       { get; set; } = "";
    public string CommodityCode     { get; set; } = "";
    public string DeclaredValue     { get; set; } = "";
    public string Weight            { get; set; } = "";
    public string CountryOfOrigin   { get; set; } = "";
    public string SerialNumber      { get; set; } = "";
}

/// <summary>
/// Builds a MyDHL "Upload a File with Item Details" CSV — no header row, 14 fixed columns per
/// item, exactly matching DHL's CSV file guidelines. One row per physical unit (Quantity is
/// always 1, Unique Item Number is always the literal "1" per DHL's spec) with the unit's
/// serial number carried as a Reference Type/Details pair (SE = Serial Number) rather than
/// batching identical models under one row with Quantity > 1 — that would lose the individual
/// serials DHL's Reference field can only hold one of per row.
/// </summary>
public static class DhlItemCsvGenerator
{
    public static string Generate(List<DhlItemCsvRow> items, string currency)
    {
        var sb = new StringBuilder();

        foreach (var item in items)
        {
            var countryCode = CountryCodes.ToAlpha2(item.CountryOfOrigin) ?? "";
            var hasSerial = !string.IsNullOrWhiteSpace(item.SerialNumber);

            var fields = new[]
            {
                "1",                                    // 1  Unique Item Number — always 1
                "INV_ITEM",                              // 2  Item
                Truncate(item.Description, 170),         // 3  Item Description
                item.CommodityCode,                      // 4  Commodity Code
                "1",                                      // 5  Quantity — one row per unit
                "EA",                                     // 6  Units
                item.DeclaredValue,                       // 7  Value
                currency,                                 // 8  Currency
                item.Weight,                              // 9  Weight
                "",                                        // 10 Weight 2 — always blank
                countryCode,                               // 11 Country of Origin
                hasSerial ? "SE" : "",                     // 12 Reference Type
                hasSerial ? Truncate(item.SerialNumber, 35) : "", // 13 Reference Details
                ""                                         // 14 Tax Paid — left blank
            };

            sb.AppendLine(string.Join(",", fields.Select(CsvEscape)));
        }

        return sb.ToString();
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max];

    private static string CsvEscape(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        return value.Contains(',') || value.Contains('"') || value.Contains('\n')
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }
}

/// <summary>Country name -> ISO 3166-1 alpha-2 code, for DHL's Country of Origin column.
/// Covers the countries seen in this app's customs data; returns null (caller should flag it)
/// for anything not in the list. A bare 2-letter input is passed through as-is.</summary>
public static class CountryCodes
{
    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Australia"] = "AU",
        ["Canada"] = "CA",
        ["China"] = "CN",
        ["Denmark"] = "DK",
        ["France"] = "FR",
        ["Germany"] = "DE",
        ["Italy"] = "IT",
        ["Japan"] = "JP",
        ["Mexico"] = "MX",
        ["Netherlands"] = "NL",
        ["New Zealand"] = "NZ",
        ["Norway"] = "NO",
        ["Sweden"] = "SE",
        ["Switzerland"] = "CH",
        ["United Kingdom"] = "GB",
        ["UK"] = "GB",
        ["USA"] = "US",
        ["United States"] = "US",
        ["United States of America"] = "US",
    };

    public static string? ToAlpha2(string countryName)
    {
        var trimmed = countryName?.Trim() ?? "";
        if (trimmed.Length == 0) return null;
        // The list first, so a two-letter name that isn't itself a code ("UK") still maps.
        if (Map.TryGetValue(trimmed, out var code)) return code;
        return trimmed.Length == 2 ? trimmed.ToUpperInvariant() : null;
    }
}
