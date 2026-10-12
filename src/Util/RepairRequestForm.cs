using WorkshopDashboard.Models;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace WorkshopDashboard.Util;

public class RepairItem
{
    public string Model          { get; set; } = "";
    public string SerialNo       { get; set; } = "";
    public string JobRef         { get; set; } = "";
    public string FaultDetails   { get; set; } = "";
    public string Priority       { get; set; } = "Low";
    public string ApprovalStatus { get; set; } = "Not Pre-approved";
}

public class RepairRequestFormGenerator
{
    static RepairRequestFormGenerator()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    /// <summary>
    /// Generates a Repair Request Form PDF and returns it as a byte array.
    /// </summary>
    /// <param name="items">List of repair items to include in the form.</param>
    /// <param name="dhlAwb">Optional DHL Air Waybill number.</param>
    public byte[] Generate(List<RepairItem> items, string dhlAwb = "")
    {
        return Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(1.5f, Unit.Centimetre);
                page.DefaultTextStyle(x => x.FontSize(9).FontFamily("Arial"));

                page.Content().Column(col =>
                {
                    ComposeHeader(col, dhlAwb);
                    ComposeInstructions(col);
                    ComposeTable(col, items);

                    col.Item().PaddingVertical(12);

                    ComposeFooter(col);
                });
            });
        })
        .GeneratePdf();
    }

    private void ComposeHeader(ColumnDescriptor col, string dhlAwb)
    {
        col.Item().Row(row =>
        {
            row.RelativeItem().Column(inner =>
            {
                inner.Item().Text("Repair Request Form").Bold().FontSize(18);
                inner.Item().Text("Scopes & Surgical Tools")
                    .FontSize(11).FontColor(Colors.Grey.Darken2);
            });

            row.ConstantItem(200).AlignRight().Column(inner =>
            {
                inner.Item().Text($"DHL AWB: {dhlAwb}").FontSize(9);
            });
        });

        col.Item().PaddingVertical(4).LineHorizontal(1).LineColor(Colors.Grey.Lighten1);
    }

    private void ComposeInstructions(ColumnDescriptor col)
    {
        col.Item().PaddingBottom(8).Text(text =>
        {
            text.Span("Please quote for repair and reference our ").FontSize(8.5f);
            text.Span("Job Reference Number").Bold().FontSize(8.5f);
            text.Span(" and/or ").FontSize(8.5f);
            text.Span("Purchase Order Number").Bold().FontSize(8.5f);
            text.Span(" with each quotation and invoice. Send all quotations to ").FontSize(8.5f);
            text.Span(CompanyProfile.QuotesEmail).FontColor(Colors.Blue.Medium).FontSize(8.5f);
            text.Span(".").FontSize(8.5f);
        });
    }

    private void ComposeTable(ColumnDescriptor col, List<RepairItem> items)
    {
        col.Item().Table(table =>
        {
            table.ColumnsDefinition(cols =>
            {
                cols.RelativeColumn(2.2f); // Model
                cols.RelativeColumn(1.5f); // Serial No
                cols.RelativeColumn(1.8f); // Job Ref / PO
                cols.RelativeColumn(4.0f); // Fault Details
                cols.RelativeColumn(1.2f); // Priority
                cols.RelativeColumn(2.0f); // Approval Status
            });

            table.Header(header =>
            {
                void H(string text) =>
                    header.Cell()
                          .Element(HeaderCell)
                          .Text(text).FontColor(Colors.White).Bold().FontSize(8);

                H("Model");
                H("Serial No");
                H("Job Ref /\nPurchase Order");
                H("Fault Details");
                H("Customer\nPriority");
                H("Approval\nStatus");
            });

            foreach (var item in items)
            {
                table.Cell().Element(DataCell).Text(item.Model).Bold().FontSize(8.5f);
                table.Cell().Element(DataCell).Text(item.SerialNo).FontSize(8.5f);
                table.Cell().Element(DataCell).Text(item.JobRef).FontSize(8.5f);
                table.Cell().Element(DataCell).Text(item.FaultDetails).FontSize(8.5f);
                table.Cell().Element(DataCell).Text(item.Priority).FontSize(8.5f);
                table.Cell().Element(DataCell).Text(item.ApprovalStatus).FontSize(8.5f);
            }

            // Blank rows for manual additions
            for (int i = 0; i < 3; i++)
                for (int c = 0; c < 6; c++)
                    table.Cell().Element(DataCell).Text("");
        });
    }

    private void ComposeFooter(ColumnDescriptor col)
    {
        col.Item().Row(row =>
        {
            row.RelativeItem().Column(addr =>
            {
                addr.Item().Text("Please return all equipment to:").Bold().FontSize(8.5f);
                addr.Item().PaddingTop(3)
                    .Text($"{CompanyProfile.Name}\n{CompanyProfile.Address}")
                    .FontSize(8.5f).LineHeight(1.4f);
            });

            row.RelativeItem().Column(contact =>
            {
                contact.Item().Text("For any questions or concerns, please contact:")
                    .Bold().FontSize(8.5f);
                contact.Item().PaddingTop(3).Text(text =>
                {
                    text.Line(CompanyProfile.Department).FontSize(8.5f);
                    text.Line(CompanyProfile.WorkshopEmail).FontColor(Colors.Blue.Medium).FontSize(8.5f);
                    text.Line(CompanyProfile.Phone).FontSize(8.5f);
                });
            });
        });
    }

    private static IContainer HeaderCell(IContainer c) =>
        c.Background(Colors.Grey.Darken3).Padding(5);

    private static IContainer DataCell(IContainer c) =>
        c.BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(5);
}
