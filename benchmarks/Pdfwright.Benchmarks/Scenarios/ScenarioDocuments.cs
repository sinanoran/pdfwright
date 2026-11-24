using System.Globalization;

namespace Pdfwright.Benchmarks.Scenarios;

/// <summary>
/// The measured documents, built only through the public API. <see cref="Table"/> is the baseline scenario
/// (1,000 rows by 6 columns, A4 portrait, a repeated header, a "Page x of y" footer); <see cref="Report"/> is a
/// report of about ten pages with headings, paragraphs, two tables, two charts, a header and a footer, in Latin and Arabic.
/// The others are the scenarios of section 7 of the plan: <see cref="Report20"/>, <see cref="Stress"/>, <see cref="TableArabic"/>
/// and <see cref="TableDevanagari"/>. The allocation-budget tests compile this file too, so that the tests and the benchmarks measure the same documents.
/// Every scenario is written tagged, as the library writes a document by default; <c>tagged: false</c> writes it untagged (the file of 0.1.5), so
/// that the two can be measured side by side, and <c>pinned: true</c> writes it uncompressed and deterministic, so that a test can pin its bytes (the
/// benchmarks write it compressed and dated by the clock, as a caller would).
/// </summary>
internal static class ScenarioDocuments
{
    private const int Report20ParagraphsPerSection = 20;

    private const int Report20Notes = 8;

    private const int StressParagraphsPerSection = 23;

    private const int StressRowsPerTable = 60;

    private static readonly string[] FontFiles = ["NotoSans-Regular.ttf", "NotoSans-Bold.ttf", "NotoSansArabic-Regular.ttf", "NotoSansDevanagari-Regular.ttf"];

    private static readonly string[] Months = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

    private static readonly Color Blue = Color.FromHex("#3366CC");

    /// <summary>Loads the fonts of the scenarios from the <c>fonts</c> folder beside the program.</summary>
    internal static FontCollection LoadFonts() => LoadFonts(Path.Combine(AppContext.BaseDirectory, "fonts"));

    /// <summary>Loads the fonts of the scenarios from <paramref name="directory"/>.</summary>
    internal static FontCollection LoadFonts(string directory)
    {
        FontCollection fonts = new();
        foreach (string file in FontFiles)
        {
            fonts.Add(File.ReadAllBytes(Path.Combine(directory, file)));
        }

        return fonts;
    }

    /// <summary>
    /// The 1,000-row table of the baselines. The Chromium baseline printed with its default header and footer (title and date, page x/y); here the header carries the title and the footer "Page x of y".
    /// </summary>
    internal static PdfDocument Table(FontCollection fonts, int rows = 1000, bool tagged = true, bool pinned = false) =>
        TableDocument(fonts, TableData.Header, TableData.Rows(rows), new TextStyle { Size = Length.Pt(9) }, null, tagged, pinned);

    /// <summary>The same table with the cell texts in Arabic (right to left, Noto Sans Arabic, Latin digits): "Row r cell c text" as "صف r خلية c نص", the columns running from the right edge.</summary>
    internal static PdfDocument TableArabic(FontCollection fonts, int rows = 1000, bool tagged = true, bool pinned = false) =>
        TableDocument(
            fonts,
            TableData.LocalizedHeader("عمود"),
            TableData.LocalizedRows(rows, "صف", "خلية", "نص"),
            new TextStyle { Family = "Noto Sans Arabic", Size = Length.Pt(9), Language = "ar" },
            TextDirection.RightToLeft,
            tagged,
            pinned);

    /// <summary>The same table with the cell texts in Devanagari (Hindi, Noto Sans Devanagari, Latin digits): "Row r cell c text" as "पंक्ति r कक्ष c पाठ".</summary>
    internal static PdfDocument TableDevanagari(FontCollection fonts, int rows = 1000, bool tagged = true, bool pinned = false) =>
        TableDocument(
            fonts,
            TableData.LocalizedHeader("स्तंभ"),
            TableData.LocalizedRows(rows, "पंक्ति", "कक्ष", "पाठ"),
            new TextStyle { Family = "Noto Sans Devanagari", Size = Length.Pt(9), Language = "hi" },
            null,
            tagged,
            pinned);

    /// <summary>
    /// The 20-page report of the plan: a heading, a row of four KPI boxes, three charts, two tables of 40 rows, prose, notes, a header and a footer, in Latin.
    /// About 20 A4 pages (the diagnostics say how many).
    /// </summary>
    internal static PdfDocument Report20(FontCollection fonts, bool tagged = true, bool pinned = false)
    {
        PdfDocument document = ReportShell(fonts, "Annual report 2026", tagged, pinned);
        document.Add(new Heading(1, "Annual report 2026"));
        document.Add(Kpis());
        document.Add(new Paragraph(Prose(0)));
        for (int section = 1; section <= 6; section++)
        {
            document.Add(new Heading(2, $"Section {section}"));
            for (int p = 0; p < Report20ParagraphsPerSection; p++)
            {
                document.Add(new Paragraph(Prose((section * 10) + p)));
            }

            if (section is 1 or 3 or 5)
            {
                document.Add(new Heading(3, $"Chart {(section + 1) / 2}"));
                document.Add(section == 3 ? LineChart(Series(section, 12), Months, "Line chart of twelve monthly figures from January to December") : BarChart(Series(section, 8), Months, "Bar chart of eight monthly figures from January to August"));
            }

            if (section is 2 or 4)
            {
                document.Add(new Heading(3, $"Table {section / 2}"));
                document.Add(Invoices(40));
            }
        }

        document.Add(new Heading(2, "Notes"));
        for (int n = 1; n <= Report20Notes; n++)
        {
            document.Add(new Paragraph($"Note {n}. " + Prose(100 + n)) { Style = new TextStyle { Size = Length.Pt(8) } });
        }

        return document;
    }

