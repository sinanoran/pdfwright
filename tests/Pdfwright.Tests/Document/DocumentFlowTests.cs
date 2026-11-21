using System.Text;
using UglyToad.PdfPig.Content;

namespace Pdfwright.Tests.Document;

/// <summary>The flow: blocks go top to bottom inside the margins, pages break between lines, a heading stays with what follows.</summary>
public class DocumentFlowTests
{
    private static readonly double PageWidth = PageSize.A4.Width.Points;
    private static readonly double PageHeight = PageSize.A4.Height.Points;
    private static readonly double Left = Length.Mm(15).Points;
    private static readonly double Right = PageWidth - Length.Mm(15).Points;
    private static readonly double Bottom = Length.Mm(20).Points;
    private static readonly double ContentHeight = PageHeight - (2 * Length.Mm(20).Points);

    private static PdfDocument NewDocument(Action<DocumentTestKit.OptionsBuilder>? change = null) => new(DocumentTestKit.Options(change));

    private static (byte[] Pdf, string[] Pages) SaveAndRead(PdfDocument document)
    {
        byte[] pdf = DocumentTestKit.Save(document);
        return (pdf, DocumentTestKit.PageTexts(pdf));
    }

    [Fact]
    public void An_empty_document_is_one_blank_page_that_qpdf_accepts()
    {
        PdfDocument document = NewDocument();

        (byte[] pdf, string[] pages) = SaveAndRead(document);

        Assert.Single(pages);
        Assert.Equal(string.Empty, pages[0]);
        Assert.Equal(1, document.Diagnostics.PageCount);
        DocumentTestKit.AssertValid(pdf, "doc-empty");
    }

    [Fact]
    public void A_paragraph_reads_back_and_stays_inside_the_margins()
    {
        PdfDocument document = NewDocument();
        string text = DocumentTestKit.LongText(60);
        document.Add(new Paragraph(text));

        byte[] pdf = DocumentTestKit.Save(document);

        using UglyToad.PdfPig.PdfDocument read = UglyToad.PdfPig.PdfDocument.Open(pdf);
        Page page = read.GetPage(1);
        Assert.Equal(595.2756, page.Width, 3);
        Assert.Equal(DocumentTestKit.Strip(text), DocumentTestKit.Strip(page.Text));
        Assert.All(page.Letters, l =>
        {
            Assert.True(l.StartBaseLine.X >= Left - 0.01, $"{l.Value} starts left of the margin");
            Assert.True(l.EndBaseLine.X <= Right + 0.01, $"{l.Value} ends right of the margin");
            Assert.True(l.StartBaseLine.Y <= PageHeight - Length.Mm(20).Points, $"{l.Value} is above the top margin");
            Assert.True(l.StartBaseLine.Y >= Bottom, $"{l.Value} is below the bottom margin");
        });
        Assert.True(DistinctLines(page) > 1, "the text wrapped");
        DocumentTestKit.AssertValid(pdf, "doc-paragraph");
    }

    /// <summary>The baselines of a page, top to bottom, with those within a third of a point (rounding of one line's glyphs) taken as one.</summary>
    private static double[] Baselines(Page page)
    {
        List<double> lines = [];
        foreach (double y in page.Letters.Select(l => l.StartBaseLine.Y).OrderByDescending(y => y))
        {
            if (lines.Count == 0 || lines[^1] - y > 0.3)
            {
                lines.Add(y);
            }
        }

        return [.. lines];
    }

    private static int DistinctLines(Page page) => Baselines(page).Length;

