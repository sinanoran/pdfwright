using System.Diagnostics;
using System.Globalization;
using Pdfwright.Tests;
using Pdfwright.Tests.Document;
using UglyToad.PdfPig.Content;

// This namespace is not under Pdfwright.Tests on purpose: there, the test folder named Drawing would hide the block called Drawing that the documentation
// uses, and a snippet would not read the way a reader's code does.
namespace Pdfwright.DocumentationTests;

/// <summary>A sequence of the caller's own records, as the snippets of the documentation have it.</summary>
internal sealed record Invoice(string Name, decimal Amount, string Status);

/// <summary>
/// Runs every code snippet of README.md and docs/guide.md. The code between <c>// &lt;snippet name&gt;</c> and <c>// &lt;/snippet&gt;</c> is the code of the document
/// that says <c>&lt;!-- snippet: name --&gt;</c> above its code block, character for character (see <see cref="DocumentationSyncTests"/>, which fails when the two differ,
/// or when a code block has no snippet here), so what a reader copies is what was compiled and run. Where a snippet needs a value that only the test has (a folder, a stream), the line
/// is one of the pairs that <see cref="DocumentationSyncTests"/> lists with the line the document shows, and the <c>// using X;</c> lines at the top of a snippet are the using lines of the document. Each test that saves a file checks it with qpdf last, so that a machine without qpdf skips the check after the assertions ran.
/// </summary>
public class DocumentationSnippetTests
{
    private static readonly Invoice[] AllInvoices = [.. Enumerable.Range(0, 120).Select(i => new Invoice($"Customer {i}", (i * 37 % 1000) + 0.5m, i % 3 == 0 ? "Open" : "Paid"))];

    private byte[]? _pdf;

    private static byte[] LogoBytes => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Images", "Files", "gradient-24x16.jpg"));

    private static string FontDirectory => TestPaths.FontsDirectory;

