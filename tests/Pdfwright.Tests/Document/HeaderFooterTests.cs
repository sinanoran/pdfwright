using System.Text;
using UglyToad.PdfPig.Content;
using DrawingBlock = Pdfwright.Drawing;

namespace Pdfwright.Tests.Document;

/// <summary>Headers and footers: made per page after the body, with the page count known, in one pass.</summary>
public class HeaderFooterTests
{
    // A page of 300 by 200 points, a margin of 30 at the top and bottom and of 10 at the sides: a content area of 280 by 140.
    private static PdfDocumentOptions Small(Action<DocumentTestKit.OptionsBuilder>? change = null) =>
        DocumentTestKit.Options(b =>
        {
            b.PageSize = new PageSize(Length.Pt(300), Length.Pt(200));
            b.Margins = Margins.Pt(30, 10, 30, 10);
            change?.Invoke(b);
        });

    private static PdfDocument Document(PdfDocumentOptions options, params Block[] blocks)
    {
        PdfDocument document = new(options);
        foreach (Block block in blocks)
        {
            document.Add(block);
        }

        return document;
    }

    private static Block Body(int lines) => new Paragraph(string.Join('\n', Enumerable.Range(0, lines).Select(i => $"body{i}")));

    [Fact]
    public void Page_x_of_y_is_right_on_every_page_of_a_document_of_four_pages()
    {
        PdfDocument document = Document(Small(b => b.Footer = page => new Paragraph($"Page {page.Number} / {page.Count}")), Body(40));

        byte[] pdf = DocumentTestKit.Save(document);

        string[] pages = DocumentTestKit.PageTexts(pdf);
        Assert.True(pages.Length >= 4);
        for (int n = 1; n <= pages.Length; n++)
        {
            Assert.Contains($"Page {n} / {pages.Length}", pages[n - 1]);
        }

        Assert.Equal(pages.Length, document.Diagnostics.PageCount);
    }

    private sealed class CountingRows(int count) : IEnumerable<TableRow>
    {
        internal int Enumerations { get; private set; }

        internal int Rows { get; private set; }