    [Fact]
    public void A_long_paragraph_is_split_between_lines_over_pages_and_reads_back_whole()
    {
        PdfDocument document = NewDocument();
        string text = DocumentTestKit.LongText(3000);
        document.Add(new Paragraph(text));

        (byte[] pdf, string[] pages) = SaveAndRead(document);

        Assert.True(pages.Length >= 3, $"{pages.Length} pages");
        Assert.Equal(pages.Length, document.Diagnostics.PageCount);
        Assert.Equal(DocumentTestKit.Strip(text), DocumentTestKit.Strip(string.Concat(pages)));
        using UglyToad.PdfPig.PdfDocument read = UglyToad.PdfPig.PdfDocument.Open(pdf);
        foreach (Page page in read.GetPages())
        {
            Assert.All(page.Letters, l => Assert.True(l.StartBaseLine.Y >= Bottom && l.StartBaseLine.Y <= PageHeight - Length.Mm(20).Points));
        }

        // Every page but the last is filled to within one line of the bottom margin.
        foreach (Page page in read.GetPages().SkipLast(1))
        {
            Assert.True(page.Letters.Min(l => l.StartBaseLine.Y) < Bottom + 20, "a page that is continued is full");
        }

        DocumentTestKit.AssertValid(pdf, "doc-long");
    }

    [Fact]
    public void A_heading_that_would_end_a_page_goes_to_the_next_with_the_first_line_after_it()
    {
        PdfDocument document = NewDocument();
        document.Add(new Paragraph("First paragraph"));

        // Leave about 55 points: room for the heading (20 before, 27 high) but not for the line after it.
        document.Add(new Spacer(Length.Pt(ContentHeight - 55 - 20)));
        document.Add(new Heading(1, "Kept heading"));
        document.Add(new Paragraph("Body after the heading"));

        (byte[] pdf, string[] pages) = SaveAndRead(document);

        Assert.Equal(2, pages.Length);
        Assert.Equal("First paragraph", pages[0]);
        Assert.Contains("Kept heading", pages[1], StringComparison.Ordinal);
        Assert.Contains("Body after the heading", pages[1], StringComparison.Ordinal);
        Assert.True(pages[1].IndexOf("Kept heading", StringComparison.Ordinal) < pages[1].IndexOf("Body", StringComparison.Ordinal));
        DocumentTestKit.AssertValid(pdf, "doc-keep");
    }

    [Fact]
    public void The_same_heading_with_nothing_after_it_fits_where_it_was()
    {
        PdfDocument document = NewDocument();
        document.Add(new Paragraph("First paragraph"));
        document.Add(new Spacer(Length.Pt(ContentHeight - 55 - 20)));
        document.Add(new Heading(1, "Last heading"));

        string[] pages = SaveAndRead(document).Pages;

        Assert.Single(pages);
        Assert.Contains("Last heading", pages[0], StringComparison.Ordinal);
    }

    [Fact]
    public void A_heading_at_the_top_of_a_page_is_not_pushed_on_and_its_space_before_is_dropped()
    {
        PdfDocument document = NewDocument();
        document.Add(new Heading(1, "Top"));
        document.Add(new Paragraph("Body"));

        byte[] pdf = DocumentTestKit.Save(document);

        using UglyToad.PdfPig.PdfDocument read = UglyToad.PdfPig.PdfDocument.Open(pdf);
        Letter first = read.GetPage(1).Letters[0];
        // The baseline is one ascent (about 1.07 em of 20 points) under the top margin: the 20 points of space before the heading are dropped.
        double drop = PageHeight - Length.Mm(20).Points - first.StartBaseLine.Y;
        Assert.InRange(drop, 0.9 * 20, 1.4 * 20);
    }

    [Fact]
    public void A_page_break_starts_a_new_page_and_two_in_a_row_leave_one_blank()
    {
        PdfDocument document = NewDocument();
        document.Add(new Paragraph("A")).Add(new PageBreak()).Add(new Paragraph("B")).Add(new PageBreak()).Add(new PageBreak()).Add(new Paragraph("C"));

        string[] pages = SaveAndRead(document).Pages;

        Assert.Equal(["A", "B", string.Empty, "C"], pages);
    }

    [Fact]
    public void A_page_break_at_the_very_start_makes_a_blank_first_page_and_one_at_the_end_adds_none()
    {
        PdfDocument start = NewDocument();
        start.Add(new PageBreak()).Add(new Paragraph("A"));
        PdfDocument end = NewDocument();
        end.Add(new Paragraph("A")).Add(new PageBreak());

        Assert.Equal([string.Empty, "A"], SaveAndRead(start).Pages);
        Assert.Equal(["A"], SaveAndRead(end).Pages);
    }