    /// <summary>The stress document of the plan: about 100 A4 pages, 12 charts and 20 tables mixed with prose, with a header and a footer, in Latin.</summary>
    internal static PdfDocument Stress(FontCollection fonts, bool tagged = true, bool pinned = false)
    {
        PdfDocument document = ReportShell(fonts, "Stress document", tagged, pinned);
        document.Add(new Heading(1, "Stress document"));
        document.Add(Kpis());
        int charts = 0;
        for (int section = 1; section <= 20; section++)
        {
            document.Add(new Heading(2, $"Section {section}"));
            for (int p = 0; p < StressParagraphsPerSection; p++)
            {
                document.Add(new Paragraph(Prose((section * 10) + p)));
            }

            if (charts < 12 && section % 5 != 0)
            {
                charts++;
                document.Add(new Heading(3, $"Chart {charts}"));
                document.Add(charts % 2 == 0 ? LineChart(Series(section, 12), Months, "Line chart of twelve monthly figures from January to December") : BarChart(Series(section, 8), Months, "Bar chart of eight monthly figures from January to August"));
            }

            document.Add(new Heading(3, $"Table {section}"));
            document.Add(Invoices(StressRowsPerTable));
            document.Add(new Paragraph($"Note {section}. " + Prose(200 + section)) { Style = new TextStyle { Size = Length.Pt(8) } });
        }

        return document;
    }

    private static PdfDocument ReportShell(FontCollection fonts, string title, bool tagged, bool pinned) => new(new PdfDocumentOptions
    {
        Fonts = fonts,
        Tagged = tagged,
        Compress = !pinned,
        Deterministic = pinned,
        DefaultStyle = new TextStyle { Family = "Noto Sans", Size = Length.Pt(10), Language = "en" },
        PageSize = PageSize.A4,
        Margins = Margins.Mm(top: 25, right: 15, bottom: 20, left: 15),
        Title = title,
        Language = "en",
        Header = page => new Paragraph(title) { Style = new TextStyle { Size = Length.Pt(8), Color = Color.MidGray } },
        Footer = page => new Paragraph($"Page {page.Number} of {page.Count}") { Alignment = TextAlignment.End, Style = new TextStyle { Size = Length.Pt(8), Color = Color.MidGray } },
    });

    /// <summary>A row of four boxes, each with a figure and a label.</summary>
    private static Columns Kpis() => new(
        new (string Value, string Label)[] { ("1.24 M", "Revenue"), ("12.4 %", "Growth"), ("3,842", "Orders"), ("97.1 %", "Satisfaction") }
            .Select(k => (Block)new Callout(
            [
                new Paragraph(k.Value) { Style = new TextStyle { Size = Length.Pt(18), Weight = 700 } },
                new Paragraph(k.Label) { Style = new TextStyle { Size = Length.Pt(9), Color = Color.MidGray } },
            ])
            { Fill = Color.Gray(0.95), CornerRadius = Length.Pt(4) }));

    /// <summary>Values from 1 to 10, the same for the same seed.</summary>
    private static double[] Series(int seed, int count)
    {
        double[] values = new double[count];
        for (int i = 0; i < count; i++)
        {
            values[i] = (((seed * 31) + (i * 17)) * 7 % 10) + 1;
        }

        return values;
    }

    private static PdfDocument TableDocument(FontCollection fonts, IReadOnlyList<string> header, IEnumerable<string[]> rows, TextStyle tableStyle, TextDirection? direction, bool tagged, bool pinned)
    {
        PdfDocument document = new(new PdfDocumentOptions
        {
            Fonts = fonts,
            Tagged = tagged,
            Compress = !pinned,
            Deterministic = pinned,
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
            new TableRow(header.ToArray()),
            rows.Select(cells => new TableRow(cells)))
        {
            Borders = Borders.Horizontal,
            CellPadding = Margins.Pt(1, 3, 1, 3),
            Style = tableStyle,
            Direction = direction,
        });
        return document;
    }

    /// <summary>A report of about ten pages: three sections, a long table that spans pages, a small table, a bar chart and a line chart, and an Arabic section with Latin words in it.</summary>
    internal static PdfDocument Report(FontCollection fonts, int invoiceRows = 200, bool tagged = true, bool pinned = false)
    {
        PdfDocument document = new(new PdfDocumentOptions
        {
            Fonts = fonts,
            Tagged = tagged,
            Compress = !pinned,
            Deterministic = pinned,
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
                document.Add(new Heading(3, "Sales by month"));
                document.Add(BarChart([4, 7, 5, 9, 6, 8, 3, 7], ["Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct"]));
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
            document.Add(new Paragraph("ارتفعت المبيعات بنسبة 12% خلال الربع، وكان أداء المنطقة الشمالية الأفضل. تظهر الرموز اللاتينية مثل Q3 وINV-2026-0042 في مكانها الصحيح داخل السطر.")
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

    private static Drawing BarChart(double[] values, string[] labels, string altText = "Bar chart of sales by month")
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
        { AltText = altText };
    }

    private static Drawing LineChart(double[] values, string[] labels, string altText = "Line chart of orders per month")
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
        { AltText = altText };
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