        public IEnumerator<TableRow> GetEnumerator()
        {
            Enumerations++;
            for (int i = 0; i < count; i++)
            {
                Rows++;
                yield return new TableRow([$"r{i}", $"c{i}"]);
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Fact]
    public void A_footer_that_reads_the_page_count_does_not_enumerate_a_streamed_table_twice()
    {
        CountingRows rows = new(300);
        PdfDocument document = Document(
            Small(b => b.Footer = page => new Paragraph($"{page.Number} of {page.Count}")),
            new Table([new TableColumn(), new TableColumn()], rows));

        byte[] pdf = DocumentTestKit.Save(document);

        Assert.Equal(1, rows.Enumerations);
        Assert.Equal(300, rows.Rows);
        string[] pages = DocumentTestKit.PageTexts(pdf);
        Assert.True(pages.Length >= 3);
        for (int n = 1; n <= pages.Length; n++)
        {
            Assert.Contains($"{n} of {pages.Length}", pages[n - 1]);
        }
    }

    [Fact]
    public void The_context_says_which_page_is_first_and_last()
    {
        List<(int Number, int Count, bool First, bool Last)> seen = [];
        PdfDocument document = Document(Small(b => b.Header = page =>
        {
            seen.Add((page.Number, page.Count, page.IsFirst, page.IsLast));
            return null;
        }), Body(30));

        _ = DocumentTestKit.Save(document);

        int count = document.Diagnostics.PageCount;
        Assert.True(count >= 3);
        Assert.Equal(count, seen.Count);
        Assert.All(seen, s => Assert.Equal(count, s.Count));
        Assert.Equal(Enumerable.Range(1, count), seen.Select(s => s.Number));
        Assert.Equal([true], seen.Where(s => s.First).Select(s => s.Number == 1));
        Assert.Equal(count, seen.Single(s => s.Last).Number);
    }

    [Fact]
    public void A_header_for_the_first_page_only_leaves_the_other_pages_without_one()
    {
        PdfDocument document = Document(Small(b => b.Header = page => page.IsFirst ? new Paragraph("TITLE PAGE") : null), Body(30));

        string[] pages = DocumentTestKit.PageTexts(DocumentTestKit.Save(document));

        Assert.Contains("TITLE PAGE", pages[0]);
        Assert.All(pages.Skip(1), p => Assert.DoesNotContain("TITLE", p));
    }

    [Fact]
    public void The_header_sits_in_the_top_margin_and_the_footer_in_the_bottom_margin_and_the_body_is_unchanged()
    {
        PdfDocument withBoth = Document(Small(b =>
        {
            b.Header = _ => new Paragraph("HEAD");
            b.Footer = _ => new Paragraph("FOOT");
        }), Body(5));
        PdfDocument without = Document(Small(), Body(5));

        using UglyToad.PdfPig.PdfDocument read = UglyToad.PdfPig.PdfDocument.Open(DocumentTestKit.Save(withBoth));
        using UglyToad.PdfPig.PdfDocument plain = UglyToad.PdfPig.PdfDocument.Open(DocumentTestKit.Save(without));
        Page page = read.GetPage(1);
        Word head = page.GetWords().First(w => w.Text == "HEAD");
        Word foot = page.GetWords().First(w => w.Text == "FOOT");
        Word body = page.GetWords().First(w => w.Text == "body0");

        // Offsets default to half the margin: the header's top is 15 points below the top edge, the footer's bottom 15 above the bottom edge.
        Assert.True(head.BoundingBox.Top <= 200 - 15 + 0.5 && head.BoundingBox.Bottom >= 200 - 30 - 0.01);
        Assert.True(foot.BoundingBox.Bottom >= 15 - 0.5 && foot.BoundingBox.Top <= 30 + 0.01);
        Assert.True(body.BoundingBox.Top <= 200 - 30 + 0.5);
        Assert.Equal(plain.GetPage(1).GetWords().First(w => w.Text == "body0").BoundingBox.Top, body.BoundingBox.Top, 3);
    }

    [Fact]
    public void The_offsets_move_the_header_and_the_footer()
    {
        PdfDocument document = Document(Small(b =>
        {
            b.Header = _ => new Paragraph("HEAD");
            b.Footer = _ => new Paragraph("FOOT");
            b.HeaderOffset = Length.Pt(5);
            b.FooterOffset = Length.Pt(4);
        }), Body(2));

        using UglyToad.PdfPig.PdfDocument read = UglyToad.PdfPig.PdfDocument.Open(DocumentTestKit.Save(document));
        Word head = read.GetPage(1).GetWords().First(w => w.Text == "HEAD");
        Word foot = read.GetPage(1).GetWords().First(w => w.Text == "FOOT");
        Assert.True(head.BoundingBox.Top > 200 - 15);
        Assert.True(head.BoundingBox.Top <= 200 - 5 + 0.5);
        Assert.True(foot.BoundingBox.Bottom < 15);
        Assert.True(foot.BoundingBox.Bottom >= 4 - 0.5);
    }

    [Fact]
    public void A_header_taller_than_the_margin_fails_the_save_naming_the_page_and_writes_nothing()
    {
        PdfDocument document = Document(Small(b => b.Header = page => page.Number == 2 ? new Paragraph("a\nb\nc\nd\ne") : new Paragraph("ok")), Body(30));
        using MemoryStream stream = new();

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => document.Save(stream));

        Assert.Contains("header of page 2", error.Message);
        Assert.Contains("top margin", error.Message);
        Assert.Equal(0, stream.Length);
    }

    [Fact]
    public void A_footer_taller_than_the_margin_fails_the_save()
    {
        PdfDocument document = Document(Small(b => b.Footer = _ => new Paragraph("a\nb\nc\nd\ne")), Body(3));

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => DocumentTestKit.Save(document));

        Assert.Contains("footer of page 1", error.Message);
        Assert.Contains("bottom margin", error.Message);
    }

    [Fact]
    public void An_offset_larger_than_the_margin_is_refused_when_the_document_is_made()
    {
        Assert.Throws<ArgumentException>(() => new PdfDocument(Small(b => b.HeaderOffset = Length.Pt(31))));
        Assert.Throws<ArgumentException>(() => new PdfDocument(Small(b => b.FooterOffset = Length.Pt(31))));
        Assert.Equal("HeaderOffset", Assert.Throws<ArgumentOutOfRangeException>(() => new PdfDocumentOptions { Fonts = DocumentTestKit.Fonts, HeaderOffset = Length.Pt(-1) }).ParamName);
        Assert.Equal("FooterOffset", Assert.Throws<ArgumentOutOfRangeException>(() => new PdfDocumentOptions { Fonts = DocumentTestKit.Fonts, FooterOffset = Length.Pt(-3) }).ParamName);
    }