    private static int CountOf(string needle, string text)
    {
        int count = 0;
        for (int at = text.IndexOf(needle, StringComparison.Ordinal); at >= 0; at = text.IndexOf(needle, at + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private string[] ReadPages(byte[] pdf)
    {
        _pdf = pdf;
        using UglyToad.PdfPig.PdfDocument read = UglyToad.PdfPig.PdfDocument.Open(pdf);
        return [.. read.GetPages().Select((Page p) => p.Text.Replace("͏", string.Empty, StringComparison.Ordinal))];
    }

    private async Task<string[]> SaveAndReadAsync(PdfDocument document, CancellationToken cancellationToken)
    {
        using MemoryStream stream = new();
        await document.SaveAsync(stream, cancellationToken);
        return ReadPages(stream.ToArray());
    }

    [Fact]
    public async Task The_quick_start_of_the_readme_writes_a_report()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Invoice[] invoices = AllInvoices;

        // <snippet readme-quickstart>
        // using Pdfwright;
        // using System.Globalization;
        string fontDirectory = FontDirectory;
        using Stream output = new MemoryStream();

        // Fonts are supplied by the caller, never looked up in the operating system. A collection is made once and shared.
        using FontCollection fonts = new FontCollection()
            .Add(await File.ReadAllBytesAsync(Path.Combine(fontDirectory, "NotoSans-Regular.ttf"), cancellationToken))
            .Add(await File.ReadAllBytesAsync(Path.Combine(fontDirectory, "NotoSans-Bold.ttf"), cancellationToken))
            .Add(await File.ReadAllBytesAsync(Path.Combine(fontDirectory, "NotoSansArabic-Regular.ttf"), cancellationToken));

        PdfDocument document = new(new PdfDocumentOptions
        {
            Fonts = fonts,
            DefaultStyle = new TextStyle { Family = "Noto Sans", Size = Length.Pt(10) },
            Title = "Monthly report",
            Language = "en",
            Footer = page => new Paragraph($"{page.Number} / {page.Count}") { Alignment = TextAlignment.End },
        });

        document.Add(new Heading(1, "Monthly report"));
        document.Add(new Paragraph(
        [
            new TextRun("Sales rose "),
            new TextRun("12%", new TextStyle { Weight = 700, Color = Color.Green }),
            new TextRun(" over the quarter."),
        ]));
        document.Add(new Paragraph("تقرير المبيعات الشهري"));

        // The rows are any sequence. They are enumerated once, when the document is saved, and never held whole.
        document.Add(new Table(
            [new TableColumn(ColumnWidth.Fraction(1)), new TableColumn(ColumnWidth.Fixed(Length.Mm(30))) { Alignment = TextAlignment.End }, new TableColumn()],
            new TableRow("Customer", "Amount", "Status"),
            invoices.Select(i => new TableRow(i.Name, i.Amount.ToString("N2", CultureInfo.InvariantCulture), i.Status)))
        {
            Borders = Borders.Horizontal,
            HeaderFill = Color.Gray(0.9),
        });

        await document.SaveAsync(output, cancellationToken);
        // </snippet>

        Assert.True(output.Length > 1000);
        Assert.Empty(document.Diagnostics.MissingGlyphs);
        Assert.True(document.Diagnostics.PageCount >= 3);
        string[] pages = ReadPages(((MemoryStream)output).ToArray());
        Assert.Contains("Monthly report", pages[0]);
        Assert.Contains("تقرير المبيعات الشهري", pages[0]);
        Assert.Contains($"{pages.Length} / {pages.Length}", pages[^1]);

        // Last, so that a machine without qpdf skips the test after its assertions have run.
        DocumentTestKit.AssertValid(_pdf!, "documentation");
    }

    [Fact]
    public void A_font_collection_is_made_from_bytes_and_refuses_what_it_cannot_embed()
    {
        string fontDirectory = FontDirectory;
        byte[] fontBytes = [1, 2, 3];

        // <snippet guide-fonts>
        // One collection for the whole application: it parses each font once and keeps a native copy of it.
        FontCollection fonts = new();
        fonts.Add(File.ReadAllBytes(Path.Combine(fontDirectory, "NotoSans-Regular.ttf")));
        fonts.Add(File.ReadAllBytes(Path.Combine(fontDirectory, "NotoSans-Bold.ttf")));
        fonts.Add(File.ReadAllBytes(Path.Combine(fontDirectory, "NotoSansArabic-Regular.ttf")));

        string families = string.Join(", ", fonts.Families); // Noto Sans, Noto Sans Arabic

        // fontBytes stands for the bytes of one more font file, here one that may be refused.
        try
        {
            fonts.Add(fontBytes);
        }
        catch (FontNotEmbeddableException e)
        {
            // The font's licence forbids embedding it: e.Family names it and e.EmbeddingPermission is its OS/2 fsType.
            Console.Error.WriteLine($"{e.Family} may not be embedded (fsType {e.EmbeddingPermission}).");
        }
        catch (ArgumentException)
        {
            // Not a TrueType font. OpenType fonts with CFF outlines (most .otf files) are refused as well.
        }

        fonts.Dispose(); // when the application is done with it, not after each document
        // </snippet>

        Assert.Equal("Noto Sans, Noto Sans Arabic", families);
        Assert.Throws<ObjectDisposedException>(() => fonts.Add(File.ReadAllBytes(Path.Combine(fontDirectory, "NotoSans-Italic.ttf"))));
    }

    [Fact]
    public async Task Styles_are_laid_over_each_other_from_the_document_to_the_run()
    {
        FontCollection fonts = DocumentTestKit.Fonts;
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        // <snippet guide-styles>
        PdfDocument document = new(new PdfDocumentOptions
        {
            Fonts = fonts,
            // The base of every style. What it leaves unset is the library's: the first font, 10 points, regular, upright, black.
            DefaultStyle = new TextStyle { Family = "Noto Sans", Size = Length.Pt(10), Language = "en" },
        });

        document.Add(new Paragraph(
        [
            new TextRun("Totals are "),
            new TextRun("final", new TextStyle { Weight = 700 }),   // bold, in the paragraph's size and colour
            new TextRun(", amounts in "),
            new TextRun("euros", new TextStyle { Italic = true, Color = Color.MidGray }),
            new TextRun("."),
        ])
        {
            // Under the runs' own styles, over the document's: this paragraph is 11 points.
            Style = new TextStyle { Size = Length.Pt(11) },
        });
        // </snippet>

        string[] pages = await SaveAndReadAsync(document, cancellationToken);
        Assert.Contains("Totals are", pages[0]);
        DocumentTestKit.AssertValid(_pdf!, "documentation");
    }

    [Fact]
    public async Task Headings_paragraphs_spacers_and_page_breaks_make_the_flow()
    {
        FontCollection fonts = DocumentTestKit.Fonts;
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        // <snippet guide-flow>
        PdfDocument document = new(new PdfDocumentOptions
        {
            Fonts = fonts,
            DefaultStyle = new TextStyle { Family = "Noto Sans", Size = Length.Pt(10) },
            // Six styles for levels 1 to 6; what one leaves unset comes from the default style. Without this, headings are bold at 2.0, 1.6, 1.3, 1.15, 1.0 and 1.0 times its size.
            HeadingStyles =
            [
                new TextStyle { Size = Length.Pt(24), Weight = 700, Color = Color.FromHex("#1F3A5F") },
                new TextStyle { Size = Length.Pt(16), Weight = 700, Color = Color.FromHex("#1F3A5F") },
                new TextStyle { Size = Length.Pt(13), Weight = 700 },
                new TextStyle { Size = Length.Pt(11), Weight = 700 },
                new TextStyle { Size = Length.Pt(10), Weight = 700 },
                new TextStyle { Size = Length.Pt(10), Weight = 700, Italic = true },
            ],
        });

        document.Add(new Heading(1, "Quarterly review"));
        document.Add(new Heading(2, "Summary") { SpaceBefore = Length.Pt(18) });
        document.Add(new Paragraph("Revenue grew in every region. The paragraph breaks into lines at the width of the page.")
        {
            Alignment = TextAlignment.Start,   // Start, Center or End: logical, so Start is the right edge in a right-to-left paragraph
            LineSpacing = 1.2,                 // a multiplier of the natural line height
            SpaceAfter = Length.Pt(8),         // the default is 0.6 of the font size
        });
        document.Add(new Spacer(Length.Mm(5)));
        document.Add(new Paragraph("A short block that is moved whole to the next page rather than split.") { KeepTogether = true });
        document.Add(new PageBreak());
        document.Add(new Heading(2, "Details"));
        // </snippet>

        string[] pages = await SaveAndReadAsync(document, cancellationToken);
        Assert.Equal(2, pages.Length);
        Assert.Contains("Details", pages[1]);
        DocumentTestKit.AssertValid(_pdf!, "documentation");
    }

    [Fact]
    public async Task A_table_has_columns_of_three_kinds_cells_with_their_own_style_and_borders()
    {
        FontCollection fonts = DocumentTestKit.Fonts;
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Invoice[] invoices = AllInvoices;
        PdfDocument document = new(new PdfDocumentOptions { Fonts = fonts });

        // <snippet guide-table>
        // using System.Globalization;
        TableColumn[] columns =
        [
            new(ColumnWidth.Fraction(2)),                                              // shares what the others leave, in proportion to its weight
            new(ColumnWidth.Fixed(Length.Mm(25))) { Alignment = TextAlignment.End },   // a given width; numbers sit at the end edge
            new(),                                                                     // auto: measured from the header and the first 100 rows
        ];

        TableRow header = new("Customer", "Amount", "Status");
        IEnumerable<TableRow> rows = invoices.Select(i => new TableRow(i.Name, i.Amount.ToString("N2", CultureInfo.InvariantCulture), i.Status));

        document.Add(new Table(columns, header, rows)
        {
            Borders = Borders.Horizontal,
            HeaderFill = Color.Gray(0.9),
            ZebraFill = Color.Gray(0.97),
            CellPadding = Margins.Pt(top: 3, right: 5, bottom: 3, left: 5),
            HeaderStyle = new TextStyle { Weight = 700 },
            Style = new TextStyle { Size = Length.Pt(9) },
        });

        // A row can be made of cells with a style, a fill, an alignment or several paragraphs of their own.
        document.Add(new Table(
            columns,
            [
                new TableRow(
                    new TableCell("Total") { Style = new TextStyle { Weight = 700 } },
                    new TableCell("1,234.50") { Alignment = TextAlignment.End, Fill = Color.FromHex("#FFF3CD") },
                    new TableCell([new Paragraph("Paid"), new Paragraph("by card") { Style = new TextStyle { Color = Color.MidGray } }])),
            ]));
        // </snippet>

        string[] pages = await SaveAndReadAsync(document, cancellationToken);
        Assert.True(pages.Length >= 2);
        Assert.All(pages.Skip(1), p => Assert.Contains("Customer", p));
        DocumentTestKit.AssertValid(_pdf!, "documentation");
    }

    [Fact]
    public async Task A_table_of_many_rows_streams_and_can_be_cut_at_a_limit()
    {
        FontCollection fonts = DocumentTestKit.Fonts;
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Invoice[] invoices = AllInvoices;
        using MemoryStream stream = new();
        TableColumn[] columns = [new TableColumn(), new TableColumn(), new TableColumn()];
        TableRow header = new("Customer", "Amount", "Status");

        // <snippet guide-table-streaming>
        // using System.Diagnostics;
        // using System.Globalization;
        PdfDocument document = new(new PdfDocumentOptions { Fonts = fonts });

        // A lazy sequence is the intended input: it is enumerated once for each save and never held whole, so a table of a hundred
        // thousand rows costs the pages it makes and not the rows it came from.
        IEnumerable<TableRow> rows = invoices.Select(i => new TableRow(i.Name, i.Amount.ToString("N2", CultureInfo.InvariantCulture), i.Status));

        // MaxRows draws the first 100 rows and leaves the rest out; the document says so in its diagnostics.
        document.Add(new Table(columns, header, rows) { MaxRows = 100 });

        await document.SaveAsync(stream, cancellationToken);

        foreach (TruncatedTable cut in document.Diagnostics.TruncatedTables)
        {
            Trace.WriteLine($"The table at block {cut.BlockIndex} was cut after {cut.MaxRows} rows.");
        }
        // </snippet>

        TruncatedTable truncated = Assert.Single(document.Diagnostics.TruncatedTables);
        Assert.Equal(100, truncated.MaxRows);
        Assert.Equal(0, truncated.BlockIndex);
        _ = ReadPages(stream.ToArray());
        DocumentTestKit.AssertValid(_pdf!, "documentation");
    }

    [Fact]
    public async Task Columns_sit_in_a_callout_and_a_callout_holds_blocks()
    {
        FontCollection fonts = DocumentTestKit.Fonts;
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        PdfDocument document = new(new PdfDocumentOptions { Fonts = fonts });

        // <snippet guide-columns-callout>
        // A KPI row: one to four equal cells, here inside a filled box that is kept on one page.
        document.Add(new Callout(new Columns(
        [
            new Paragraph([new TextRun("Revenue\n"), new TextRun("1.2 M", new TextStyle { Size = Length.Pt(16), Weight = 700 })]),
            new Paragraph([new TextRun("Orders\n"), new TextRun("8,430", new TextStyle { Size = Length.Pt(16), Weight = 700 })]),
            new Paragraph([new TextRun("Returns\n"), new TextRun("2.1%", new TextStyle { Size = Length.Pt(16), Weight = 700, Color = Color.Red })]),
        ]))
        {
            Fill = Color.FromHex("#E8F0FE"),
            Border = new BorderLine(Length.Pt(0.75), Color.FromHex("#3366CC")),
            CornerRadius = Length.Pt(4),
            Padding = Margins.Pt(top: 8, right: 10, bottom: 8, left: 10),
            KeepTogether = true,
        });

        // A cell may hold several blocks, and a gap can be set; a callout in the flow holds any blocks and is split between pages when it is too tall.
        document.Add(new Columns([[new Heading(3, "Notes"), new Paragraph("Left cell")], [new Paragraph("Right cell")]]) { Gap = Length.Mm(8) });
        document.Add(new Callout([new Heading(3, "Note"), new Paragraph("A note is a few blocks in a box.")]) { Fill = Color.FromHex("#FFF3CD") });
        // </snippet>

        string[] pages = await SaveAndReadAsync(document, cancellationToken);
        Assert.Contains("Revenue", pages[0]);
        Assert.Contains("A note is a few blocks in a box.", pages[0]);
        DocumentTestKit.AssertValid(_pdf!, "documentation");
    }

    [Fact]
    public async Task Lists_have_markers_a_hanging_indent_and_a_nested_list()
    {
        FontCollection fonts = DocumentTestKit.Fonts;
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        PdfDocument document = new(new PdfDocumentOptions { Fonts = fonts, DefaultStyle = new TextStyle { Family = "Noto Sans", Size = Length.Pt(10) } });

        // <snippet guide-lists>
        // A bulleted list of texts, and a numbered one that starts at 3.
        document.Add(new ItemList(["Revenue grew in every region.", "Returns fell to 2.1%."]));
        document.Add(new ItemList(["Check the totals", "Send the report", "File the copy"]) { Marker = ListMarker.Decimal, Start = 3 });

        // An item may hold more paragraphs and a list of its own, one level deep.
        document.Add(new ItemList(
        [
            new ListItem([new Paragraph("Regions"), new ItemList(["North", "South"]) { Marker = ListMarker.LowerAlpha }]),
            new ListItem("Products"),
        ])
        {
            Indent = Length.Pt(14),                 // from the start edge to the marker
            MarkerGap = Length.Pt(6),               // from the widest marker to the text
            ItemSpacing = Length.Pt(3),             // between two items
            MarkerStyle = new TextStyle { Weight = 700 },
        });

        // In a right-to-left list the marker is at the right edge and the text hangs to its left.
        document.Add(new ItemList(["البند الأول", "البند الثاني"]) { Direction = TextDirection.RightToLeft, Marker = ListMarker.ArabicIndicDecimal });
        // </snippet>

        string[] pages = await SaveAndReadAsync(document, cancellationToken);
        Assert.Contains("Revenue grew in every region.", pages[0]);
        Assert.Contains("5.", pages[0]);
        Assert.Contains("a.", pages[0]);
        Assert.Contains("البند الثاني", pages[0]);
        Assert.Empty(document.Diagnostics.MissingGlyphs);
        DocumentTestKit.AssertValid(_pdf!, "documentation");
    }

    [Fact]
    public async Task A_drawing_draws_a_bar_chart_with_labels_placed_by_the_measurer()
    {
        FontCollection fonts = DocumentTestKit.Fonts;
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        PdfDocument document = new(new PdfDocumentOptions { Fonts = fonts, DefaultStyle = new TextStyle { Family = "Noto Sans" } });

        // <snippet guide-drawing>
        // using System.Globalization;
        double[] values = [4, 7, 5, 9, 6];
        string[] labels = ["Jun", "Jul", "Aug", "Sep", "Oct"];
        TextStyle small = new() { Size = Length.Pt(8) };
        TextMeasurer measurer = new(fonts, new TextStyle { Family = "Noto Sans" });

        document.Add(new Drawing(Length.Mm(120), Length.Mm(50), canvas =>
        {
            // Points, origin at the top left, y pointing down. Nothing is drawn outside the box.
            double baseline = canvas.Height - 14;
            double step = canvas.Width / values.Length;

            canvas.Stroke(new DrawingPathBuilder().MoveTo(0, baseline).LineTo(canvas.Width, baseline).Build(), Color.MidGray, 0.5);

            for (int i = 0; i < values.Length; i++)
            {
                double height = values[i] / 10 * (baseline - 14);
                double x = (i * step) + 6;
                canvas.Fill(new DrawingPathBuilder().Rectangle(x, baseline - height, step - 12, height).Build(), Color.FromHex("#3366CC"));

                // Place the value above the bar, centred, using the width the engine will draw it with.
                string value = values[i].ToString(CultureInfo.InvariantCulture);
                double width = measurer.Measure(value, small).Width.Points;
                if (width < step - 12)
                {
                    canvas.DrawText(value, x + ((step - 12) / 2), baseline - height - 3, small, TextAlignment.Center);
                }

                canvas.DrawText(labels[i], x + ((step - 12) / 2), canvas.Height - 3, small, TextAlignment.Center);
            }
        })
        { AltText = "Bar chart of sales by month" });
        // </snippet>

        string[] pages = await SaveAndReadAsync(document, cancellationToken);
        Assert.Contains("Oct", pages[0]);
        DocumentTestKit.AssertValid(_pdf!, "documentation");
    }

    [Fact]
    public async Task A_drawing_has_paths_clips_transforms_dashes_and_opacity()
    {
        FontCollection fonts = DocumentTestKit.Fonts;
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        PdfDocument document = new(new PdfDocumentOptions { Fonts = fonts });

        // <snippet guide-canvas>
        document.Add(new Drawing(Length.Mm(60), Length.Mm(30), canvas =>
        {
            DrawingPath disc = new DrawingPathBuilder().Ellipse(40, 40, 30, 30).Build();
            canvas.Fill(disc, Color.FromHex("#3366CC"), opacity: 0.5);

            canvas.Save();                                   // the transform and the clip are undone by Restore
            canvas.Clip(new DrawingPathBuilder().Rectangle(0, 0, canvas.Width, canvas.Height).Build());
            canvas.Translate(120, 40);
            canvas.Rotate(20);                               // degrees, clockwise on the canvas
            DrawingPen dashed = new(Color.Red, width: 2, dashes: [5, 3]);
            canvas.Stroke(new DrawingPathBuilder().RoundedRectangle(-20, -10, 40, 20, 4, 4).Build(), dashed);
            canvas.Restore();

            // A pie slice: a path of an arc and two lines.
            DrawingPath slice = new DrawingPathBuilder().MoveTo(40, 40).LineTo(70, 40).Arc(40, 40, 30, 30, 0, 90).Close().Build();
            canvas.Fill(slice, Color.Green, rule: FillRule.NonZero);
        }));
        // </snippet>

        string[] pages = await SaveAndReadAsync(document, cancellationToken);
        Assert.Single(pages);
        DocumentTestKit.AssertValid(_pdf!, "documentation");
    }

    [Fact]
    public async Task A_picture_is_a_block_and_can_be_drawn_on_a_canvas()
    {
        FontCollection fonts = DocumentTestKit.Fonts;
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        byte[] logoBytes = LogoBytes;
        PdfDocument document = new(new PdfDocumentOptions { Fonts = fonts });

        // <snippet guide-images>
        PdfImage logo = PdfImage.FromBytes(logoBytes);   // JPEG, PNG or WebP; make it once and use it in any number of documents

        // The block keeps the picture's proportions: give a width, a height, or neither (the picture's natural size, 96 pixels to the inch).
        document.Add(new Image(logo) { Width = Length.Mm(30), Alignment = TextAlignment.End, AltText = "Company logo" });

        // The canvas stretches the picture to the rectangle it is given.
        document.Add(new Drawing(Length.Mm(40), Length.Mm(20), canvas => canvas.DrawImage(logo, 0, 0, canvas.Width, canvas.Height, opacity: 0.8)));
        // </snippet>

        Assert.Equal(24, logo.PixelWidth);
        string[] pages = await SaveAndReadAsync(document, cancellationToken);
        Assert.Single(pages);
        DocumentTestKit.AssertValid(_pdf!, "documentation");
    }

    [Fact]
    public void The_pixel_limit_of_a_picture_can_be_changed_with_an_option()
    {
        byte[] photoBytes = LogoBytes;

        // <snippet guide-images-limit>
        // The limit is on width times height, for every format (JPEG, PNG and WebP); the default is 40 million, and the most that can be set is 100 million.
        PdfImage photo = PdfImage.FromBytes(photoBytes, new PdfImageOptions { MaxPixels = 60_000_000 });
        // </snippet>

        Assert.Equal(24, photo.PixelWidth);
        ArgumentException refused = Assert.Throws<ArgumentException>(() => PdfImage.FromBytes(photoBytes, new PdfImageOptions { MaxPixels = 100 }));
        Assert.Contains("PdfImageOptions.MaxPixels", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_header_and_a_footer_are_built_for_each_page_from_its_context()
    {
        FontCollection fonts = DocumentTestKit.Fonts;
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        byte[] logoBytes = LogoBytes;

        // <snippet guide-header-footer>
        PdfImage logo = PdfImage.FromBytes(logoBytes);

        PdfDocument document = new(new PdfDocumentOptions
        {
            Fonts = fonts,
            Margins = Margins.Mm(top: 25, right: 15, bottom: 20, left: 15),
            HeaderOffset = Length.Mm(8),   // from the top edge of the page to the top of the header (by default, half the margin)
            FooterOffset = Length.Mm(8),   // from the bottom edge of the page to the bottom of the footer
            // Called once for each page, after the whole body is laid out, so Count is final. Return null for a page with no header.
            Header = page => page.IsFirst
                ? null
                : new Columns([new Image(logo) { Width = Length.Pt(24) }, new Paragraph("Monthly report") { Alignment = TextAlignment.End, Style = new TextStyle { Color = Color.MidGray } }]),
            Footer = page => new Paragraph($"Page {page.Number} of {page.Count}") { Alignment = TextAlignment.End },
        });

        document.Add(new Heading(1, "Monthly report"));
        for (int i = 0; i < 90; i++)
        {
            document.Add(new Paragraph($"Line {i} of the body, which runs over several pages."));
        }
        // </snippet>

        string[] pages = await SaveAndReadAsync(document, cancellationToken);
        Assert.True(pages.Length >= 2);
        Assert.Contains($"Page 1 of {pages.Length}", pages[0]);
        Assert.Equal(1, CountOf("Monthly report", pages[0])); // the heading only: no header on the first page
        Assert.Equal(1, CountOf("Monthly report", pages[1])); // the header only
        DocumentTestKit.AssertValid(_pdf!, "documentation");
    }

    [Fact]
    public async Task A_page_number_in_a_right_to_left_footer_is_shown_back_to_front_unless_the_footer_says_otherwise()
    {
        FontCollection fonts = DocumentTestKit.Fonts;
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        // The footer a reader gets without doing anything: a right-to-left paragraph, so page 1 of 3 is shown as "3 / 1".
        PdfDocument plain = new(new PdfDocumentOptions
        {
            Fonts = fonts,
            DefaultStyle = new TextStyle { Family = "Noto Sans Arabic", Language = "ar" },
            Direction = TextDirection.RightToLeft,
            Footer = page => new Paragraph($"{page.Number} / {page.Count}"),
        });
        Assert.Equal("31", await FooterDigitsOfFirstPageAsync(plain, cancellationToken));

        // <snippet guide-rtl-footer>
        PdfDocument document = new(new PdfDocumentOptions
        {
            Fonts = fonts,
            DefaultStyle = new TextStyle { Family = "Noto Sans Arabic", Language = "ar" },
            Direction = TextDirection.RightToLeft,
            // The footer is a right-to-left paragraph here, so "1 / 3" would be read in bidi order and shown as "3 / 1".
            // Make the whole footer left to right:
            Footer = page => new Paragraph($"{page.Number} / {page.Count}") { Direction = TextDirection.LeftToRight, Alignment = TextAlignment.Center },
        });
        // </snippet>

        Assert.Equal("13", await FooterDigitsOfFirstPageAsync(document, cancellationToken));
        AssertFooterFileValid();
    }

    [Fact]
    public async Task A_left_to_right_isolate_keeps_the_page_numbers_in_order_inside_a_right_to_left_footer()
    {
        FontCollection fonts = DocumentTestKit.Fonts;
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        // <snippet guide-rtl-footer-isolate>
        PdfDocument document = new(new PdfDocumentOptions
        {
            Fonts = fonts,
            DefaultStyle = new TextStyle { Family = "Noto Sans Arabic", Language = "ar" },
            Direction = TextDirection.RightToLeft,
            // "Page" and the numbers: the numbers sit inside a left-to-right isolate (U+2066 ... U+2069), so "1 / 3" keeps its order, and the footer stays a right-to-left line.
            Footer = page => new Paragraph($"صفحة ⁦{page.Number} / {page.Count}⁩"),
        });
        // </snippet>

        Assert.Equal("13", await FooterDigitsOfFirstPageAsync(document, cancellationToken));
        AssertFooterFileValid();
    }

    [Fact]
    public async Task A_word_between_the_page_numbers_keeps_them_in_the_order_an_arabic_reader_expects()
    {
        FontCollection fonts = DocumentTestKit.Fonts;
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        // <snippet guide-rtl-footer-of>
        PdfDocument document = new(new PdfDocumentOptions
        {
            Fonts = fonts,
            DefaultStyle = new TextStyle { Family = "Noto Sans Arabic", Language = "ar" },
            Direction = TextDirection.RightToLeft,
            // "Page 1 of 3" in Arabic: the word "من" (of) separates the numbers, so each keeps its own place and the line reads from right to left.
            Footer = page => new Paragraph($"صفحة {page.Number} من {page.Count}"),
        });
        // </snippet>

        // From left to right the page count stands first and the page number last: read from the right, as Arabic is, that is "page 1 of 3".
        Assert.Equal("31", await FooterDigitsOfFirstPageAsync(document, cancellationToken));
        AssertFooterFileValid();
    }

    /// <summary>Saves the document with three pages, and returns the digits of the footer of page 1 in the order they stand from left to right.</summary>
    private async Task<string> FooterDigitsOfFirstPageAsync(PdfDocument document, CancellationToken cancellationToken)
    {
        document.Add(new Paragraph("مرحبا"));
        document.Add(new PageBreak());
        document.Add(new Paragraph("مرحبا"));
        document.Add(new PageBreak());
        document.Add(new Paragraph("مرحبا"));

        using MemoryStream stream = new();
        await document.SaveAsync(stream, cancellationToken);
        _pdf = stream.ToArray();

        // The digit order is read before the file is checked with qpdf, which skips the test where qpdf is not installed.
        using UglyToad.PdfPig.PdfDocument read = UglyToad.PdfPig.PdfDocument.Open(_pdf);
        Assert.Equal(3, read.NumberOfPages);
        Page first = read.GetPage(1);
        return string.Concat(first.Letters.Where(l => l.Value.Length == 1 && char.IsAsciiDigit(l.Value[0])).OrderBy(l => l.BoundingBox.Left).Select(l => l.Value));
    }

    private void AssertFooterFileValid() => DocumentTestKit.AssertValid(_pdf!, "documentation");

    [Fact]
    public async Task Metadata_and_a_fixed_date_make_the_same_bytes_every_time()
    {
        FontCollection fonts = DocumentTestKit.Fonts;
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        // <snippet guide-metadata>
        PdfDocumentOptions options = new()
        {
            Fonts = fonts,
            Title = "Monthly report",
            Subject = "Sales, October 2026",
            Author = "Finance",                 // written only when set; nothing is added that the caller did not give
            Creator = "Reporting service",
            Language = "en",                    // a screen reader chooses its voice from this
            Deterministic = true,               // the clock is never read: the same document is the same bytes
            Creation = new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.Zero),   // optional; a deterministic file has no date without it
            Compress = false,                   // Flate depends on the runtime's zlib; off, the bytes are the same on every .NET version
        };

        PdfDocument document = new(options);
        document.Add(new Paragraph("Hello"));
        // </snippet>

        using MemoryStream first = new();
        using MemoryStream second = new();
        await document.SaveAsync(first, cancellationToken);
        await document.SaveAsync(second, cancellationToken);
        Assert.Equal(first.ToArray(), second.ToArray());
        using UglyToad.PdfPig.PdfDocument read = UglyToad.PdfPig.PdfDocument.Open(first.ToArray());
        Assert.Equal("Monthly report", read.Information.Title);
        Assert.Equal("Finance", read.Information.Author);
        DocumentTestKit.AssertValid(first.ToArray(), "documentation");
    }

    [Fact]
    public async Task Limits_stop_a_runaway_document_and_the_diagnostics_report_what_could_not_be_drawn()
    {
        FontCollection fonts = DocumentTestKit.Fonts;
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using MemoryStream stream = new();

        // <snippet guide-diagnostics-limits>
        // using System.Diagnostics;
        PdfDocument document = new(new PdfDocumentOptions
        {
            Fonts = fonts,
            Limits = new DocumentLimits { MaxPages = 50, MaxBytes = 5_000_000, MaxTableRows = 5_000 },
        });

        document.Add(new Paragraph("Hello 你好"));   // no font of the collection has these two characters

        try
        {
            await document.SaveAsync(stream, cancellationToken);
        }
        catch (PdfLimitExceededException e)
        {
            // Nothing was written to the stream. e.Limit says which limit and e.Maximum its value.
            Trace.WriteLine($"{e.Limit} of {e.Maximum} was exceeded.");
            throw;
        }

        // What the save found: data to inspect, never an exception.
        DocumentDiagnostics report = document.Diagnostics;
        foreach (MissingGlyph glyph in report.MissingGlyphs)
        {
            Trace.WriteLine($"{glyph.Character} (U+{glyph.CodePoint:X4}) is in no font; language {glyph.Language ?? "unset"}, page {glyph.Page}.");
        }

        Trace.WriteLine($"{report.PageCount} page(s), {report.ByteCount} bytes, missing families: {string.Join(", ", report.MissingFamilies)}");
        // </snippet>

        Assert.Equal(2, report.MissingGlyphs.Count);
        Assert.Equal(1, report.PageCount);
        Assert.Equal(stream.Length, report.ByteCount);
    }

    [Fact]
    public void A_limit_that_is_hit_writes_nothing()
    {
        FontCollection fonts = DocumentTestKit.Fonts;
        using MemoryStream stream = new();
        PdfDocument document = new(new PdfDocumentOptions { Fonts = fonts, Limits = new DocumentLimits { MaxPages = 1 } });
        document.Add(new Paragraph("one"));
        document.Add(new PageBreak());
        document.Add(new Paragraph("two"));

        PdfLimitExceededException e = Assert.Throws<PdfLimitExceededException>(() => document.Save(stream));

        Assert.Equal(DocumentLimit.MaxPages, e.Limit);
        Assert.Equal(0, stream.Length);
    }

    [Fact]
    public void The_measurer_answers_with_the_width_and_height_a_document_would_use()
    {
        FontCollection fonts = DocumentTestKit.Fonts;

        // <snippet guide-measuring>
        // Make it the way the document is made: the same fonts, default style, language and line spacing.
        TextMeasurer measurer = new(fonts, new TextStyle { Family = "Noto Sans", Size = Length.Pt(9) }, language: "en");

        TextMeasurement one = measurer.Measure("Revenue by quarter");
        double widthInPoints = one.Width.Points;

        // With a width the text wraps like a paragraph of that width: how many lines, and how tall.
        TextMeasurement wrapped = measurer.Measure("A caption that is longer than the room the figure leaves for it.", maxWidth: Length.Mm(40));
        int lines = wrapped.LineCount;
        Length height = wrapped.Height;
        // </snippet>

        Assert.True(widthInPoints > 50);
        Assert.True(lines >= 2);
        Assert.True(height > one.Height);
    }

    [Fact]
    public async Task A_right_to_left_document_reads_from_the_right_and_a_paragraph_can_say_otherwise()
    {
        FontCollection fonts = DocumentTestKit.Fonts;
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        // <snippet guide-rtl>
        PdfDocument document = new(new PdfDocumentOptions
        {
            Fonts = fonts,
            DefaultStyle = new TextStyle { Family = "Noto Sans Arabic", Size = Length.Pt(11), Language = "ar" },
            Language = "ar",
            Direction = TextDirection.RightToLeft,   // the default is Auto: each paragraph takes its direction from its first strong character
        });

        document.Add(new Heading(1, "تقرير المبيعات الشهري"));

        // English inside Arabic: the Unicode bidirectional algorithm orders the runs; the text stays in logical order in the file.
        document.Add(new Paragraph("بلغت المبيعات 1,200 وحدة في Q3 وفق تقرير Finance."));

        // A reference code reads left to right, as one line.
        document.Add(new Paragraph("INV-2026-0042") { Direction = TextDirection.LeftToRight });

        // The first column is at the right edge; Start alignment is the right edge, End the left.
        document.Add(new Table(
            [new TableColumn(ColumnWidth.Fraction(1)), new TableColumn(ColumnWidth.Fixed(Length.Mm(30))) { Alignment = TextAlignment.End }],
            new TableRow("العميل", "المبلغ"),
            [new TableRow("شركة الأفق", "1,200.00"), new TableRow("مؤسسة النور", "845.50")])
        {
            Borders = Borders.Horizontal,
        });
        // </snippet>

        string[] pages = await SaveAndReadAsync(document, cancellationToken);
        Assert.Contains("تقرير المبيعات الشهري", pages[0]);
        Assert.Contains("INV-2026-0042", pages[0]);
        Assert.Empty(document.Diagnostics.MissingGlyphs);
        DocumentTestKit.AssertValid(_pdf!, "documentation");
    }

    [Fact]
    public async Task A_document_is_saved_to_a_stream_and_the_bytes_are_returned()
    {
        FontCollection fonts = DocumentTestKit.Fonts;
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        PdfDocument document = new(new PdfDocumentOptions { Fonts = fonts });
        document.Add(new Paragraph("Hello"));

        // <snippet guide-save>
        // The whole file is made in memory and then copied, so a document that fails writes nothing. The stream is not closed.
        using MemoryStream stream = new();
        await document.SaveAsync(stream, cancellationToken);
        byte[] pdf = stream.ToArray();   // e.g. return Results.File(pdf, "application/pdf", "report.pdf") from an ASP.NET Core endpoint
        // </snippet>

        Assert.StartsWith("%PDF-", System.Text.Encoding.ASCII.GetString(pdf, 0, 5), StringComparison.Ordinal);
        DocumentTestKit.AssertValid(pdf, "documentation");
    }
}
