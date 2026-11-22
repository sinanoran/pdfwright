using Pdfwright.Tests.Images;
using DrawingBlock = Pdfwright.Drawing;

namespace Pdfwright.Tests.Document;

/// <summary>
/// Makes the sample reports of the README and a right-to-left one, as real, compressed files, and checks them. With <c>PDFWRIGHT_SAMPLES_DIR</c> set the files are also written there, so that
/// a person (or the pdf.js script of <c>tools/pdfjs-check</c>) can look at them.
/// </summary>
public class SampleDocumentTests
{
    private static PdfDocumentOptions Options(Action<DocumentTestKit.OptionsBuilder>? change = null) =>
        DocumentTestKit.Options(b =>
        {
            b.Compress = true;
            b.Deterministic = true;
            change?.Invoke(b);
        });

    private static void Keep(string name, byte[] pdf)
    {
        string? directory = Environment.GetEnvironmentVariable("PDFWRIGHT_SAMPLES_DIR");
        if (!string.IsNullOrEmpty(directory))
        {
            _ = Directory.CreateDirectory(directory);
            File.WriteAllBytes(Path.Combine(directory, name + ".pdf"), pdf);
        }
    }

    private static PdfImage Logo() => PdfImage.FromBytes(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Images", "Files", "gradient-24x16.jpg")));

    /// <summary>The sample report of the README, also the golden file of the report (S6a).</summary>
    internal static PdfDocument ReadmeReport(Action<DocumentTestKit.OptionsBuilder>? change = null)
    {
        PdfImage logo = Logo();
        PdfDocument document = new(Options(b =>
        {
            b.Title = "Monthly report";
            b.Language = "en";
            b.Header = page => page.IsFirst
                ? null
                : new Columns([new Image(logo) { Width = Length.Pt(24) }, new Paragraph("Monthly report") { Alignment = TextAlignment.End, Style = new TextStyle { Color = Color.MidGray } }]);
            b.Footer = page => new Paragraph($"{page.Number} / {page.Count}") { Alignment = TextAlignment.End };
            change?.Invoke(b);
        }));
        document.Add(new Heading(1, "Monthly report"));
        document.Add(new Paragraph("Sales rose 12% over the quarter. " + DocumentTestKit.LongText(60)));
        document.Add(new Callout(new Columns(
        [
            new Paragraph([new TextRun("Revenue\n"), new TextRun("1.2 M", new TextStyle { Size = Length.Pt(16), Weight = 700 })]),
            new Paragraph([new TextRun("Orders\n"), new TextRun("8,430", new TextStyle { Size = Length.Pt(16), Weight = 700 })]),
            new Paragraph([new TextRun("Returns\n"), new TextRun("2.1%", new TextStyle { Size = Length.Pt(16), Weight = 700, Color = Color.Red })]),
        ])) { CornerRadius = Length.Pt(4), KeepTogether = true });
        document.Add(new Heading(2, "Invoices"));
        document.Add(new Table(
            [new TableColumn(ColumnWidth.Fraction(1)), new TableColumn(ColumnWidth.Fixed(Length.Mm(30))) { Alignment = TextAlignment.End }, new TableColumn()],
            new TableRow("Customer", "Amount", "Status"),
            Enumerable.Range(0, 70).Select(i => new TableRow($"Customer {i}", $"{i * 37 % 1000}.50", i % 3 == 0 ? "Open" : "Paid")))
        {
            Borders = Borders.Horizontal,
            HeaderFill = Color.Gray(0.9),
            ZebraFill = Color.Gray(0.97),
        });
        double[] sales = [4, 7, 5, 9, 6];
        string[] months = ["Jun", "Jul", "Aug", "Sep", "Oct"];
        document.Add(new Heading(2, "Sales by month"));
        document.Add(new DrawingBlock(Length.Mm(120), Length.Mm(50), canvas =>
        {
            double step = canvas.Width / sales.Length;
            DrawingPen axis = new(Color.MidGray, 0.5);
            canvas.Stroke(new DrawingPathBuilder().MoveTo(0, canvas.Height - 14).LineTo(canvas.Width, canvas.Height - 14).Build(), axis);
            for (int i = 0; i < sales.Length; i++)
            {
                double height = sales[i] / 10 * (canvas.Height - 24);
                canvas.Fill(new DrawingPathBuilder().Rectangle((i * step) + 6, canvas.Height - 14 - height, step - 12, height).Build(), Color.FromHex("#3366CC"));
                canvas.DrawText(months[i], (i * step) + (step / 2), canvas.Height - 3, alignment: TextAlignment.Center);
                canvas.DrawText(sales[i].ToString(System.Globalization.CultureInfo.InvariantCulture), (i * step) + (step / 2), canvas.Height - 18 - height, new TextStyle { Size = Length.Pt(8) }, TextAlignment.Center);
            }
        })
        { AltText = "Bar chart of sales by month" });
        document.Add(new Image(logo) { Width = Length.Mm(30), AltText = "Logo" });
        return document;
    }

    [Fact]
    public void The_report_of_the_readme_is_a_valid_file_with_every_part_in_it()
    {
        PdfDocument document = ReadmeReport();

        byte[] pdf = DocumentTestKit.Save(document);

        Keep("report", pdf);
        string[] pages = DocumentTestKit.PageTexts(pdf);
        Assert.True(pages.Length >= 3);
        Assert.Contains($"1 / {pages.Length}", pages[0]);
        Assert.Contains($"{pages.Length} / {pages.Length}", pages[^1]);
        Assert.Contains("Revenue", string.Concat(pages));
        Assert.Contains("Oct", string.Concat(pages));
        DocumentTestKit.AssertValid(pdf, "sample-report");
    }

    [Fact]
    public void A_right_to_left_report_with_columns_a_split_callout_and_a_footer_is_a_valid_file()
    {
        PdfDocument document = new(Options(b =>
        {
            b.Direction = TextDirection.RightToLeft;
            b.Language = "ar";
            b.DefaultStyle = new TextStyle { Family = "Noto Sans Arabic", Size = Length.Pt(11), Language = "ar" };
            b.Footer = page => new Paragraph($"{page.Number} / {page.Count}") { Alignment = TextAlignment.Center };
        }));
        document.Add(new Heading(1, "تقرير المبيعات الشهري"));
        document.Add(new Columns(
        [
            new Callout(new Paragraph("المبيعات")) { Fill = Color.FromHex("#E8F0FE") },
            new Callout(new Paragraph("الطلبات")) { Fill = Color.FromHex("#E6F4EA") },
            new Callout(new Paragraph("المرتجعات")) { Fill = Color.FromHex("#FCE8E6") },
        ]));
        document.Add(new Callout(Enumerable.Range(0, 60).Select(i => (Block)new Paragraph($"سطر رقم {i} من الملاحظة الطويلة التي تمتد عبر عدة صفحات")))
        {
            CornerRadius = Length.Pt(6),
            Border = new BorderLine(Length.Pt(1), Color.FromHex("#3366CC")),
        });

        byte[] pdf = DocumentTestKit.Save(document);

        Keep("report-rtl", pdf);
        string[] pages = DocumentTestKit.PageTexts(pdf);
        Assert.True(pages.Length >= 2);
        Assert.Empty(document.Diagnostics.MissingGlyphs);
        DocumentTestKit.AssertValid(pdf, "sample-rtl");
    }

    [Fact]
    public void A_page_of_picture_blocks_with_transparency_is_a_valid_file()
    {
        PdfImage png = PdfImage.FromBytes(ImageTestKit.Png(16, 16, 8, 6));
        PdfDocument document = new(Options(b => b.Footer = page => new Paragraph($"Page {page.Number} of {page.Count}")));
        document.Add(new Heading(1, "Pictures"));
        document.Add(new Callout(new Image(png) { Width = Length.Mm(40), Alignment = TextAlignment.Center }) { Fill = Color.FromHex("#FFF3CD") });
        document.Add(new Columns([[new Image(png) { Width = Length.Mm(20) }], [new Image(Logo()) { Width = Length.Mm(20) }], [new Paragraph("Captions")]]));
        document.Add(new DrawingBlock(Length.Mm(60), Length.Mm(30), canvas =>
        {
            canvas.Fill(new DrawingPathBuilder().Rectangle(0, 0, canvas.Width, canvas.Height).Build(), Color.Gray(0.85));
            canvas.DrawImage(png, 10, 10, 60, 60, 0.6);
            canvas.Save();
            canvas.Translate(120, 40);
            canvas.Rotate(20);
            canvas.Stroke(new DrawingPathBuilder().Rectangle(-20, -10, 40, 20).Build(), new DrawingPen(Color.Red, 2, dashes: [5, 3]));
            canvas.Restore();
        }));

        byte[] pdf = DocumentTestKit.Save(document);

        Keep("pictures", pdf);
        DocumentTestKit.AssertValid(pdf, "sample-pictures");
    }
}