    [Fact]
    public void A_spacer_moves_what_follows_down_and_is_kept_at_the_top_of_a_page()
    {
        PdfDocument plain = NewDocument();
        plain.Add(new Paragraph("One")).Add(new Paragraph("Two"));
        PdfDocument spaced = NewDocument();
        spaced.Add(new Spacer(Length.Pt(50))).Add(new Paragraph("One")).Add(new Spacer(Length.Pt(30))).Add(new Paragraph("Two"));

        double Y(PdfDocument d, string word)
        {
            using UglyToad.PdfPig.PdfDocument read = UglyToad.PdfPig.PdfDocument.Open(DocumentTestKit.Save(d));
            return read.GetPage(1).Letters.First(l => l.Value == word[0].ToString()).StartBaseLine.Y;
        }

        double plainTwo = Y(plain, "Two");
        double spacedTwo = Y(spaced, "Two");
        double plainOne = Y(plain, "One");
        double spacedOne = Y(spaced, "One");

        Assert.Equal(50, plainOne - spacedOne, 3); // the spacer at the start of the document is kept: it is the author's intent
        Assert.Equal(80, plainTwo - spacedTwo, 3);
    }

    [Fact]
    public void A_spacer_taller_than_the_room_left_ends_the_page_and_does_not_push_text_onto_the_next_blank()
    {
        PdfDocument document = NewDocument();
        document.Add(new Paragraph("A")).Add(new Spacer(Length.Pt(5000))).Add(new Paragraph("B"));

        string[] pages = SaveAndRead(document).Pages;

        Assert.Equal(["A", "B"], pages);
    }

    [Fact]
    public void Runs_of_different_size_weight_and_colour_share_a_line()
    {
        PdfDocument document = NewDocument();
        document.Add(new Paragraph(
        [
            new TextRun("Plain "),
            new TextRun("Big", new TextStyle { Size = Length.Pt(20), Weight = 700, Color = Color.Red }),
            new TextRun(" and "),
            new TextRun("slanted", new TextStyle { Italic = true, Color = Color.FromHex("#0000ff") }),
        ]));

        byte[] pdf = DocumentTestKit.Save(document);

        using UglyToad.PdfPig.PdfDocument read = UglyToad.PdfPig.PdfDocument.Open(pdf);
        Page page = read.GetPage(1);
        Assert.Equal("Plain Big and slanted", page.Text);
        Assert.Equal(1, DistinctLines(page));
        Letter[] big = [.. page.Letters.Skip(6).Take(3)];
        Assert.Equal("Big", string.Concat(big.Select(l => l.Value)));
        Assert.All(big, l =>
        {
            Assert.Equal(20, l.PointSize, 3);
            Assert.Contains("Bold", l.FontName, StringComparison.Ordinal);
            Assert.Equal((1.0, 0.0, 0.0), l.Color.ToRGBValues());
        });
        Assert.All(page.Letters.Take(5), l =>
        {
            Assert.Equal(10, l.PointSize, 3);
            Assert.DoesNotContain("Bold", l.FontName, StringComparison.Ordinal);
            Assert.Equal((0.0, 0.0, 0.0), l.Color.ToRGBValues());
        });
        Assert.All(page.Letters.TakeLast(7), l =>
        {
            Assert.Contains("Italic", l.FontName, StringComparison.Ordinal);
            Assert.Equal((0.0, 0.0, 1.0), l.Color.ToRGBValues());
        });

        // The words after the big one sit after it on the same baseline: the width of the line is the sum of its runs'.
        double baseline = page.Letters[0].StartBaseLine.Y;
        Assert.All(page.Letters, l => Assert.Equal(baseline, l.StartBaseLine.Y, 3));
        DocumentTestKit.AssertValid(pdf, "doc-runs");
    }