    [Fact]
    public void A_table_or_a_page_break_cannot_be_a_header()
    {
        PdfDocument table = Document(Small(b => b.Header = _ => new Table([new TableColumn()], [new TableRow(["x"])])), Body(1));
        PdfDocument pageBreak = Document(Small(b => b.Footer = _ => new PageBreak()), Body(1));

        Assert.Contains("Table", Assert.Throws<InvalidOperationException>(() => DocumentTestKit.Save(table)).Message);
        Assert.Contains("PageBreak", Assert.Throws<InvalidOperationException>(() => DocumentTestKit.Save(pageBreak)).Message);
    }

    [Fact]
    public void A_logo_and_a_date_in_a_header_made_of_columns_are_drawn_on_every_page()
    {
        PdfImage logo = PdfImage.FromBytes(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Images", "Files", "gradient-24x16.jpg")));
        DateTime date = new(2026, 11, 21);
        PdfDocument document = Document(
            Small(b => b.Header = _ => new Columns([new Image(logo) { Width = Length.Pt(24) }, new Paragraph(date.ToString("dd MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture)) { Alignment = TextAlignment.End }]) { Gap = Length.Zero }),
            Body(30));

        byte[] pdf = DocumentTestKit.Save(document);

        using UglyToad.PdfPig.PdfDocument read = UglyToad.PdfPig.PdfDocument.Open(pdf);
        Assert.True(read.NumberOfPages >= 3);
        foreach (Page page in read.GetPages())
        {
            IPdfImage image = Assert.Single(page.GetImages());
            Assert.Equal(10, image.BoundingBox.Left, 2);
            Assert.True(image.BoundingBox.Top <= 185.01 && image.BoundingBox.Bottom >= 170);
            Assert.Contains("21", page.Text);
            Assert.Contains("November", page.Text);
            Word year = page.GetWords().First(w => w.Text == "2026");
            Assert.True(year.BoundingBox.Right <= 290.5 && year.BoundingBox.Right > 270);
        }

        Assert.Equal(1, CountOf(Encoding.Latin1.GetString(pdf), "/Subtype /Image"));
        DocumentTestKit.AssertValid(pdf, "header-logo");
    }

    private static int CountOf(string text, string part)
    {
        int count = 0;
        for (int at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    [Fact]
    public void A_drawing_in_a_footer_runs_once_for_each_page()
    {
        int calls = 0;
        PdfDocument document = Document(
            Small(b => b.Footer = page => new DrawingBlock(Length.Pt(40), Length.Pt(8), c =>
            {
                calls++;
                c.Fill(new DrawingPathBuilder().Rectangle(0, 0, 40 * page.Number / (double)page.Count, 8).Build(), Color.Blue);
            })),
            Body(30));

        _ = DocumentTestKit.Save(document);

        Assert.Equal(document.Diagnostics.PageCount, calls);
    }

    [Fact]
    public void A_missing_glyph_in_the_header_or_the_footer_is_reported_with_its_page()
    {
        string han = char.ConvertFromUtf32(0x4E00);
        PdfDocument document = Document(Small(b =>
        {
            b.Header = page => page.Number == 2 ? new Paragraph("h" + han) : null;
            b.Footer = page => new Paragraph("f" + char.ConvertFromUtf32(0x4E01));
        }), Body(30));

        _ = DocumentTestKit.Save(document);

        int count = document.Diagnostics.PageCount;
        Assert.Contains(document.Diagnostics.MissingGlyphs, m => m.CodePoint == 0x4E00 && m.Page == 2);
        Assert.Equal(Enumerable.Range(1, count), document.Diagnostics.MissingGlyphs.Where(m => m.CodePoint == 0x4E01).Select(m => m.Page));
        Assert.Single(document.Diagnostics.MissingGlyphs, m => m.CodePoint == 0x4E00);
    }

    [Fact]
    public void A_family_that_a_footer_asks_for_and_the_collection_lacks_is_reported()
    {
        PdfDocument document = Document(Small(b => b.Footer = _ => new Paragraph("x") { Style = new TextStyle { Family = "Absent" } }), Body(1));

        _ = DocumentTestKit.Save(document);

        Assert.Equal(["Absent"], document.Diagnostics.MissingFamilies);
    }

    [Fact]
    public void Extraction_gives_the_header_then_the_body_then_the_footer_on_every_page()
    {
        PdfDocument document = Document(Small(b =>
        {
            b.Header = page => new Paragraph($"HEADER{page.Number}");
            b.Footer = page => new Paragraph($"FOOTER{page.Number}");
        }), Body(30));

        string[] pages = DocumentTestKit.PageTexts(DocumentTestKit.Save(document));

        for (int n = 0; n < pages.Length; n++)
        {
            string text = pages[n];
            int header = text.IndexOf($"HEADER{n + 1}", StringComparison.Ordinal);
            int body = text.IndexOf("body", StringComparison.Ordinal);
            int footer = text.IndexOf($"FOOTER{n + 1}", StringComparison.Ordinal);
            Assert.True(header >= 0 && header < body && body < footer, text);
        }
    }

    [Fact]
    public void The_file_is_the_same_bytes_every_time_and_with_no_header_or_footer_it_is_as_it_was()
    {
        PdfDocumentOptions options = Small(b => b.Footer = page => new Paragraph($"{page.Number}/{page.Count}"));
        PdfDocument document = Document(options, Body(30));

        byte[] first = DocumentTestKit.Save(document);
        byte[] second = DocumentTestKit.Save(document);
        byte[] third = DocumentTestKit.Save(Document(options, Body(30)));

        Assert.Equal(first, second);
        Assert.Equal(first, third);
    }

    [Fact]
    public void Pages_with_a_header_and_a_footer_have_three_content_streams_and_the_file_is_valid()
    {
        PdfDocument document = Document(Small(b =>
        {
            b.Header = _ => new Paragraph("H");
            b.Footer = page => new Paragraph($"{page.Number}/{page.Count}");
        }), Body(30));
        byte[] pdf = DocumentTestKit.Save(document);

        using UglyToad.PdfPig.PdfDocument read = UglyToad.PdfPig.PdfDocument.Open(pdf);
        Assert.True(read.NumberOfPages >= 3);
        DocumentTestKit.AssertValid(pdf, "header-footer");
    }

    [Fact]
    public void A_compressed_document_with_a_footer_is_valid_and_reads_back()
    {
        PdfDocument document = Document(Small(b =>
        {
            b.Compress = true;
            b.Footer = page => new Paragraph($"{page.Number}/{page.Count}");
        }), Body(30));
        byte[] pdf = DocumentTestKit.Save(document);

        string[] pages = DocumentTestKit.PageTexts(pdf);
        Assert.Contains($"{pages.Length}/{pages.Length}", pages[^1]);
        DocumentTestKit.AssertValid(pdf, "header-footer-compressed");
    }

    [Fact]
    public void An_empty_document_still_gets_its_header_and_footer_on_its_one_page()
    {
        PdfDocument document = Document(Small(b =>
        {
            b.Header = page => new Paragraph("H");
            b.Footer = page => new Paragraph($"{page.Number}/{page.Count}");
        }));

        string[] pages = DocumentTestKit.PageTexts(DocumentTestKit.Save(document));

        Assert.Single(pages);
        Assert.Contains("H", pages[0]);
        Assert.Contains("1/1", pages[0]);
    }

    [Fact]
    public void The_page_limit_still_stops_a_document_with_a_footer()
    {
        PdfDocument document = Document(Small(b =>
        {
            b.Footer = _ => new Paragraph("f");
            b.Limits = new DocumentLimits { MaxPages = 2 };
        }), Body(60));

        PdfLimitExceededException error = Assert.Throws<PdfLimitExceededException>(() => DocumentTestKit.Save(document));
        Assert.Equal(DocumentLimit.MaxPages, error.Limit);
    }

    [Fact]
    public void A_callback_that_throws_stops_the_save()
    {
        PdfDocument document = Document(Small(b => b.Footer = _ => throw new FormatException("footer failed")), Body(3));
        using MemoryStream stream = new();

        Assert.Throws<FormatException>(() => document.Save(stream));
        Assert.Equal(0, stream.Length);
    }

    [Fact]
    public void The_page_context_reports_the_page_size()
    {
        PageSize? seen = null;
        _ = DocumentTestKit.Save(Document(Small(b => b.Header = page =>
        {
            seen = page.PageSize;
            return null;
        }), Body(1)));

        Assert.Equal(Length.Pt(300), seen!.Value.Width);
    }
}
