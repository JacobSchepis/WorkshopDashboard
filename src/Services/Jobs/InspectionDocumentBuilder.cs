using System.Globalization;
using System.Net.Http;
using System.Text;
using CsvHelper;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using PdfSharpCore.Drawing;
using WorkshopDashboard.Abstract;
using WorkshopDashboard.Services.External.Jms;
using WorkshopDashboard.Util;

namespace WorkshopDashboard.Services.Jobs;

/// <summary>The three service report types — each has its own standard on the cover.</summary>
public enum ServiceReportType
{
    MedicalDevice,
    ClassWiring,
    MedicalGas,
}

/// <summary>Cover page details the user fills in on the page. Area may span
/// several lines (e.g. "CSSD\nEMECP"). Date is the job's last worked date.</summary>
public record CoverPageDetails(ServiceReportType Type, string RoutineServiceJobNo, string Area, DateTime Date);

/// <summary>
/// Orchestrates fetching and merging all sections of the service report.
/// Section order:
///   1. Cover page          (generated)
///   2. Contents page       (generated — built last, once section page counts are known)
///   3. Inspection Summary  (WorkSummaryReport)
///   4. ERFA Report         (ErfaReport) — omitted if empty
///   5. Not Seen Report     (NotSeenReport) — omitted if empty
///   6. Detailed Inspection (queued as a JMS background job → poll notifications → download)
///   7. Test Equipment Calibration Certificates — per test equipment item used, its
///      certificate (OpenCertificate) then its last inspection
///      (DetailedInspectionReport) — omitted if nothing could be downloaded
/// Each of 3–7 is preceded by a filler page carrying its title, and numbered
/// I., II., ... on the contents page.
/// </summary>
public class InspectionDocumentBuilder
{
    private readonly JmsUserSession _jmsSession;
    private readonly ILogger<InspectionDocumentBuilder> _logger;

    private record Section(string Title, byte[] Pdf);

    public InspectionDocumentBuilder(JmsUserSession jmsSession, ILogger<InspectionDocumentBuilder> logger)
    {
        _jmsSession = jmsSession;
        _logger = logger;
    }

    public async Task<byte[]> BuildAsync(
        int jobId,
        int reportId,
        int companyId,
        int userId,
        CoverPageDetails coverDetails,
        IReadOnlyList<TestEquipmentStatus> testEquipment,
        ICollection<string>? warnings = null,
        IProgress<string>? progress = null)
    {
        var jms = await _jmsSession.GetClientAsync();
        if (jms == null) throw new Exception("JMS session unavailable.");

        // Everything after the contents page; each gets its own filler page.
        var body = new List<Section>();

        // ── Inspection Summary (work summary) ─────────────────────────────
        progress?.Report("Fetching work summary...");
        body.Add(new("Inspection Summary Report", await FetchWorkSummaryAsync(jms, jobId, reportId, companyId)));

        // ── ERFA Report (conditional) ─────────────────────────────────────
        progress?.Report("Fetching ERFA report...");
        var erfa = await FetchErfaReportAsync(jms, jobId, reportId, companyId);
        if (erfa != null)
            body.Add(new("ERFA Report", erfa));
        else
            progress?.Report("ERFA report is empty — skipping.");

        // ── Not Seen (conditional) ────────────────────────────────────────
        progress?.Report("Fetching not seen report...");
        var notSeen = await FetchNotSeenAsync(jms, jobId, reportId, companyId);
        if (notSeen != null)
            body.Add(new("Not Seen Report", notSeen));
        else
            progress?.Report("Not seen report is empty — skipping.");

        // ── Detailed Inspection Report ────────────────────────────────────
        progress?.Report("Queuing detailed inspection report...");
        body.Add(new("Detailed Inspection Report", await FetchDetailedInspectionReportAsync(jms, jobId, reportId, companyId, userId, progress)));

        // ── Test Equipment Calibration Certificates (conditional) ─────────
        progress?.Report("Fetching calibration certificates and test equipment inspections...");
        var certificates = await FetchTestEquipmentDocsAsync(jms, testEquipment, warnings);
        if (certificates != null)
            body.Add(new("Test Equipment Calibration Certificate", certificates));

        // ── Cover + contents ──────────────────────────────────────────────
        progress?.Report("Generating cover and contents pages...");
        var (siteName, siteAddress) = await FetchSiteAsync(jms, companyId);
        var cover = GenerateTitlePage(jobId, coverDetails, siteName, siteAddress);

        // JMS's own reports can come back in a shape PdfSharpCore can't import.
        body = body.Select(s => s with { Pdf = PdfTools.EnsureImportable(s.Pdf, _logger) }).ToList();

        // Cover and contents are one page each, so the body starts on page 3.
        // Contents entries point at each section's filler page.
        var entries = new List<(string Title, int Page)>();
        var bodyPdfs = new List<byte[]>();
        var nextPage = PdfTools.CountPages(cover) + 1 + 1;
        for (var i = 0; i < body.Count; i++)
        {
            var filler = GenerateFillerPage(body[i].Title);
            entries.Add(($"{ToRoman(i + 1)}. {body[i].Title}", nextPage));
            nextPage += PdfTools.CountPages(filler) + PdfTools.CountPages(body[i].Pdf);
            bodyPdfs.Add(filler);
            bodyPdfs.Add(body[i].Pdf);
        }
        var contents = GenerateContentsPage(entries);

        // ── Merge all sections ────────────────────────────────────────────
        progress?.Report("Merging document...");
        return PdfTools.MergePdfs(new[] { cover, contents }.Concat(bodyPdfs));
    }

