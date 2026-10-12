using WorkshopDashboard.Models;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace WorkshopDashboard.Util;

public class CustomsInfoItem
{
    public string SerialNumber            { get; set; } = "";
    public string ModelLabel              { get; set; } = "";
    public string ManufacturerName        { get; set; } = "";
    public string ManufacturerAddress     { get; set; } = "";
    public string RegistrationNumber      { get; set; } = "";
    public string HtsCode                 { get; set; } = "";
    public string FdaProductCode          { get; set; } = "";
    public string Fda510k                 { get; set; } = "";
    public string RegulationNumber        { get; set; } = "";
    public string CountryOfOrigin         { get; set; } = "";
    public string Description             { get; set; } = "";
    public string DeclaredValue           { get; set; } = "";
}

public class CustomsInfoFormGenerator
{
    static CustomsInfoFormGenerator()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    /// <summary>
    /// Generates the customs paperwork (for attaching to a DHL booking) and returns it as a
    /// byte array. Page 1 lists each unit — one block per unit, since the description text is
    /// long enough that a compressed table isn't legible — under a single shared "reason for
    /// export" statement; the final page is the declaration (logo, shipping/biological-material
    /// statements, certification + signature block).
    /// </summary>
    public byte[] Generate(List<CustomsInfoItem> items, string destinationCompany, byte[]? logoBytes = null, string signedByName = "")
    {
        return Document.Create(container =>
        {
            container.Page(page => ComposeEquipmentPage(page, items));
            container.Page(page => ComposeDeclarationPage(page, destinationCompany, logoBytes, signedByName));
        })
        .GeneratePdf();
    }

    private void ComposeDeclarationPage(PageDescriptor page, string destinationCompany, byte[]? logoBytes, string signedByName)
    {
        page.Size(PageSizes.A4);
        page.Margin(2f, Unit.Centimetre);
        page.DefaultTextStyle(x => x.FontSize(11).FontFamily("Arial"));

        page.Content().Column(col =>
        {
            if (logoBytes is not null)
                col.Item().Height(50).Image(logoBytes).FitArea();
            else
                col.Item().Text(CompanyProfile.Name).Bold().FontSize(22);

            var destination = string.IsNullOrWhiteSpace(destinationCompany) ? "[destination company]" : destinationCompany;

            col.Item().PaddingTop(30).Text(
                $"Medical equipment sent to {destination} for repair purposes only and are to be returned to {CompanyProfile.LegalName} in Australia.")
                .FontSize(12).LineHeight(1.5f);

            col.Item().PaddingTop(20).Text("The medical equipment has been used with human material only and does not contain any biological material.")
                .FontSize(12).Bold().Underline().FontColor(Colors.Red.Medium);

            col.Item().PaddingTop(80).Column(cert =>
            {
                cert.Item().Text("The above information is true and correct to the best of my knowledge.").FontSize(10.5f);

                cert.Item().PaddingTop(50).Row(row =>
                {
                    row.RelativeItem().Column(sig =>
                    {
                        if (!string.IsNullOrWhiteSpace(signedByName))
                            sig.Item().PaddingBottom(2).Text(signedByName).FontSize(22).FontFamily("Segoe Script");
                        else
                            sig.Item().Height(28);

                        sig.Item().PaddingBottom(4).LineHorizontal(1).LineColor(Colors.Black);
                        sig.Item().Text("Signature").FontSize(9).FontColor(Colors.Grey.Darken2);
                    });

                    row.ConstantItem(40);

                    row.RelativeItem().Column(date =>
                    {
                        date.Item().PaddingBottom(2).Text(DateTime.Today.ToString("dd MMMM yyyy")).FontSize(12);
                        date.Item().PaddingBottom(4).LineHorizontal(1).LineColor(Colors.Black);
                        date.Item().Text("Date").FontSize(9).FontColor(Colors.Grey.Darken2);
                    });
                });
            });
        });
    }

    private void ComposeEquipmentPage(PageDescriptor page, List<CustomsInfoItem> items)
    {
        page.Size(PageSizes.A4);
        page.Margin(1.5f, Unit.Centimetre);
        page.DefaultTextStyle(x => x.FontSize(9).FontFamily("Arial"));

        page.Content().Column(col =>
        {
            ComposeHeader(col);
            ComposeReasonForExport(col);

            foreach (var item in items)
                ComposeItem(col, item);
        });
    }

    private void ComposeHeader(ColumnDescriptor col)
    {
        col.Item().Text("Proforma Invoice - Additional Information").Bold().FontSize(18);
        col.Item().Text("Scopes & Surgical Tools").FontSize(11).FontColor(Colors.Grey.Darken2);
        col.Item().PaddingVertical(4).LineHorizontal(1).LineColor(Colors.Grey.Lighten1);
    }

    // One shared statement for the whole shipment, rather than repeated per unit.
    private void ComposeReasonForExport(ColumnDescriptor col)
    {
        col.Item().PaddingTop(6).PaddingBottom(6).Column(block =>
        {
            block.Item().Text("Reason for Export:").Bold().FontSize(9.5f);
            block.Item().Text("Intended Use: 170.000 (Repair & Re-export) //").FontSize(9).LineHeight(1.4f);
            block.Item().Text("AofC Code: ERR: RPR //").FontSize(9).LineHeight(1.4f);
            block.Item().Text("Device Listings (LST): LST: UNK //").FontSize(9).LineHeight(1.4f);
        });

        col.Item().PaddingBottom(4).LineHorizontal(1).LineColor(Colors.Grey.Lighten1);
    }

    private void ComposeItem(ColumnDescriptor col, CustomsInfoItem item)
    {
        col.Item().PaddingTop(10).Column(block =>
        {
            block.Item().Text($"{item.ModelLabel} — SN: {item.SerialNumber}")
                .Bold().FontSize(11);

            block.Item().PaddingTop(4).Text(item.Description).FontSize(9).LineHeight(1.3f);

            block.Item().PaddingTop(6).Table(table =>
            {
                table.ColumnsDefinition(cols =>
                {
                    cols.RelativeColumn(1.3f);
                    cols.RelativeColumn(2.0f);
                    cols.RelativeColumn(1.3f);
                    cols.RelativeColumn(2.0f);
                });

                void Field(string label, string value)
                {
                    table.Cell().Element(LabelCell).Text(label).FontSize(8).FontColor(Colors.Grey.Darken2);
                    table.Cell().Element(ValueCell).Text(string.IsNullOrWhiteSpace(value) ? "—" : value).FontSize(8.5f);
                }

                Field("Manufacturer", item.ManufacturerName);
                Field("Registration No.", item.RegistrationNumber);
                Field("Manufacturer Address", item.ManufacturerAddress);
                Field("Country of Origin", item.CountryOfOrigin);
                Field("HTS Code", item.HtsCode);
                Field("FDA Product Code", item.FdaProductCode);
                Field("FDA 510(k)", item.Fda510k);
                Field("Regulation No.", item.RegulationNumber);
                Field("Declared Value", item.DeclaredValue);
            });
        });

        col.Item().PaddingTop(8).LineHorizontal(0.5f).LineColor(Colors.Grey.Lighten2);
    }

    private static IContainer LabelCell(IContainer c) => c.Padding(3);
    private static IContainer ValueCell(IContainer c) => c.Padding(3);
}
