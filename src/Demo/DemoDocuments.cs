using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace WorkshopDashboard.Demo;

/// <summary>Stand-in PDFs for the documents JMS would generate (quotes, reports, stickers,
/// certificates): clearly marked as demo output, with whatever details the request named.</summary>
public static class DemoDocuments
{
    public static byte[] Pdf(string title, params string[] lines)
    {
        QuestPDF.Settings.License = LicenseType.Community;
        return Document.Create(d => d.Page(p =>
        {
            p.Size(PageSizes.A4);
            p.Margin(2, Unit.Centimetre);
            p.DefaultTextStyle(x => x.FontSize(11));
            p.Header().Column(c =>
            {
                c.Item().Text("Workshop Dashboard — demo document").FontSize(9).FontColor(Colors.Grey.Darken1);
                c.Item().PaddingTop(6).Text(title).FontSize(20).Bold();
            });
            p.Content().PaddingTop(20).Column(c =>
            {
                foreach (var line in lines)
                    c.Item().PaddingBottom(4).Text(line);
                c.Item().PaddingTop(20).Text(
                    "In the real deployment this PDF comes from the job management system. " +
                    "The demo generates a placeholder so the download and attach flows can be tried.")
                    .FontSize(9).FontColor(Colors.Grey.Darken1);
            });
            p.Footer().AlignRight().Text($"Generated {DateTime.Now:dd/MM/yyyy HH:mm}").FontSize(8).FontColor(Colors.Grey.Medium);
        })).GeneratePdf();
    }
}
