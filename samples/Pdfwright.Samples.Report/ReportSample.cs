using System.Globalization;

namespace Pdfwright.Samples;

/// <summary>
/// A monthly report of several pages: a heading, a row of KPIs in a callout, a bar chart drawn on the canvas, a table of invoices that runs over several pages with its header
/// repeated, a header and a footer with "Page x of y", and a section in Arabic with Latin words in it. It is written tagged (the default), and its title and language make it
/// PDF/UA-1. It uses only the public API of Pdfwright, and the test suite builds the same
/// document (<c>ReportSampleTests</c>) so that this file is known to work.
/// </summary>
public static class ReportSample
{
    private static readonly string[] FontFiles = ["NotoSans-Regular.ttf", "NotoSans-Bold.ttf", "NotoSansArabic-Regular.ttf"];

    private static readonly Color Blue = Color.FromHex("#3366CC");

    /// <summary>Makes the font collection of the report from the Noto fonts in a folder. The caller owns the collection and disposes it when it is done with it.</summary>
    /// <param name="directory">The folder with <c>NotoSans-Regular.ttf</c>, <c>NotoSans-Bold.ttf</c> and <c>NotoSansArabic-Regular.ttf</c>.</param>
    public static FontCollection LoadFonts(string directory)
    {
        FontCollection fonts = new();
        foreach (string file in FontFiles)
        {
            fonts.AddFile(Path.Combine(directory, file));
        }

        return fonts;
    }

    /// <summary>Makes the report. Nothing is laid out until the document is saved.</summary>
    /// <param name="fonts">The fonts, made by <see cref="LoadFonts"/>.</param>
    /// <param name="logoBytes">The bytes of a JPEG, PNG or WebP file, for the header.</param>
    public static PdfDocument Build(FontCollection fonts, byte[] logoBytes)
    {
        PdfImage logo = PdfImage.FromBytes(logoBytes);

        PdfDocument document = new(new PdfDocumentOptions
        {
            Fonts = fonts,
            DefaultStyle = new TextStyle { Family = "Noto Sans", Size = Length.Pt(10), Language = "en" },
            PageSize = PageSize.A4,
            Margins = Margins.Mm(top: 25, right: 15, bottom: 20, left: 15),
            Title = "Monthly report",
            Subject = "Sales, October 2026",
            Language = "en",
            Deterministic = true,
            Header = page => page.IsFirst
                ? null
                : new Columns(
                [
                    new Image(logo) { Width = Length.Pt(24) },
                    new Paragraph("Monthly report") { Alignment = TextAlignment.End, Style = new TextStyle { Color = Color.MidGray } },
                ]),
            Footer = page => new Paragraph($"Page {page.Number} of {page.Count}") { Alignment = TextAlignment.End, Style = new TextStyle { Size = Length.Pt(8), Color = Color.MidGray } },
        });

        document.Add(new Heading(1, "Monthly report"));
        document.Add(new Paragraph(
        [
            new TextRun("October 2026. Sales rose "),
            new TextRun("12%", new TextStyle { Weight = 700, Color = Color.Green }),
            new TextRun(" over the quarter, led by the northern region. The figures below are made up for this sample."),
        ]));

        document.Add(Kpis());
        document.Add(new Heading(2, "Sales by month"));
        document.Add(SalesChart([4, 7, 5, 9, 6], ["Jun", "Jul", "Aug", "Sep", "Oct"]));
        document.Add(new Heading(2, "Invoices"));
        document.Add(Invoices(rowCount: 90));
        document.Add(new Heading(2, "ملخص بالعربية") { Direction = TextDirection.RightToLeft });
        foreach (Block block in Arabic())
        {
            document.Add(block);
        }

        return document;
    }

    private static Block Kpis() =>
        new Callout(new Columns(
        [
            Kpi("Revenue", "1.2 M", Color.Black),
            Kpi("Orders", "8,430", Color.Black),
            Kpi("Returns", "2.1%", Color.Red),
        ]))
        {
            Fill = Color.FromHex("#E8F0FE"),
            Border = new BorderLine(Length.Pt(0.75), Blue),
            CornerRadius = Length.Pt(4),
            KeepTogether = true,
        };

    private static Paragraph Kpi(string label, string value, Color color) =>
        new([new TextRun(label + "\n"), new TextRun(value, new TextStyle { Size = Length.Pt(16), Weight = 700, Color = color })]);

    private static Drawing SalesChart(double[] values, string[] labels)
    {
        TextStyle small = new() { Size = Length.Pt(8) };
        return new Drawing(Length.Mm(120), Length.Mm(50), canvas =>
        {
            double baseline = canvas.Height - 14;
            double step = canvas.Width / values.Length;
            canvas.Stroke(new DrawingPathBuilder().MoveTo(0, baseline).LineTo(canvas.Width, baseline).Build(), Color.MidGray, 0.5);

            for (int i = 0; i < values.Length; i++)
            {
                double height = values[i] / 10 * (baseline - 14);
                double x = (i * step) + 6;
                canvas.Fill(new DrawingPathBuilder().Rectangle(x, baseline - height, step - 12, height).Build(), Blue);
                canvas.DrawText(values[i].ToString(CultureInfo.InvariantCulture), x + ((step - 12) / 2), baseline - height - 3, small, TextAlignment.Center);
                canvas.DrawText(labels[i], x + ((step - 12) / 2), canvas.Height - 3, small, TextAlignment.Center);
            }
        })
        { AltText = "Bar chart of sales by month" };
    }

    private static Table Invoices(int rowCount) =>
        new(
            [new TableColumn(ColumnWidth.Fraction(1)), new TableColumn(ColumnWidth.Fixed(Length.Mm(30))) { Alignment = TextAlignment.End }, new TableColumn()],
            new TableRow("Customer", "Amount", "Status"),
            Enumerable.Range(1, rowCount).Select(i => new TableRow($"Customer {i}", ((i * 37 % 1000) + 0.5m).ToString("N2", CultureInfo.InvariantCulture), i % 3 == 0 ? "Open" : "Paid")))
        {
            Borders = Borders.Horizontal,
            HeaderFill = Color.Gray(0.9),
            ZebraFill = Color.Gray(0.97),
        };

    private static IEnumerable<Block> Arabic()
    {
        TextStyle arabic = new() { Family = "Noto Sans Arabic", Size = Length.Pt(11), Language = "ar" };
        yield return new Paragraph("ارتفعت المبيعات بنسبة 12% خلال الربع، وكان أداء المنطقة الشمالية الأفضل. تظهر الرموز اللاتينية مثل Q3 وINV-2026-0042 في مكانها الصحيح داخل السطر.")
        {
            Style = arabic,
            Direction = TextDirection.RightToLeft,
        };
        yield return new Table(
            [new TableColumn(ColumnWidth.Fraction(1)), new TableColumn(ColumnWidth.Fixed(Length.Mm(30))) { Alignment = TextAlignment.End }],
            new TableRow("العميل", "المبلغ"),
            [new TableRow("شركة الأفق", "1,200.00"), new TableRow("مؤسسة النور", "845.50"), new TableRow("Contoso Ltd", "310.25")])
        {
            Direction = TextDirection.RightToLeft,
            Style = arabic,
            Borders = Borders.Horizontal,
            HeaderFill = Color.Gray(0.9),
        };
    }
}
