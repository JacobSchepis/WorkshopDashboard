using Google.Apis.Services;
using Google.Apis.Sheets.v4;
using Google.Apis.Sheets.v4.Data;
using Microsoft.Extensions.Options;

namespace WorkshopDashboard.Services.External.Google;

public record LoanEquipmentExportRow(
    string Model, string SerialNumber, string Status, string Location, string LoanedTo,
    string OriginalOwner, string Job, string LoanedDate, string CoveringFor,
    string InspectedDate, string AgreementSigned, string BuyPrice);

/// <summary>Publishes the loan fleet list somewhere people outside the app can read it.</summary>
public interface ILoanEquipmentExporter
{
    Task ExportAsync(List<LoanEquipmentExportRow> rows);
}

// ═══════════════════════════════════════════════════════════════════════════
//  LoanEquipmentSheetExportService
//
//  Overwrites a fixed Google Sheet tab with the current loan equipment list
//  on demand (LoanEquipmentManager.razor's "Export to Sheet" button). The
//  canonical data stays managed here in WorkshopDashboard — the sheet is just
//  a read-only mirror other people can check without needing access to this
//  app. Always a full overwrite (clear then write), not an append/diff, so
//  a previous export's stale trailing rows never linger if the list shrinks.
//
//  Authorizes through GoogleAuthorizer under its own WRITE scope
//  (Spreadsheets, not SpreadsheetsReadonly) and its own token folder,
//  token-store-sheets-write. The first call needs a one-time interactive
//  consent in a browser.
// ═══════════════════════════════════════════════════════════════════════════

public class LoanEquipmentSheetExportService : ILoanEquipmentExporter
{
    // From the sheet's URL: https://docs.google.com/spreadsheets/d/{this}/edit?gid={TabGid}
    // From GoogleOptions (the Google configuration section).
    private readonly string SpreadsheetId;
    private readonly int TabGid;

    private static readonly string[] Scopes = { SheetsService.Scope.Spreadsheets };
    private const string ApplicationName = "WorkshopDashboard";

    private static readonly object[] Headers =
    {
        "Model", "Serial Number", "Status", "Location", "Loaned To", "Original Owner",
        "Loan Job", "Loaned Date", "Covering For", "Inspected Date", "Agreement Signed", "Buy Price"
    };

    private readonly GoogleAuthorizer _auth;
    private SheetsService? _sheetsService;

    public LoanEquipmentSheetExportService(GoogleAuthorizer auth, IOptions<GoogleOptions> options)
    {
        _auth = auth;
        SpreadsheetId = options.Value.LoanEquipmentSheetId;
        TabGid = options.Value.LoanEquipmentTabGid;
    }

    public async Task ExportAsync(List<LoanEquipmentExportRow> rows)
    {
        var sheets = await GetServiceAsync();
        var tabTitle = await ResolveTabTitleAsync(sheets);

        var values = new List<IList<object>> { Headers.ToList() };
        values.AddRange(rows.Select(r => (IList<object>)new List<object>
        {
            r.Model, r.SerialNumber, r.Status, r.Location, r.LoanedTo, r.OriginalOwner,
            r.Job, r.LoanedDate, r.CoveringFor, r.InspectedDate, r.AgreementSigned, r.BuyPrice
        }));

        await sheets.Spreadsheets.Values.Clear(new ClearValuesRequest(), SpreadsheetId, $"'{tabTitle}'!A1:Z10000").ExecuteAsync();

        var update = sheets.Spreadsheets.Values.Update(new ValueRange { Values = values }, SpreadsheetId, $"'{tabTitle}'!A1");
        update.ValueInputOption = SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum.RAW;
        await update.ExecuteAsync();
    }

    // Resolved by gid (a stable numeric id) rather than a hardcoded tab name, since the tab
    // could get renamed by hand without anyone updating this.
    private async Task<string> ResolveTabTitleAsync(SheetsService sheets)
    {
        var spreadsheet = await sheets.Spreadsheets.Get(SpreadsheetId).ExecuteAsync();
        var sheet = spreadsheet.Sheets.FirstOrDefault(s => s.Properties.SheetId == TabGid);
        if (sheet == null)
            throw new Exception($"Sheet gid={TabGid} not found in spreadsheet {SpreadsheetId}.");

        return sheet.Properties.Title;
    }

    private async Task<SheetsService> GetServiceAsync()
    {
        if (_sheetsService != null) return _sheetsService;

        var credential = await _auth.AuthorizeAsync(Scopes, "token-store-sheets-write");

        _sheetsService = new SheetsService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = ApplicationName
        });

        return _sheetsService;
    }
}
