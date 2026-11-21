using System.Globalization;

namespace Pdfwright.Benchmarks.Scenarios;

/// <summary>
/// The two measured documents, built only through the public API. <see cref="Table"/> is the baseline scenario
/// (1,000 rows by 6 columns, A4 portrait, a repeated header, a "Page x of y" footer); <see cref="Report"/> is a
/// report of about ten pages with headings, paragraphs, two tables, two charts, a header and a footer, in Latin and Arabic.
/// </summary>
internal static class ScenarioDocuments
{
    private static readonly string[] FontFiles = ["NotoSans-Regular.ttf", "NotoSans-Bold.ttf", "NotoSansArabic-Regular.ttf"];

    private static readonly Color Blue = Color.FromHex("#3366CC");

    internal static FontCollection LoadFonts()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "fonts");
        FontCollection fonts = new();
        foreach (string file in FontFiles)
        {
            fonts.Add(File.ReadAllBytes(Path.Combine(directory, file)));
        }

        return fonts;
    }

    /// <summary>The 1,000-row table of the baselines. The Chromium baseline printed with its default header and footer (title and date, page x/y); here the header carries the title and the footer "Page x of y".</summary>
    internal static PdfDocument Table(FontCollection fonts, int rows = 1000)
    {
        PdfDocument document = new(new PdfDocumentOptions
        {
            Fonts = fonts,
            DefaultStyle = new TextStyle { Family = "Noto Sans", Size = Length.Pt(10), Language = "en" },
            PageSize = PageSize.A4,
            Margins = Margins.Mm(top: 20, right: 8, bottom: 20, left: 8),
            Title = "Report",
            Language = "en",
            Header = page => new Paragraph("Report") { Style = new TextStyle { Size = Length.Pt(8), Color = Color.MidGray } },
            Footer = page => new Paragraph($"Page {page.Number} of {page.Count}") { Alignment = TextAlignment.End, Style = new TextStyle { Size = Length.Pt(8), Color = Color.MidGray } },
        });

        document.Add(new Heading(1, "Report"));
        document.Add(new Table(
            Enumerable.Range(0, TableData.Columns).Select(_ => new TableColumn(ColumnWidth.Fraction(1))),
            new TableRow(TableData.Header.ToArray()),
            TableData.Rows(rows).Select(cells => new TableRow(cells)))
        {
            Borders = Borders.Horizontal,
            CellPadding = Margins.Pt(1, 3, 1, 3),
            Style = new TextStyle { Size = Length.Pt(9) },
        });
        return document;
    }

    /// <summary>A report of about ten pages: three sections, a long table that spans pages, a small table, a bar chart and a line chart, and an Arabic section with Latin words in it.</summary>
    internal static PdfDocument Report(FontCollection fonts, int invoiceRows = 200)
    {
        PdfDocument document = new(new PdfDocumentOptions
        {
            Fonts = fonts,
            DefaultStyle = new TextStyle { Family = "Noto Sans", Size = Length.Pt(10), Language = "en" },
            PageSize = PageSize.A4,
            Margins = Margins.Mm(top: 25, right: 15, bottom: 20, left: 15),
            Title = "Annual report",
            Language = "en",
            Header = page => new Paragraph("Annual report 2026") { Style = new TextStyle { Size = Length.Pt(8), Color = Color.MidGray } },
            Footer = page => new Paragraph($"Page {page.Number} of {page.Count}") { Alignment = TextAlignment.End, Style = new TextStyle { Size = Length.Pt(8), Color = Color.MidGray } },
        });

        document.Add(new Heading(1, "Annual report 2026"));
        document.Add(new Paragraph(Prose(0)));
        for (int section = 1; section <= 3; section++)
        {
            document.Add(new Heading(2, $"Section {section}"));
            for (int p = 0; p < 4; p++)
            {
                document.Add(new Paragraph(Prose((section * 10) + p)));
            }

            if (section == 1)
            {
                document.Add(new Heading(3, "Sales by quarter"));
                document.Add(BarChart([4, 7, 5, 9, 6, 8, 3, 7], ["Q1", "Q2", "Q3", "Q4", "Q5", "Q6", "Q7", "Q8"]));
            }
            else if (section == 2)
            {
                document.Add(new Heading(3, "Orders per month"));
                document.Add(LineChart([12, 18, 15, 22, 30, 27, 35, 31, 40, 38, 45, 52], ["J", "F", "M", "A", "M", "J", "J", "A", "S", "O", "N", "D"]));
            }
        }

        document.Add(new Heading(2, "Invoices"));
        document.Add(Invoices(invoiceRows));
        document.Add(new Heading(2, "Summary"));
        document.Add(Invoices(6));
        document.Add(new Heading(2, "ملخص بالعربية") { Direction = TextDirection.RightToLeft });
        TextStyle arabic = new() { Family = "Noto Sans Arabic", Size = Length.Pt(11), Language = "ar" };
        for (int p = 0; p < 3; p++)
        {
            document.Add(new Paragraph("ارتفعت المبيعات بنسبة 12 % خلال الربع، وكان أداء المنطقة الشمالية الأفضل. تظهر الأرقام الإنجليزية مثل Q3 و INV-2026-0042 في مكانها الصحيح داخل السطر.")
            {
                Style = arabic,
                Direction = TextDirection.RightToLeft,
            });
        }

        return document;
    }

    private static string Prose(int seed) =>
        string.Create(CultureInfo.InvariantCulture, $"Paragraph {seed}. ") +
        "Revenue grew over the year, led by the northern region, while costs stayed flat. The figures in this report are made up for a performance measurement. " +
        "Customers renewed at a higher rate than in the previous year, and the support team closed most tickets within a day. " +
        "A longer sentence follows so that the paragraph wraps over several lines and the line breaker has real work to do on every page of the document. " +
        "Orders, returns and refunds are shown in the tables and charts of this section, and the notes below each one explain how they were counted.";

    private static Drawing BarChart(double[] values, string[] labels)
    {
        TextStyle small = new() { Size = Length.Pt(8) };
        return new Drawing(Length.Mm(150), Length.Mm(60), canvas =>
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
        { AltText = "Bar chart of sales by quarter" };
    }

    private static Drawing LineChart(double[] values, string[] labels)
    {
        TextStyle small = new() { Size = Length.Pt(8) };
        return new Drawing(Length.Mm(150), Length.Mm(60), canvas =>
        {
            double baseline = canvas.Height - 14;
            double step = canvas.Width / values.Length;
            double max = values.Max();
            canvas.Stroke(new DrawingPathBuilder().MoveTo(0, baseline).LineTo(canvas.Width, baseline).Build(), Color.MidGray, 0.5);
            DrawingPathBuilder line = new();
            for (int i = 0; i < values.Length; i++)
            {
                double x = (i * step) + (step / 2);
                double y = baseline - (values[i] / max * (baseline - 14));
                _ = i == 0 ? line.MoveTo(x, y) : line.LineTo(x, y);
            }

            canvas.Stroke(line.Build(), Blue, 1.5);
            for (int i = 0; i < values.Length; i++)
            {
                double x = (i * step) + (step / 2);
                double y = baseline - (values[i] / max * (baseline - 14));
                canvas.Fill(new DrawingPathBuilder().Ellipse(x, y, 2.5, 2.5).Build(), Blue);
                canvas.DrawText(values[i].ToString(CultureInfo.InvariantCulture), x, y - 5, small, TextAlignment.Center);
                canvas.DrawText(labels[i], x, canvas.Height - 3, small, TextAlignment.Center);
            }
        })
        { AltText = "Line chart of orders per month" };
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
}