    // ─────────────────────────────────────────────────────────────────────
    // Section fetchers
    // ─────────────────────────────────────────────────────────────────────

    private static async Task<byte[]> FetchWorkSummaryAsync(IJmsDataClient jms, int jobId, int reportId, int companyId)
    {
        return await jms.GetWorkSummaryReportAsync(jobId, reportId, companyId);
    }

    private async Task<byte[]?> FetchNotSeenAsync(IJmsDataClient jms, int jobId, int reportId, int companyId)
    {
        var bytes = await jms.GetNotSeenReportAsync(jobId, reportId, companyId);
        return await ExcludeIfEmptyAsync(bytes, "Not Seen");
    }

    private async Task<byte[]?> FetchErfaReportAsync(IJmsDataClient jms, int jobId, int reportId, int companyId)
    {
        var bytes = await jms.GetErfaReportAsync(jobId, reportId, companyId);
        return IsErfaEmpty(bytes, _logger) ? null : bytes;
    }

    /// <summary>
    /// For each test equipment item, downloads its latest calibration certificate followed
    /// by its most recent inspection, and merges everything into one PDF. Anything missing
    /// or not downloadable as a PDF is skipped and reported through <paramref name="warnings"/>.
    /// Returns null when nothing was downloaded.
    /// </summary>
    private async Task<byte[]?> FetchTestEquipmentDocsAsync(
        IJmsDataClient jms, IReadOnlyList<TestEquipmentStatus> testEquipment, ICollection<string>? warnings)
    {
        var pdfs = new List<byte[]>();

        foreach (var te in testEquipment.OrderBy(t => t.EquipmentId))
        {
            var label = $"test equipment {te.EquipmentId} ({te.SerialNo ?? "no serial"})";

            // ── Calibration certificate ──
            if (string.IsNullOrWhiteSpace(te.CertificateFilename))
            {
                warnings?.Add($"No calibration certificate on file for {label}.");
            }
            else
            {
                var bytes = await jms.GetCertificateAsync(te.CertificateFilename);
                if (PdfTools.LooksLikePdf(bytes))
                    AddIfReadable(pdfs, bytes, $"the calibration certificate for {label} ({te.CertificateFilename})", warnings);
                else
                {
                    _logger.LogWarning("Could not download certificate {File} for {Label}", te.CertificateFilename, label);
                    warnings?.Add($"Could not download the calibration certificate for {label} ({te.CertificateFilename}).");
                }
            }

            // ── Last inspection ──
            if (te.LastInspectionId == null)
            {
                warnings?.Add($"No inspection found for {label}.");
                continue;
            }

            var inspection = await jms.GetDetailedInspectionReportAsync(te.LastInspectionId.Value);
            if (PdfTools.LooksLikePdf(inspection))
                AddIfReadable(pdfs, inspection, $"the last inspection ({te.LastInspectionId}) for {label}", warnings);
            else
            {
                _logger.LogWarning("Inspection {InspectionId} for {Label} didn't come back as a PDF", te.LastInspectionId, label);
                warnings?.Add($"Could not download the last inspection ({te.LastInspectionId}) for {label}.");
            }
        }

        return pdfs.Count == 0 ? null : PdfTools.MergePdfs(pdfs);
    }

