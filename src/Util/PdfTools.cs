using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;

namespace WorkshopDashboard.Util;

/// <summary>Checking and merging the PDFs JMS hands back — shared by InspectionDocumentBuilder
/// (service reports) and MonthlyReportBuilder (monthly site packs).</summary>
public static class PdfTools
{
    /// <summary>An expired JMS session returns an HTML login page with a 200, so check the bytes.</summary>
    public static bool LooksLikePdf(byte[]? bytes) =>
        bytes is { Length: >= 4 } && bytes[0] == 0x25 && bytes[1] == 0x50 && bytes[2] == 0x44 && bytes[3] == 0x46;

    /// <summary>
    /// Returns the PDF unchanged if PdfSharpCore can import it; otherwise rewrites it
    /// through MuPDF (which repairs broken xref tables and similar damage) and returns
    /// the cleaned copy. Throws if even the repaired copy can't be imported.
    /// </summary>
    public static byte[] EnsureImportable(byte[] bytes, ILogger? logger = null)
    {
        try
        {
            CountPages(bytes);
            return bytes;
        }
        catch (Exception ex)
        {
            logger?.LogInformation("PdfSharpCore could not read a PDF ({Error}) — repairing it with MuPDF", ex.Message);
        }

        using var doc = new MuPDF.NET.Document(stream: bytes, fileType: "pdf");
        var repaired = doc.Write(garbage: true, clean: true);
        CountPages(repaired); // throws if still unreadable
        return repaired;
    }

    public static int CountPages(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        using var doc = PdfReader.Open(ms, PdfDocumentOpenMode.Import);
        return doc.PageCount;
    }

    public static byte[] MergePdfs(IEnumerable<byte[]> pdfs)
    {
        using var output = new PdfDocument();

        foreach (var pdfBytes in pdfs)
        {
            using var ms = new MemoryStream(pdfBytes);
            using var src = PdfReader.Open(ms, PdfDocumentOpenMode.Import);
            foreach (var page in src.Pages)
                output.AddPage(page);
        }

        using var result = new MemoryStream();
        output.Save(result);
        return result.ToArray();
    }
}