    [Fact]
    public void A_line_is_as_high_as_its_largest_run()
    {
        PdfDocument small = NewDocument();
        small.Add(new Paragraph("a")).Add(new Paragraph("b"));
        PdfDocument mixed = NewDocument();
        mixed.Add(new Paragraph([new TextRun("a"), new TextRun("A", new TextStyle { Size = Length.Pt(30) })])).Add(new Paragraph("b"));

        double Gap(PdfDocument d)
        {
            using UglyToad.PdfPig.PdfDocument read = UglyToad.PdfPig.PdfDocument.Open(DocumentTestKit.Save(d));
            Letter[] letters = [.. read.GetPage(1).Letters];
            return letters[0].StartBaseLine.Y - letters[^1].StartBaseLine.Y;
        }

        Assert.True(Gap(mixed) > Gap(small) + 4, "a 30 point run makes its line taller: its descent is below the baseline of the line");
    }

    [Fact]
    public void A_line_break_inside_a_run_ends_the_line()
    {
        PdfDocument document = NewDocument();
        document.Add(new Paragraph("one\ntwo\r\nthree\rfour"));

        byte[] pdf = DocumentTestKit.Save(document);

        using UglyToad.PdfPig.PdfDocument read = UglyToad.PdfPig.PdfDocument.Open(pdf);
        Assert.Equal(4, DistinctLines(read.GetPage(1)));
        Assert.Equal("onetwothreefour", DocumentTestKit.Strip(read.GetPage(1).Text));
    }

    [Fact]
    public void Alignment_is_logical_start_is_the_right_edge_of_a_right_to_left_paragraph()
    {
        PdfDocument document = NewDocument();
        document.Add(new Paragraph("مرحبا بالعالم") { Style = new TextStyle { Language = "ar" } });
        document.Add(new Paragraph("Hello") { Alignment = TextAlignment.End });
        document.Add(new Paragraph("Middle") { Alignment = TextAlignment.Center });
        document.Add(new Paragraph("Start"));
        document.Add(new Paragraph("Forced") { Direction = TextDirection.RightToLeft });

        byte[] pdf = DocumentTestKit.Save(document);

        using UglyToad.PdfPig.PdfDocument read = UglyToad.PdfPig.PdfDocument.Open(pdf);
        Page page = read.GetPage(1);
        double[] bottoms = Baselines(page);
        Assert.True(bottoms.Length == 5, string.Join(" ", bottoms));

        (double min, double max) Extent(double y)
        {
            Letter[] line = [.. page.Letters.Where(l => Math.Abs(l.StartBaseLine.Y - y) <= 0.3)];
            return (line.Min(l => Math.Min(l.StartBaseLine.X, l.EndBaseLine.X)), line.Max(l => Math.Max(l.StartBaseLine.X, l.EndBaseLine.X)));
        }

        Assert.Equal(Right, Extent(bottoms[0]).max, 1.5); // Arabic, start: the right edge
        Assert.Equal(Right, Extent(bottoms[1]).max, 1.5); // English, end: the right edge
        (double cMin, double cMax) = Extent(bottoms[2]);
        Assert.Equal((Left + Right) / 2, (cMin + cMax) / 2, 1.5);
        Assert.Equal(Left, Extent(bottoms[3]).min, 1.5); // English, start: the left edge
        Assert.Equal(Right, Extent(bottoms[4]).max, 1.5); // Latin text in a right-to-left paragraph: start is the right edge
    }

    [Fact]
    public void The_document_direction_applies_to_paragraphs_that_set_none()
    {
        PdfDocument document = NewDocument(o => o.Direction = TextDirection.RightToLeft);
        document.Add(new Paragraph("Hello"));
        document.Add(new Paragraph("Hello") { Direction = TextDirection.LeftToRight });

        using UglyToad.PdfPig.PdfDocument read = UglyToad.PdfPig.PdfDocument.Open(DocumentTestKit.Save(document));
        double[] lines = Baselines(read.GetPage(1));

        double MaxX(double y) => read.GetPage(1).Letters.Where(l => Math.Abs(l.StartBaseLine.Y - y) <= 0.3).Max(l => l.EndBaseLine.X);
        Assert.Equal(Right, MaxX(lines[0]), 1.5);
        Assert.True(MaxX(lines[1]) < Left + 60);
    }

