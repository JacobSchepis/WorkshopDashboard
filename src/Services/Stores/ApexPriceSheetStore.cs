using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.EntityFrameworkCore;
using WorkshopDashboard.Data;

namespace WorkshopDashboard.Services.Stores;

/// <summary>What re-importing a price sheet would change, line by line (matched on code).</summary>
public record ApexPriceSheetDiff(
    List<ApexPriceLine> Incoming,
    List<ApexPriceLine> Added,
    List<ApexPriceLine> Removed,
    List<(ApexPriceLine Old, ApexPriceLine New)> PriceChanged,
    int DescriptionChanged)
{
    public bool HasChanges => Added.Count + Removed.Count + PriceChanged.Count + DescriptionChanged > 0;
}

// ═══════════════════════════════════════════════════════════════════════════
//  ApexPriceSheetStore
//
//  Apex's flexible repair price sheet (USD), used to price in-house
//  repairs on QuoteInHouseFlex. The sheet is confidential, so it never goes in
//  the repo: it's converted to CSV (line_item, code, description, category,
//  price_usd[, section]), dropped into App_Data/apex-pricing.csv, and imported
//  from there (or uploaded) on the Apex Price Sheet page. An import replaces the
//  whole sheet, after showing what would change. Small (~500 lines), so callers
//  load it all and search in memory.
// ═══════════════════════════════════════════════════════════════════════════

public class ApexPriceSheetStore
{
    public const string DefaultFileName = "apex-pricing.csv";

    private readonly IDbContextFactory<WorkshopDbContext> _dbFactory;

    public ApexPriceSheetStore(IDbContextFactory<WorkshopDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<List<ApexPriceLine>> LoadAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.ApexPriceLines.AsNoTracking().OrderBy(l => l.LineItem).ToListAsync();
    }

    /// <summary>Lines matching every word of <paramref name="term"/> (in the code, description or
    /// category, any case). Code matches rank first, then sheet order.</summary>
    public static List<ApexPriceLine> Search(IEnumerable<ApexPriceLine> lines, string term, int limit = 10)
    {
        var words = term.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0) return new();

        return lines
            .Where(l => words.All(w =>
                l.Code.Contains(w, StringComparison.OrdinalIgnoreCase)
                || l.Description.Contains(w, StringComparison.OrdinalIgnoreCase)
                || l.Category.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(l => l.Code.StartsWith(words[0], StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(l => l.LineItem)
            .Take(limit)
            .ToList();
    }

    /// <summary>Reads a price sheet CSV and compares it with what's stored, without saving
    /// anything. Throws with a readable message if the file isn't a usable price sheet.</summary>
    public async Task<ApexPriceSheetDiff> PreviewAsync(Stream csv)
    {
        var incoming = Parse(csv);
        var current = (await LoadAsync()).ToDictionary(l => l.Code, StringComparer.OrdinalIgnoreCase);
        var incomingByCode = incoming.ToDictionary(l => l.Code, StringComparer.OrdinalIgnoreCase);

        var added = incoming.Where(l => !current.ContainsKey(l.Code)).ToList();
        var removed = current.Values.Where(l => !incomingByCode.ContainsKey(l.Code)).OrderBy(l => l.LineItem).ToList();
        var matched = incoming.Where(l => current.ContainsKey(l.Code)).Select(l => (Old: current[l.Code], New: l)).ToList();
        var priceChanged = matched.Where(m => m.Old.PriceUsd != m.New.PriceUsd).ToList();
        var descriptionChanged = matched.Count(m => m.Old.Description != m.New.Description || m.Old.Category != m.New.Category);

        return new ApexPriceSheetDiff(incoming, added, removed, priceChanged, descriptionChanged);
    }

    /// <summary>Replaces the whole stored sheet with <paramref name="lines"/>.</summary>
    public async Task ReplaceAllAsync(IReadOnlyCollection<ApexPriceLine> lines)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.ApexPriceLines.ExecuteDeleteAsync();
        db.ApexPriceLines.AddRange(lines.Select(l => new ApexPriceLine
        {
            Code = l.Code,
            LineItem = l.LineItem,
            Description = l.Description,
            Category = l.Category,
            PriceUsd = l.PriceUsd,
        }));
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }

    internal static List<ApexPriceLine> Parse(Stream csv)
    {
        using var reader = new StreamReader(csv);
        using var parser = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            PrepareHeaderForMatch = a => a.Header.Trim().ToLowerInvariant(),
            MissingFieldFound = null,
        });

        List<Row> rows;
        try
        {
            rows = parser.GetRecords<Row>().ToList();
        }
        catch (HeaderValidationException)
        {
            throw new InvalidOperationException("That doesn't look like a price sheet CSV — it needs line_item, code, description, category and price_usd columns.");
        }

        var lines = new List<ApexPriceLine>();
        foreach (var r in rows)
        {
            var code = (r.code ?? "").Trim();
            if (code.Length == 0) continue;

            if (!decimal.TryParse((r.price_usd ?? "").Replace("$", "").Replace(",", "").Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var price))
                throw new InvalidOperationException($"Line {r.line_item} ({code}) has a price that isn't a number: \"{r.price_usd}\".");

            lines.Add(new ApexPriceLine
            {
                Code = code,
                LineItem = int.TryParse(r.line_item, out var n) ? n : lines.Count + 1,
                Description = (r.description ?? "").Trim(),
                Category = (r.category ?? "").Trim(),
                PriceUsd = price,
            });
        }

        var duplicate = lines.GroupBy(l => l.Code, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicate != null)
            throw new InvalidOperationException($"Code {duplicate.Key} appears more than once.");
        if (lines.Count == 0)
            throw new InvalidOperationException("The file has no price lines in it.");

        return lines;
    }

    private class Row
    {
        public string? line_item { get; set; }
        public string? code { get; set; }
        public string? description { get; set; }
        public string? category { get; set; }
        public string? price_usd { get; set; }
    }
}
