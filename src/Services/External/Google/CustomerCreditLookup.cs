using Google.Apis.Services;
using Google.Apis.Sheets.v4;
using Microsoft.Extensions.Options;

namespace WorkshopDashboard.Services.External.Google;

public record CreditLine(string SerialNumber, string Model, string CreditGained, string CreditDeducted,
    string Date, string QuoteNo, string PurchaseOfferNo, string RemainingBalance);

public record CustomerCredit(string TabName, string TotalAccrued, string TotalRemaining, List<CreditLine> Lines, string? Error = null);

/// <summary>A customer's scope-repair credit statement — the Google Sheet normally, invented
/// figures in demo mode (Demo/DemoSheets).</summary>
public interface ICustomerCreditSource
{
    Task<CustomerCredit> GetCreditAsync(string tabName);
}

// ═══════════════════════════════════════════════════════════════════════════
//  CustomerCreditLookup
//
//  Credit is no longer managed on JMS — it's tracked entirely in a shared
//  Google Sheet, one tab per customer. This reads a tab's fixed layout (a
//  convention the credit team maintains by hand in the sheet itself, not
//  something this app enforces):
//    row 11: A11:C11 merged = total accrued, E11:H11 merged = total remaining
//    row 14: column headers (Serial Number, Model, Credit Gained, Credit
//            Deducted, Date, Quote No., Purchase Offer No., Remaining Credit
//            Balance)
//    row 15+: one credit line per row, ending at the first row with a blank
//             Serial Number
//  A merged cell's value only appears on its top-left cell via the Sheets
//  API, so A11/E11 alone give the two totals without needing to know the
//  merge span.
//
//  Authorizes through GoogleAuthorizer (the same OAuth client as GmailMailTransport) under its
//  own scope (Sheets read-only) and its own token folder, token-store-sheets. The first call
//  needs a one-time interactive consent in a browser.
// ═══════════════════════════════════════════════════════════════════════════

public class CustomerCreditLookup : ICustomerCreditSource
{
    // From the sheet's URL: https://docs.google.com/spreadsheets/d/{this}/edit
    // From GoogleOptions (the Google configuration section).
    private readonly string SpreadsheetId;

    private static readonly string[] Scopes = { SheetsService.Scope.SpreadsheetsReadonly };
    private const string ApplicationName = "WorkshopDashboard";

    private readonly GoogleAuthorizer _auth;
    private readonly ILogger<CustomerCreditLookup> _logger;
    private SheetsService? _sheetsService;

    public CustomerCreditLookup(GoogleAuthorizer auth, IOptions<GoogleOptions> options, ILogger<CustomerCreditLookup> logger)
    {
        _auth = auth;
        _logger = logger;
        SpreadsheetId = options.Value.CustomerCreditSheetId;
    }

    public async Task<CustomerCredit> GetCreditAsync(string tabName)
    {
        try
        {
            var sheets = await GetServiceAsync();

            // A10:H — covers both merged totals (row 11) and the header row (14) through
            // whatever data follows, in one call.
            var request = sheets.Spreadsheets.Values.Get(SpreadsheetId, $"'{tabName}'!A10:H");
            request.ValueRenderOption = SpreadsheetsResource.ValuesResource.GetRequest.ValueRenderOptionEnum.FORMATTEDVALUE;
            var response = await request.ExecuteAsync();

            var rows = response.Values;
            if (rows is null || rows.Count == 0)
                return new CustomerCredit(tabName, "", "", new(), "Tab has no data in the expected range.");

            // Rows come back relative to A10 — row 11 (totals) is index 1, row 15+ (data) is
            // index 5+. A short/blank row comes back with fewer cells than requested, hence Cell().
            static string Cell(IList<object> row, int col) => col < row.Count ? row[col]?.ToString() ?? "" : "";

            var totalsRow = rows.Count > 1 ? rows[1] : new List<object>();
            var totalAccrued = Cell(totalsRow, 0);
            var totalRemaining = Cell(totalsRow, 4);

            var lines = new List<CreditLine>();
            for (var i = 5; i < rows.Count; i++)
            {
                var row = rows[i];
                var serial = Cell(row, 0);
                if (string.IsNullOrWhiteSpace(serial)) continue; // blank Serial Number = end of data

                lines.Add(new CreditLine(
                    serial, Cell(row, 1), Cell(row, 2), Cell(row, 3),
                    Cell(row, 4), Cell(row, 5), Cell(row, 6), Cell(row, 7)));
            }

            return new CustomerCredit(tabName, totalAccrued, totalRemaining, lines);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load credit sheet tab \"{Tab}\"", tabName);
            return new CustomerCredit(tabName, "", "", new(),
                $"Could not read tab \"{tabName}\" — check the tab name is exact, or see server log.");
        }
    }

    private async Task<SheetsService> GetServiceAsync()
    {
        if (_sheetsService != null) return _sheetsService;

        // Its own token folder, not Gmail's — see GoogleAuthorizer for why they can't share.
        var credential = await _auth.AuthorizeAsync(Scopes, "token-store-sheets");

        _sheetsService = new SheetsService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = ApplicationName
        });

        return _sheetsService;
    }
}