    [Fact]
    public void Headings_follow_the_default_scale_and_the_options_can_replace_it()
    {
        PdfDocument document = NewDocument();
        for (int level = 1; level <= 6; level++)
        {
            document.Add(new Heading(level, "H" + level));
        }

        using UglyToad.PdfPig.PdfDocument read = UglyToad.PdfPig.PdfDocument.Open(DocumentTestKit.Save(document));
        double[] sizes = [.. read.GetPage(1).Letters.Where(l => l.Value == "H").Select(l => l.PointSize)];

        Assert.Equal(new double[] { 20, 16, 13, 11.5, 10, 10 }, sizes);
        Assert.All(read.GetPage(1).Letters.Where(l => l.Value == "H"), l => Assert.Contains("Bold", l.FontName, StringComparison.Ordinal));

        TextStyle[] custom = [.. Enumerable.Range(1, 6).Select(i => new TextStyle { Size = Length.Pt(10 + (i * 2)), Weight = 400 })];
        PdfDocument second = NewDocument(o => o.HeadingStyles = custom);
        second.Add(new Heading(2, "X"));
        using UglyToad.PdfPig.PdfDocument read2 = UglyToad.PdfPig.PdfDocument.Open(DocumentTestKit.Save(second));
        Letter x = read2.GetPage(1).Letters.Single();
        Assert.Equal(14, x.PointSize, 3);
        Assert.DoesNotContain("Bold", x.FontName, StringComparison.Ordinal);
    }