    /// <summary>
    /// The ERFA report is a single table (Location / Serial No / ... / Status) followed
    /// by an "I request the above actions be taken..." sign-off block, so text-order
    /// checks don't work — the sign-off and footer always follow the header. Instead,
    /// look for any word positioned between the header row and the sign-off line.
    /// Anything unexpected (unreadable PDF, markers not found) counts as not empty,
    /// so a real report is never dropped.
    /// </summary>
    public static bool IsErfaEmpty(byte[]? bytes, ILogger? logger = null)
    {
        if (bytes == null || bytes.Length == 0) return true;

        try
        {
            using var pdf = UglyToad.PdfPig.PdfDocument.Open(bytes);

            // Rows that overflow onto a second page mean there is data.
            if (pdf.NumberOfPages != 1) return false;

            var words = pdf.GetPage(1).GetWords().ToList();

            // PDF y runs bottom-up: the header sits above (higher y than) the sign-off.
            // Topmost "Status" is the column header; lowest "request" is the sign-off.
            var header = words.Where(w => w.Text == "Status").MaxBy(w => w.BoundingBox.Bottom);
            var signOff = words.Where(w => w.Text == "request").MinBy(w => w.BoundingBox.Top);
            if (header == null || signOff == null || signOff.BoundingBox.Top >= header.BoundingBox.Bottom)
            {
                logger?.LogInformation("ERFA report: could not find the table header or sign-off — including it anyway");
                return false;
            }

            var rowWords = words.Count(w =>
                w.BoundingBox.Centroid.Y < header.BoundingBox.Bottom - 1 &&
                w.BoundingBox.Centroid.Y > signOff.BoundingBox.Top + 1);

            if (rowWords == 0) logger?.LogInformation("ERFA report has no data rows — leaving it out");
            return rowWords == 0;
        }
        catch (Exception ex)
        {
            logger?.LogWarning("Could not check whether the ERFA report is empty ({Error}) — including it anyway", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Returns null if the PDF has no data rows (i.e. nothing follows the last "Status"
    /// column header after stripping page numbers and form feeds). Used for the Not Seen
    /// report; ERFA has a sign-off block after its table, so it uses IsErfaEmpty instead.
    /// </summary>
    private async Task<byte[]?> ExcludeIfEmptyAsync(byte[]? bytes, string reportName)
    {
        if (bytes == null || bytes.Length == 0)
            return null;

        try
        {
            var tmpFile = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".pdf");
            await File.WriteAllBytesAsync(tmpFile, bytes);

            var psi = new System.Diagnostics.ProcessStartInfo("pdftotext", $"{tmpFile} -")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            using var proc = System.Diagnostics.Process.Start(psi)!;
            var text = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();
            File.Delete(tmpFile);

            // pdftotext renders columns out of visual order so we can't rely on
            // positional checks. Strip page numbers and form feeds, then check if
            // anything meaningful follows the last "Status" column header.
            var cleaned = System.Text.RegularExpressions.Regex
                .Replace(text, @"Page \d+", "")
                .Replace("", "")
                .Trim();

            var lastStatusIdx = cleaned.LastIndexOf("Status", StringComparison.OrdinalIgnoreCase);
            if (lastStatusIdx >= 0)
            {
                var afterStatus = cleaned[(lastStatusIdx + "Status".Length)..].Trim();
                if (string.IsNullOrWhiteSpace(afterStatus))
                {
                    _logger.LogInformation("{Report} report has no data rows — leaving it out", reportName);
                    return null;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not check whether the {Report} report is empty ({Error}) — including it anyway", reportName, ex.Message);
        }

        return bytes;
    }

    private async Task<byte[]> FetchDetailedInspectionReportAsync(
        IJmsDataClient jms, int jobId, int reportId, int companyId, int userId,
        IProgress<string>? progress)
    {
        // Step 1: Note the newest notification before submitting so polling only
        //         matches the notification for this run — an earlier "PDF Complete"
        //         for the same job would otherwise match instantly with a stale report.
        var latestCsv = await jms.PostQueryForCsvAsync(JmsQueries.LatestNotificationIdQuery(userId));
        var latestLines = latestCsv.Content?.Trim().Split('\n') ?? Array.Empty<string>();
        if (!latestCsv.Success || latestLines.Length < 2 || !int.TryParse(latestLines[1].Trim().Trim('"'), out var afterNotificationId))
            throw new Exception("Could not read existing notifications before queuing the detailed inspection report.");

        _logger.LogDebug("Queuing detailed inspection report for job {JobId} (report {ReportId}, after notification {AfterId})",
            jobId, reportId, afterNotificationId);

        var submitResponse = await jms.QueueDetailedInspectionReportAsync(jobId, reportId, companyId);

        if (!submitResponse.Success)
            throw new Exception($"Failed to queue detailed inspection job: HTTP {submitResponse.StatusCode}");

        // Step 2: Poll user_notification for the 'PDF Complete' notification,
        //         filtered by job number in the message so we don't grab another job's notification.
        progress?.Report("Waiting for report to generate...");
        var downloadUrl = await PollForDetailedReportUrlAsync(jms, userId, jobId, afterNotificationId, progress);

        // Step 3: Download the PDF from notification_link
        progress?.Report("Downloading detailed inspection report...");
        var bytes = await jms.DownloadAsync(downloadUrl);
        _logger.LogDebug("Detailed inspection report for job {JobId}: {Bytes} bytes", jobId, bytes.Length);
        return bytes;
    }

    private async Task<string> PollForDetailedReportUrlAsync(
        IJmsDataClient jms,
        int userId,
        int jobId,
        int afterNotificationId,
        IProgress<string>? progress)
    {
        const int maxAttempts = 30;
        const int delayMs = 5000; // poll every 5 seconds

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            progress?.Report($"Waiting for report to generate... ({attempt * delayMs / 1000}s)");

            // Also select notification_message so we can log it and verify the job number match.
            var query = JmsQueries.DetailedReportNotificationQuery(userId, jobId, afterNotificationId);

            var csv = await jms.PostQueryForCsvAsync(query);

            var lines = (csv.Content ?? "").Trim().Split('\n');

            if (lines.Length >= 2)
            {
                var parts = lines[1].Split(',', 2);
                var url = parts[0].Trim();
                var message = parts.Length > 1 ? parts[1].Trim() : "(no message)";

                if (!string.IsNullOrWhiteSpace(url))
                {
                    _logger.LogDebug("Detailed inspection report ready: {Message}", message);
                    return url;
                }
            }
            else
            {
                _logger.LogDebug("Detailed inspection report not ready yet (attempt {Attempt}/{Max})", attempt, maxAttempts);
            }

            await Task.Delay(delayMs);
        }

        throw new Exception("Timed out waiting for detailed inspection report to generate.");
    }

    // ─────────────────────────────────────────────────────────────────────
    // Generated pages
    // ─────────────────────────────────────────────────────────────────────

    // Lines above "Routine Service Job - ..." for each report type.
    private static string[] StandardLines(ServiceReportType type) => type switch
    {
        ServiceReportType.MedicalDevice => new[] { "AS3551:2012" },
        ServiceReportType.ClassWiring => new[] { "AS/NZS3003:2018", "Class Wiring" },
        ServiceReportType.MedicalGas => new[] { "AS/NZS2896:2021" },
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    private static async Task<(string? Name, string? Address)> FetchSiteAsync(IJmsDataClient jms, int companyId)
    {
        var csv = await jms.PostQueryForCsvAsync(JmsQueries.CompanyCoverQuery(companyId));
        if (!csv.Success || string.IsNullOrWhiteSpace(csv.Content)) return (null, null);

        using var reader = new StringReader(csv.Content);
        using var parser = new CsvReader(reader, CultureInfo.InvariantCulture);
        if (!parser.Read()) return (null, null);
        parser.ReadHeader();
        if (!parser.Read()) return (null, null);
        return (parser.GetField("company_name"), parser.GetField("address"));
    }

    private static byte[] GenerateTitlePage(int jobId, CoverPageDetails details, string? siteName, string? siteAddress)
    {
        // Load the blank title page template and overlay the report details on top.
        // Template path: Resources/Templates/BlankTitlePage.pdf
        var templatePath = Path.Combine(AppContext.BaseDirectory, "Resources", "Templates", "BlankTitlePage.pdf");
        using var templateMs = new MemoryStream(File.ReadAllBytes(templatePath));
        using var template = PdfReader.Open(templateMs, PdfDocumentOpenMode.Import);

        using var document = new PdfDocument();
        var page = document.AddPage(template.Pages[0]);

        using var gfx = XGraphics.FromPdfPage(page);

        // Sizes and gaps copied from the hand-made examples the team currently uses.
        var headerFont = new XFont("Arial", 32, XFontStyle.Bold);
        var atFont = new XFont("Arial", 24, XFontStyle.Regular);
        var siteFont = new XFont("Arial", 30, XFontStyle.Underline);
        var addressFont = new XFont("Arial", 16, XFontStyle.Regular);
        var reportFont = new XFont("Arial", 24, XFontStyle.Regular);
        var dateFont = new XFont("Arial", 16, XFontStyle.Italic);

        const double margin = 40;
        var pageWidth = page.Width.Point;
        var maxWidth = pageWidth - margin * 2;
        double y = 170;

        void Draw(string? text, XFont font, double lineH)
        {
            foreach (var line in WrapText(gfx, text ?? "", font, maxWidth))
            {
                var width = gfx.MeasureString(line, font).Width;
                gfx.DrawString(line, font, XBrushes.Black, (pageWidth - width) / 2, y);
                y += lineH;
            }
        }

        // ── Header block: standard, job, area, "Inspection Report" ──
        var header = StandardLines(details.Type)
            .Append($"Routine Service Job - {details.RoutineServiceJobNo.Trim()}")
            .Concat(SplitLines(details.Area).Select(ToTitleCase))
            .Append("Inspection Report");
        foreach (var line in header) Draw(line, headerFont, 42);

        // ── Site ──
        y += 34;
        Draw("At", atFont, 76);
        Draw(ToTitleCase(siteName ?? ""), siteFont, 34);
        // JMS stores the address over several lines; the cover shows it on one.
        Draw(string.Join(", ", SplitLines(siteAddress).Select(ToTitleCase)), addressFont, 20);

        // ── Report number + date ──
        y += 75;
        Draw($"REPORT - {jobId}", reportFont, 95);
        Draw(FormatCoverDate(details.Date), dateFont, 20);

        using var ms = new MemoryStream();
        document.Save(ms);
        return ms.ToArray();
    }

    // Words that stay in capitals when title-casing JMS text: state codes plus
    // department/device acronyms that turn up in job notes.
    private static readonly HashSet<string> KeepUppercase = new(StringComparer.OrdinalIgnoreCase)
    {
        "NSW", "VIC", "QLD", "SA", "WA", "TAS", "NT", "ACT",
        "CSSD", "EMECP", "ICU", "ED", "PCA", "OEM", "RCD",
    };

    // Small joining words stay lowercase unless they start the line ("Department of Health").
    private static readonly HashSet<string> KeepLowercase = new(StringComparer.OrdinalIgnoreCase)
    {
        "of", "and", "the", "for", "at", "in", "on", "to",
    };

    // "KESTRELVALE private hospital" -> "Kestrelvale Private Hospital".
    // Words containing digits (unit numbers, postcodes) are left alone.
    private static string ToTitleCase(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text, @"[A-Za-z0-9']+", m =>
        {
            var word = m.Value;
            if (KeepUppercase.Contains(word)) return word.ToUpperInvariant();
            if (word.Any(char.IsDigit)) return word;
            if (m.Index > 0 && KeepLowercase.Contains(word)) return word.ToLowerInvariant();

            var cased = char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant();
            // O'Connell, D'Arcy
            if (cased.Length > 2 && cased[1] == '\'')
                cased = cased[..2] + char.ToUpperInvariant(cased[2]) + cased[3..];
            return cased;
        });

    private static IEnumerable<string> SplitLines(string? text) =>
        (text ?? "").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    // "24th July 2026"
    private static string FormatCoverDate(DateTime date)
    {
        var suffix = (date.Day % 100) switch
        {
            11 or 12 or 13 => "th",
            _ => (date.Day % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" },
        };
        return $"{date.Day}{suffix} {date:MMMM yyyy}";
    }

    // Greedy word wrap so a long site name or address line stays on the page.
    private static IEnumerable<string> WrapText(XGraphics gfx, string text, XFont font, double maxWidth)
    {
        if (text.Length == 0 || gfx.MeasureString(text, font).Width <= maxWidth)
        {
            yield return text;
            yield break;
        }

        var line = "";
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = line.Length == 0 ? word : line + " " + word;
            if (line.Length > 0 && gfx.MeasureString(candidate, font).Width > maxWidth)
            {
                yield return line;
                line = word;
            }
            else
            {
                line = candidate;
            }
        }
        if (line.Length > 0) yield return line;
    }

    private static byte[] GenerateContentsPage(IReadOnlyList<(string Title, int Page)> entries)
    {
        // Same background as the section divider pages.
        var templatePath = Path.Combine(AppContext.BaseDirectory, "Resources", "Templates", "BlankFillerPage.pdf");
        using var templateMs = new MemoryStream(File.ReadAllBytes(templatePath));
        using var template = PdfReader.Open(templateMs, PdfDocumentOpenMode.Import);

        using var document = new PdfDocument();
        var page = document.AddPage(template.Pages[0]);

        using var gfx = XGraphics.FromPdfPage(page);

        var headingFont = new XFont("Arial", 32, XFontStyle.Bold);
        var entryFont = new XFont("Arial", 18, XFontStyle.Regular);
        var leaderBrush = new XSolidBrush(XColor.FromArgb(110, 110, 110));

        double left = 60, right = page.Width.Point - 60;
        double y = 150;

        gfx.DrawString("Contents", headingFont, XBrushes.Black, left, y);
        y += 70;

        foreach (var (title, pageNo) in entries)
        {
            var pageText = pageNo.ToString();
            var titleWidth = gfx.MeasureString(title, entryFont).Width;
            var pageWidth = gfx.MeasureString(pageText, entryFont).Width;

            gfx.DrawString(title, entryFont, XBrushes.Black, left, y);
            gfx.DrawString(pageText, entryFont, XBrushes.Black, right - pageWidth, y);

            // Dot leader between the title and the page number
            var dotWidth = gfx.MeasureString(".", entryFont).Width;
            var leaderStart = left + titleWidth + 6;
            var leaderEnd = right - pageWidth - 6;
            var dots = (int)((leaderEnd - leaderStart) / dotWidth);
            if (dots > 0)
                gfx.DrawString(new string('.', dots), entryFont, leaderBrush, leaderStart, y);

            y += 42;
        }

        using var ms = new MemoryStream();
        document.Save(ms);
        return ms.ToArray();
    }

    private static byte[] GenerateFillerPage(string title)
    {
        // Section divider: the blank filler template with the section title
        // centred on it, in the same font as the cover page header.
        // Template path: Resources/Templates/BlankFillerPage.pdf
        var templatePath = Path.Combine(AppContext.BaseDirectory, "Resources", "Templates", "BlankFillerPage.pdf");
        using var templateMs = new MemoryStream(File.ReadAllBytes(templatePath));
        using var template = PdfReader.Open(templateMs, PdfDocumentOpenMode.Import);

        using var document = new PdfDocument();
        var page = document.AddPage(template.Pages[0]);

        using var gfx = XGraphics.FromPdfPage(page);

        var font = new XFont("Arial", 32, XFontStyle.Bold);
        const double margin = 40, lineH = 42;
        var pageWidth = page.Width.Point;

        var lines = WrapText(gfx, title, font, pageWidth - margin * 2).ToList();
        var y = (page.Height.Point - lines.Count * lineH) / 2 + lineH * 0.75;
        foreach (var line in lines)
        {
            var width = gfx.MeasureString(line, font).Width;
            gfx.DrawString(line, font, XBrushes.Black, (pageWidth - width) / 2, y);
            y += lineH;
        }

        using var ms = new MemoryStream();
        document.Save(ms);
        return ms.ToArray();
    }

    private static string ToRoman(int number)
    {
        var numerals = new[] { (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I") };
        var sb = new StringBuilder();
        foreach (var (value, numeral) in numerals)
            while (number >= value) { sb.Append(numeral); number -= value; }
        return sb.ToString();
    }

    // ─────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────

    // Uploaded files (scanned certificates especially) are often slightly malformed —
    // viewers repair them silently, PdfSharpCore throws. Skip the file with a warning
    // rather than failing the whole report.
    private void AddIfReadable(List<byte[]> pdfs, byte[] bytes, string description, ICollection<string>? warnings)
    {
        try
        {
            pdfs.Add(PdfTools.EnsureImportable(bytes, _logger));
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not read {Description}: {Error}", description, ex.Message);
            warnings?.Add($"Could not read {description} — the file appears to be damaged.");
        }
    }
}