    [Fact]
    public void A_heading_level_outside_1_to_6_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Heading(0, "x"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Heading(7, "x"));
        Assert.Throws<ArgumentException>(() => NewDocument(o => o.HeadingStyles = [new TextStyle()]));
    }

    [Fact]
    public void The_default_style_sets_the_family_size_and_language_of_everything()
    {
        PdfDocument document = NewDocument(o =>
        {
            o.DefaultStyle = new TextStyle { Family = "Noto Sans", Size = Length.Pt(16), Italic = true };
        });
        document.Add(new Paragraph("Big italic"));

        using UglyToad.PdfPig.PdfDocument read = UglyToad.PdfPig.PdfDocument.Open(DocumentTestKit.Save(document));

        Assert.All(read.GetPage(1).Letters, l =>
        {
            Assert.Equal(16, l.PointSize, 3);
            Assert.Contains("Italic", l.FontName, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Missing_glyphs_are_reported_with_their_language_and_page_and_the_document_still_saves()
    {
        FontCollection latinOnly = DocumentTestKit.Collection("NotoSans-Regular.ttf");
        PdfDocument document = NewDocument(o => o.Fonts = latinOnly);
        document.Add(new Paragraph("ok 日本 日") { Style = new TextStyle { Language = "ja" } });
        document.Add(new PageBreak());
        document.Add(new Paragraph("again 日") { Style = new TextStyle { Language = "ja" } });

        byte[] pdf = DocumentTestKit.Save(document);

        DocumentDiagnostics report = document.Diagnostics;
        Assert.Equal(2, report.PageCount);
        Assert.Equal(pdf.Length, report.ByteCount);
        Assert.Equal(
            [(0x65E5, "ja", 1), (0x672C, "ja", 1), (0x65E5, "ja", 2)],
            report.MissingGlyphs.Select(m => (m.CodePoint, m.Language, m.Page)));
        Assert.Equal("日", report.MissingGlyphs[0].Character);
        DocumentTestKit.AssertValid(pdf, "doc-missing");
    }

    [Fact]
    public void A_document_with_every_glyph_reports_none()
    {
        PdfDocument document = NewDocument();
        document.Add(new Paragraph("Everything is here: مرحبا नमस्ते"));

        DocumentTestKit.Save(document);

        Assert.Empty(document.Diagnostics.MissingGlyphs);
        Assert.Equal(1, document.Diagnostics.PageCount);
    }

    [Fact]
    public void Diagnostics_are_empty_before_the_first_save()
    {
        PdfDocument document = NewDocument();

        Assert.Equal(0, document.Diagnostics.PageCount);
        Assert.Equal(0, document.Diagnostics.ByteCount);
        Assert.Empty(document.Diagnostics.MissingGlyphs);
    }

    [Fact]
    public void A_style_with_a_bad_size_or_weight_is_refused_where_it_is_set()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => NewDocument(o => o.DefaultStyle = new TextStyle { Size = Length.Pt(0) }));
        Assert.Throws<ArgumentOutOfRangeException>(() => NewDocument(o => o.DefaultStyle = new TextStyle { Weight = 5000 }));

        ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(() => new TextStyle { Size = Length.Pt(-3) });
        Assert.Equal("Size", error.ParamName);
    }

    [Fact]
    public void Options_that_cannot_make_a_document_are_refused_at_once()
    {
        Assert.Throws<ArgumentException>(() => new PdfDocument(new PdfDocumentOptions { Fonts = new FontCollection() }));
        Assert.Throws<ArgumentNullException>(() => new PdfDocument(null!));
        Assert.Throws<ArgumentException>(() => NewDocument(o => o.Margins = Margins.Mm(200, 20, 200, 20)));
        Assert.Throws<ArgumentException>(() => NewDocument(o => o.Margins = Margins.Pt(-1, 0, 0, 0)));
        Assert.Throws<ArgumentNullException>(() => NewDocument().Add(null!));
    }

    [Fact]
    public void Page_size_and_margins_are_honoured()
    {
        PdfDocument document = NewDocument(o =>
        {
            o.PageSize = PageSize.Letter.Landscape();
            o.Margins = Margins.Pt(36, 72, 36, 72);
        });
        document.Add(new Paragraph(DocumentTestKit.LongText(400)));

        using UglyToad.PdfPig.PdfDocument read = UglyToad.PdfPig.PdfDocument.Open(DocumentTestKit.Save(document));
        Page page = read.GetPage(1);

        Assert.Equal(792, page.Width, 3);
        Assert.Equal(612, page.Height, 3);
        Assert.Equal(72, page.Letters.Min(l => l.StartBaseLine.X), 1);
        Assert.True(page.Letters.Max(l => l.EndBaseLine.X) <= 792 - 72 + 0.01);
    }

    [Fact]
    public void Metadata_reaches_the_info_dictionary_and_the_xmp_packet()
    {
        PdfDocument document = NewDocument(o =>
        {
            o.Title = "Açık faturalar";
            o.Subject = "Q4";
            o.Author = "Ayşe";
            o.Creator = "Plinth";
            o.Language = "tr";
            o.Creation = new DateTimeOffset(2026, 11, 21, 9, 30, 0, TimeSpan.Zero);
        });
        document.Add(new Paragraph("x"));

        byte[] pdf = DocumentTestKit.Save(document);

        using UglyToad.PdfPig.PdfDocument read = UglyToad.PdfPig.PdfDocument.Open(pdf);
        Assert.Equal("Açık faturalar", read.Information.Title);
        Assert.Equal("Q4", read.Information.Subject);
        Assert.Equal("Ayşe", read.Information.Author);
        Assert.Equal("Plinth", read.Information.Creator);
        Assert.Equal("Pdfwright", read.Information.Producer);
        Assert.Contains("20261121", read.Information.CreationDate, StringComparison.Ordinal);
        string raw = Encoding.Latin1.GetString(pdf);
        Assert.Contains("/Lang (tr)", raw, StringComparison.Ordinal);
        Assert.Contains("<xmp:CreateDate>2026-11-21T09:30:00Z</xmp:CreateDate>", raw, StringComparison.Ordinal);
        Assert.Contains("xml:lang=\"x-default\">AÃ§Ä±k faturalar", raw, StringComparison.Ordinal);
        DocumentTestKit.AssertValid(pdf, "doc-metadata");
    }
}
