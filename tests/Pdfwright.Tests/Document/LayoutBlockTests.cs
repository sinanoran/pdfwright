using Pdfwright.Tests.Images;
using System.Text;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Graphics;
using DrawingBlock = Pdfwright.Drawing;

namespace Pdfwright.Tests.Document;

/// <summary>The blocks of S5c: columns, callout, keep-together, drawing and image.</summary>
public class LayoutBlockTests
{
    // A page of 300 by 200 points with a margin of 10: a content area of 280 by 180.
    private static PdfDocumentOptions Small(Action<DocumentTestKit.OptionsBuilder>? change = null) =>
        DocumentTestKit.Options(b =>
        {
            b.PageSize = new PageSize(Length.Pt(300), Length.Pt(200));
            b.Margins = Margins.Uniform(Length.Pt(10));
            change?.Invoke(b);
        });

    private static byte[] Save(PdfDocumentOptions options, params Block[] blocks)
    {
        PdfDocument document = new(options);
        foreach (Block block in blocks)
        {
            document.Add(block);
        }

        return DocumentTestKit.Save(document);
    }

    private static UglyToad.PdfPig.PdfDocument Open(byte[] pdf) => UglyToad.PdfPig.PdfDocument.Open(pdf);

    private static Word Find(Page page, string text) => page.GetWords().First(w => w.Text == text);

    private static IEnumerable<UglyToad.PdfPig.Graphics.PdfPath> Paths(Page page) => page.Paths;

    private static string Content(byte[] pdf) => Encoding.Latin1.GetString(pdf);

    private static Paragraph P(string text, double? after = null) => new(text) { SpaceAfter = after is null ? null : Length.Pt(after.Value) };

    // --- columns --------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Three_cells_are_side_by_side_and_share_the_width_equally()
    {
        byte[] pdf = Save(Small(), new Columns([P("A"), P("B"), P("C")]) { Gap = Length.Pt(12) });

        using UglyToad.PdfPig.PdfDocument read = Open(pdf);
        Page page = read.GetPage(1);
        double cell = (280 - 24) / 3.0;
        Assert.Equal(10, Find(page, "A").BoundingBox.Left, 3);
        Assert.Equal(10 + cell + 12, Find(page, "B").BoundingBox.Left, 3);
        Assert.Equal(10 + (2 * (cell + 12)), Find(page, "C").BoundingBox.Left, 3);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public void One_to_four_cells_are_allowed(int count)
    {
        Block[] cells = [.. Enumerable.Range(0, count).Select(i => (Block)P($"c{i}"))];
        byte[] pdf = Save(Small(), new Columns(cells) { Gap = Length.Zero });

        using UglyToad.PdfPig.PdfDocument read = Open(pdf);
        double cell = 280.0 / count;
        for (int i = 0; i < count; i++)
        {
            Assert.Equal(10 + (i * cell), Find(read.GetPage(1), $"c{i}").BoundingBox.Left, 3);
        }
    }

    [Fact]
    public void In_a_right_to_left_document_the_first_cell_is_at_the_right_edge()
    {
        byte[] pdf = Save(Small(b => b.Direction = TextDirection.RightToLeft), new Columns([P("A"), P("B"), P("C")]) { Gap = Length.Pt(12) });

        using UglyToad.PdfPig.PdfDocument read = Open(pdf);
        Page page = read.GetPage(1);
        Assert.True(Find(page, "A").BoundingBox.Left > Find(page, "B").BoundingBox.Left);
        Assert.True(Find(page, "B").BoundingBox.Left > Find(page, "C").BoundingBox.Left);
    }

    [Fact]
    public void The_direction_of_the_row_overrides_the_documents()
    {
        byte[] pdf = Save(Small(), new Columns([P("A"), P("B")]) { Direction = TextDirection.RightToLeft });

        using UglyToad.PdfPig.PdfDocument read = Open(pdf);
        Assert.True(Find(read.GetPage(1), "A").BoundingBox.Left > Find(read.GetPage(1), "B").BoundingBox.Left);
    }

    [Fact]
    public void A_cell_holds_several_blocks_and_the_row_is_as_high_as_the_highest_cell()
    {
        byte[] pdf = Save(
            Small(),
            new Columns([[P("one", 0), P("two", 0), P("three", 0)], [P("solo", 0)]]) { Gap = Length.Pt(10) },
            P("after"));

        using UglyToad.PdfPig.PdfDocument read = Open(pdf);
        Page page = read.GetPage(1);
        double one = Find(page, "one").BoundingBox.Bottom;
        double two = Find(page, "two").BoundingBox.Bottom;
        double three = Find(page, "three").BoundingBox.Bottom;
        Assert.True(one > two && two > three);
        Assert.Equal(Find(page, "one").BoundingBox.Bottom, Find(page, "solo").BoundingBox.Bottom, 3);
        Assert.True(Find(page, "after").BoundingBox.Top < three);
    }

    [Fact]
    public void A_row_that_does_not_fit_moves_whole_to_the_next_page()
    {
        // A page holds about 14 lines of 10 point text; fill most of it, then add a row of two lines.
        byte[] pdf = Save(
            Small(),
            new Paragraph(string.Join('\n', Enumerable.Range(0, 12).Select(i => $"line{i}"))),
            new Columns([P("left one\nleft two\nleft three\nleft four"), P("right")]));

        using UglyToad.PdfPig.PdfDocument read = Open(pdf);
        Assert.Equal(2, read.NumberOfPages);
        Assert.Contains("line11", read.GetPage(1).Text);
        Assert.DoesNotContain("left", read.GetPage(1).Text);
        Assert.Contains("left four", read.GetPage(2).Text);
        Assert.Contains("right", read.GetPage(2).Text);
    }

    [Fact]
    public void A_row_taller_than_a_page_fails_the_save_and_writes_nothing()
    {
        PdfDocument document = new(Small());
        document.Add(new Columns([P(string.Join('\n', Enumerable.Range(0, 40).Select(i => $"row{i}"))), P("x")]));
        using MemoryStream stream = new();

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => document.Save(stream));

        Assert.Contains("columns", error.Message);
        Assert.Equal(0, stream.Length);
    }

    [Fact]
    public void Columns_validate_their_cells()
    {
        Assert.Throws<ArgumentNullException>(() => new Columns((IEnumerable<Block>)null!));
        Assert.Equal("cells", Assert.Throws<ArgumentOutOfRangeException>(() => new Columns(Array.Empty<Block>())).ParamName);
        Assert.Equal("cells", Assert.Throws<ArgumentOutOfRangeException>(() => new Columns([P("1"), P("2"), P("3"), P("4"), P("5")])).ParamName);
        Assert.Equal("cells", Assert.Throws<ArgumentException>(() => new Columns([P("1"), null!])).ParamName);
        Assert.Throws<ArgumentException>(() => new Columns([new Table([new TableColumn()], [new TableRow(["x"])])]));
        Assert.Throws<ArgumentException>(() => new Columns([new PageBreak()]));
        Assert.Throws<ArgumentException>(() => new Columns([new Columns([P("inner")])]));
        Assert.Throws<ArgumentException>(() => new Columns([new Callout(new Columns([P("inner")]))]));
        Assert.Equal("Gap", Assert.Throws<ArgumentOutOfRangeException>(() => new Columns([P("1")]) { Gap = Length.Pt(-1) }).ParamName);
        Assert.Equal("Direction", Assert.Throws<ArgumentOutOfRangeException>(() => new Columns([P("1")]) { Direction = (TextDirection)99 }).ParamName);
        Assert.Equal("SpaceAfter", Assert.Throws<ArgumentOutOfRangeException>(() => new Columns([P("1")]) { SpaceAfter = Length.Pt(-1) }).ParamName);
    }

    [Fact]
    public void Columns_are_equal_when_their_cells_are_equal_block_by_block()
    {
        Columns a = new([P("1"), P("2")]);
        Columns b = new([P("1"), P("2")]);
        Columns c = new([P("1"), P("3")]);
        Columns nested1 = new([[P("1"), P("2")], [P("3")]]);
        Columns nested2 = new([[P("1"), P("2")], [P("3")]]);

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, c);
        Assert.Equal(nested1, nested2);
        Assert.NotEqual(a, a with { Gap = Length.Pt(3) });
        Assert.Equal(2, a.Cells.Count);
    }

    [Fact]
    public void The_cells_are_copied_so_that_the_callers_list_does_not_change_the_block()
    {
        List<Block> cell = [P("1")];
        Columns columns = new([cell]);
        cell.Add(P("2"));

        Assert.Single(columns.Cells[0]);
    }

    // --- callout --------------------------------------------------------------------------------------------------------------

    private static (double Left, double Top, double Right, double Bottom) Box(UglyToad.PdfPig.Graphics.PdfPath path)
    {
        var r = path.GetBoundingRectangle()!.Value;
        return (r.Left, r.Top, r.Right, r.Bottom);
    }

    [Fact]
    public void A_callout_draws_a_box_as_wide_as_the_content_area_and_its_text_inside_the_padding()
    {
        byte[] pdf = Save(Small(), new Callout(P("Note")) { Padding = Margins.Pt(6, 7, 6, 7), Border = new BorderLine(Length.Pt(2), Color.Red) });

        using UglyToad.PdfPig.PdfDocument read = Open(pdf);
        Page page = read.GetPage(1);
        var boxes = Paths(page).Select(Box).ToList();
        Assert.Contains(boxes, b => Math.Abs(b.Left - 10) < 0.01 && Math.Abs(b.Right - 290) < 0.01 && Math.Abs(b.Top - 190) < 0.01);
        Assert.Equal(10 + 2 + 7, Find(page, "Note").BoundingBox.Left, 1);
        Assert.True(190 - Find(page, "Note").BoundingBox.Top >= 2 + 6 - 0.5);
    }

    [Fact]
    public void A_callout_with_no_fill_and_no_border_draws_no_box()
    {
        byte[] pdf = Save(Small(), new Callout(P("Plain")) { Fill = null, Border = null });

        using UglyToad.PdfPig.PdfDocument read = Open(pdf);
        Assert.Empty(Paths(read.GetPage(1)));
    }

    [Fact]
    public void A_corner_radius_draws_curves()
    {
        byte[] pdf = Save(Small(), new Callout(P("Round")) { CornerRadius = Length.Pt(8), Border = null });

        Assert.Contains(" c\n", Content(pdf).Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    private static Block[] Lines(int count, string prefix = "n") =>
        [.. Enumerable.Range(0, count).Select(i => (Block)P($"{prefix}{i}", 0))];

    [Fact]
    public void A_callout_that_does_not_fit_is_split_between_its_lines_and_each_page_gets_a_whole_box()
    {
        byte[] pdf = Save(Small(), new Callout(Lines(30)));

        using UglyToad.PdfPig.PdfDocument read = Open(pdf);
        Assert.True(read.NumberOfPages >= 3);
        string all = string.Join(' ', DocumentTestKit.PageTexts(pdf));
        for (int i = 0; i < 30; i++)
        {
            Assert.Contains($"n{i}", all);
        }

        foreach (Page page in read.GetPages())
        {
            var boxes = Paths(page).Select(Box).ToList();
            // The fill and the border of the page's part: both start at the top margin and end inside the page.
            Assert.Contains(boxes, b => Math.Abs(b.Top - 190) < 0.01 && b.Bottom >= 9.99);
        }

    }

    [Fact]
    public void A_callout_that_asks_to_be_kept_together_moves_whole_to_the_next_page()
    {
        Block filler = new Paragraph(string.Join('\n', Enumerable.Range(0, 8).Select(i => $"fill{i}")));

        byte[] split = Save(Small(), filler, new Callout(Lines(6)));
        byte[] kept = Save(Small(), filler, new Callout(Lines(6)) { KeepTogether = true });

        string[] splitPages = DocumentTestKit.PageTexts(split);
        string[] keptPages = DocumentTestKit.PageTexts(kept);
        Assert.Contains("n0", splitPages[0]);
        Assert.Contains("n5", splitPages[1]);
        Assert.DoesNotContain("n0", keptPages[0]);
        Assert.Contains("n0", keptPages[1]);
        Assert.Contains("n5", keptPages[1]);
    }

    [Fact]
    public void A_keep_together_callout_taller_than_a_page_falls_back_to_a_split()
    {
        byte[] pdf = Save(Small(), new Callout(Lines(40)) { KeepTogether = true });

        string[] pages = DocumentTestKit.PageTexts(pdf);
        Assert.True(pages.Length >= 3);
        Assert.Contains("n0", pages[0]);
        Assert.Contains("n39", string.Join(' ', pages));
    }

    [Fact]
    public void A_heading_is_not_left_alone_at_the_end_of_a_callouts_page()
    {
        Block[] content = [.. Lines(11), new Heading(3, "Heading"), P("under heading")];
        byte[] pdf = Save(Small(), new Callout(content));

        string[] pages = DocumentTestKit.PageTexts(pdf);
        Assert.DoesNotContain("Heading", pages[0].Replace("under heading", string.Empty));
        Assert.Contains("Heading", pages[1]);
        Assert.Contains("under heading", pages[1]);
    }

    [Fact]
    public void A_keep_together_paragraph_in_a_callout_moves_to_the_next_part()
    {
        Block[] content = [.. Lines(10), new Paragraph("k1\nk2\nk3") { KeepTogether = true }];
        byte[] pdf = Save(Small(), new Callout(content));

        string[] pages = DocumentTestKit.PageTexts(pdf);
        Assert.DoesNotContain("k1", pages[0]);
        Assert.Contains("k1", pages[1]);
        Assert.Contains("k3", pages[1]);
    }

    [Fact]
    public void Columns_inside_a_callout_are_kept_whole()
    {
        Block[] content = [.. Lines(12), new Columns([P("kpi one"), P("kpi two")]) { Gap = Length.Pt(10) }];
        byte[] pdf = Save(Small(), new Callout(content));

        using UglyToad.PdfPig.PdfDocument read = Open(pdf);
        Assert.Equal(2, read.NumberOfPages);
        Assert.DoesNotContain("kpi", read.GetPage(1).Text);
        Assert.Contains("kpi one", read.GetPage(2).Text);
        Assert.True(Find(read.GetPage(2), "two").BoundingBox.Left > Find(read.GetPage(2), "one").BoundingBox.Left);
    }

    [Fact]
    public void A_callout_inside_a_column_cell_is_drawn_in_the_cell()
    {
        byte[] pdf = Save(Small(), new Columns([new Callout(P("left note")), P("right")]) { Gap = Length.Pt(20) });

        using UglyToad.PdfPig.PdfDocument read = Open(pdf);
        Page page = read.GetPage(1);
        double cell = (280 - 20) / 2.0;
        var boxes = Paths(page).Select(Box).ToList();
        Assert.Contains(boxes, b => Math.Abs(b.Left - 10) < 0.01 && Math.Abs(b.Right - (10 + cell)) < 0.01);
        Assert.Equal(10 + cell + 20, Find(page, "right").BoundingBox.Left, 1);
    }

    [Fact]
    public void A_block_taller_than_the_part_inside_a_callout_fails()
    {
        PdfDocument document = new(Small());
        document.Add(new Callout(new Columns([P(string.Join('\n', Enumerable.Range(0, 40).Select(i => $"r{i}")))])));

        Assert.Throws<InvalidOperationException>(() => DocumentTestKit.Save(document));
    }

    [Fact]
    public void A_callout_validates_its_content_and_settings()
    {
        Assert.Throws<ArgumentNullException>(() => new Callout((Block)null!));
        Assert.Throws<ArgumentNullException>(() => new Callout((IEnumerable<Block>)null!));
        Assert.Throws<ArgumentException>(() => new Callout(Array.Empty<Block>()));
        Assert.Throws<ArgumentException>(() => new Callout(new PageBreak()));
        Assert.Throws<ArgumentException>(() => new Callout(new Table([new TableColumn()], [new TableRow(["x"])])));
        Assert.Throws<ArgumentException>(() => new Callout(new Block[] { P("a"), null! }));
        Assert.Equal("Padding", Assert.Throws<ArgumentOutOfRangeException>(() => new Callout(P("a")) { Padding = Margins.Pt(1, -1, 1, 1) }).ParamName);
        Assert.Equal("CornerRadius", Assert.Throws<ArgumentOutOfRangeException>(() => new Callout(P("a")) { CornerRadius = Length.Pt(-1) }).ParamName);
        Assert.Equal("Border", Assert.Throws<ArgumentOutOfRangeException>(() => new Callout(P("a")) { Border = default(BorderLine) }).ParamName);
        Assert.Equal("Direction", Assert.Throws<ArgumentOutOfRangeException>(() => new Callout(P("a")) { Direction = (TextDirection)7 }).ParamName);
        Assert.Equal("SpaceBefore", Assert.Throws<ArgumentOutOfRangeException>(() => new Callout(P("a")) { SpaceBefore = Length.Pt(-2) }).ParamName);
    }

    [Fact]
    public void Callouts_are_equal_when_their_content_and_settings_are()
    {
        Callout a = new([P("1"), P("2")]) { CornerRadius = Length.Pt(3) };
        Callout b = new([P("1"), P("2")]) { CornerRadius = Length.Pt(3) };

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, new Callout([P("1"), P("2")]));
        Assert.NotEqual(a, a with { KeepTogether = true });
        Assert.NotEqual(a, new Callout([P("1"), P("x")]) { CornerRadius = Length.Pt(3) });
    }

    // --- keep-together paragraph ----------------------------------------------------------------------------------------------

    [Fact]
    public void A_keep_together_paragraph_moves_whole_to_the_next_page_and_others_are_split()
    {
        Block filler = new Paragraph(string.Join('\n', Enumerable.Range(0, 11).Select(i => $"fill{i}")));
        string text = "k0\nk1\nk2\nk3";

        string[] split = DocumentTestKit.PageTexts(Save(Small(), filler, new Paragraph(text)));
        string[] kept = DocumentTestKit.PageTexts(Save(Small(), filler, new Paragraph(text) { KeepTogether = true }));

        Assert.Contains("k0", split[0]);
        Assert.Contains("k3", split[1]);
        Assert.DoesNotContain("k0", kept[0]);
        Assert.Contains("k0", kept[1]);
        Assert.Contains("k3", kept[1]);
    }

    [Fact]
    public void A_keep_together_paragraph_taller_than_a_page_falls_back_to_normal_flow()
    {
        string[] pages = DocumentTestKit.PageTexts(Save(Small(), new Paragraph(string.Join('\n', Enumerable.Range(0, 40).Select(i => $"w{i}"))) { KeepTogether = true }));

        Assert.True(pages.Length >= 3);
        Assert.Contains("w0", pages[0]);
    }

    [Fact]
    public void Keep_together_takes_part_in_the_paragraphs_equality()
    {
        Assert.NotEqual(new Paragraph("a"), new Paragraph("a") { KeepTogether = true });
        Assert.Equal(new Paragraph("a") { KeepTogether = true }, new Paragraph("a") { KeepTogether = true });
    }

    [Fact]
    public void A_heading_is_kept_with_a_row_of_columns_that_follows_it()
    {
        Block filler = new Paragraph(string.Join('\n', Enumerable.Range(0, 11).Select(i => $"fill{i}")));
        string[] pages = DocumentTestKit.PageTexts(Save(Small(), filler, new Heading(4, "Heading"), new Columns([P("one\ntwo\nthree\nfour"), P("x")])));

        Assert.DoesNotContain("Heading", pages[0]);
        Assert.Contains("Heading", pages[1]);
    }

    // --- drawing --------------------------------------------------------------------------------------------------------------

    private static DrawingBlock Chart(Action<DrawingCanvas> draw, double width = 100, double height = 50) => new(Length.Pt(width), Length.Pt(height), draw);

    private static DrawingPath Rectangle(double x, double y, double w, double h) => new DrawingPathBuilder().Rectangle(x, y, w, h).Build();

    [Fact]
    public void A_drawing_draws_with_its_origin_at_the_top_left_of_its_box_and_y_down()
    {
        byte[] pdf = Save(Small(), Chart(c => c.Fill(Rectangle(5, 7, 20, 10), Color.Red)));

        using UglyToad.PdfPig.PdfDocument read = Open(pdf);
        var box = Paths(read.GetPage(1)).Where(p => p.IsFilled).Select(Box).Single();
        Assert.Equal(10 + 5, box.Left, 3);
        Assert.Equal(10 + 25, box.Right, 3);
        Assert.Equal(200 - 10 - 7, box.Top, 3);
        Assert.Equal(200 - 10 - 17, box.Bottom, 3);
    }

    [Fact]
    public void A_drawing_is_clipped_to_its_box()
    {
        byte[] pdf = Save(Small(), Chart(c => c.Fill(Rectangle(-50, -50, 300, 300), Color.Blue)), P("below"));

        string content = Content(pdf);
        Assert.Contains("\nW\nn\n", content.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    [Fact]
    public void The_callback_runs_when_the_document_is_saved_and_once_for_each_place_it_is_drawn()
    {
        int calls = 0;
        DrawingBlock chart = Chart(_ => calls++);
        PdfDocument document = new(Small());
        document.Add(chart).Add(chart);

        Assert.Equal(0, calls);
        _ = DocumentTestKit.Save(document);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void The_canvas_is_valid_only_during_the_callback()
    {
        DrawingCanvas? kept = null;
        _ = Save(Small(), Chart(c => kept = c));

        Assert.NotNull(kept);
        Assert.Throws<ObjectDisposedException>(() => kept!.Save());
        Assert.Throws<ObjectDisposedException>(() => kept!.Fill(Rectangle(0, 0, 1, 1), Color.Black));
        Assert.Throws<ObjectDisposedException>(() => kept!.DrawText("x", 0, 0));
        Assert.Equal(100, kept!.Width);
        Assert.Equal(50, kept!.Height);
    }

    [Fact]
    public void A_save_that_is_still_open_when_the_callback_ends_is_closed_and_the_file_is_valid()
    {
        byte[] pdf = Save(Small(), Chart(c =>
        {
            c.Save();
            c.Translate(5, 5);
            c.Save();
            c.Fill(Rectangle(0, 0, 10, 10), Color.Green);
        }), P("after"));

        Assert.Contains("after", DocumentTestKit.PageTexts(pdf)[0]);
        DocumentTestKit.AssertValid(pdf, "drawing-open-save");
    }

    [Fact]
    public void A_restore_without_a_save_is_an_error()
    {
        PdfDocument document = new(Small());
        document.Add(Chart(c => c.Restore()));

        Assert.Throws<InvalidOperationException>(() => DocumentTestKit.Save(document));
    }

    [Fact]
    public void Transforms_clips_dashes_and_opacity_are_written()
    {
        byte[] pdf = Save(Small(), Chart(c =>
        {
            c.Save();
            c.Translate(10, 10);
            c.Scale(2, 2);
            c.Rotate(15);
            c.Transform(1, 0, 0, 1, 1, 1);
            c.Clip(Rectangle(0, 0, 30, 30));
            DrawingPath line = new DrawingPathBuilder().MoveTo(0, 0).LineTo(40, 0).Build();
            c.Stroke(line, new DrawingPen(Color.Blue, 2, LineCap.Round, LineJoin.Bevel, dashes: [4, 2], opacity: 0.5));
            c.Fill(Rectangle(0, 0, 5, 5), Color.Red, 0.25, FillRule.EvenOdd);
            c.Restore();
        }));

        string content = Content(pdf).Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Contains("[4 2] 0 d", content);
        Assert.Contains("\nW\nn\n", content);
        Assert.Contains("1 J", content);
        Assert.Contains("2 j", content);
        Assert.Contains("/ExtGState", content);
        Assert.Contains("f*", content);
        DocumentTestKit.AssertValid(pdf, "drawing-state");
    }

    [Fact]
    public void Text_at_a_point_is_extracted_where_it_was_drawn_with_the_style_given()
    {
        byte[] pdf = Save(Small(), Chart(c => c.DrawText("Chart 42", 20, 30, new TextStyle { Weight = 700, Size = Length.Pt(12) })));

        using UglyToad.PdfPig.PdfDocument read = Open(pdf);
        Word word = Find(read.GetPage(1), "Chart");
        Assert.Equal(10 + 20, word.BoundingBox.Left, 1);
        Assert.Equal(200 - 10 - 30, word.BoundingBox.Bottom, 0);
        Assert.Contains("42", read.GetPage(1).Text);
        Assert.Equal(12, word.Letters[0].PointSize, 1);
    }

    [Fact]
    public void Text_can_be_centred_and_end_aligned_on_the_point()
    {
        byte[] pdf = Save(Small(), Chart(c =>
        {
            c.DrawText("MID", 50, 15, alignment: TextAlignment.Center);
            c.DrawText("END", 100, 35, alignment: TextAlignment.End);
        }));

        using UglyToad.PdfPig.PdfDocument read = Open(pdf);
        Word mid = Find(read.GetPage(1), "MID");
        Word end = Find(read.GetPage(1), "END");
        Assert.Equal(10 + 50, (mid.BoundingBox.Left + mid.BoundingBox.Right) / 2, 1);
        Assert.Equal(10 + 100, end.BoundingBox.Right, 1);
    }

    [Fact]
    public void A_missing_glyph_in_drawn_text_is_reported_with_the_page()
    {
        PdfDocument document = new(Small());
        document.Add(new PageBreak());
        document.Add(Chart(c => c.DrawText("a" + char.ConvertFromUtf32(0x4E00), 5, 20, new TextStyle { Language = "zh" })));
        _ = DocumentTestKit.Save(document);

        MissingGlyph missing = Assert.Single(document.Diagnostics.MissingGlyphs);
        Assert.Equal(0x4E00, missing.CodePoint);
        Assert.Equal(2, missing.Page);
        Assert.Equal("zh", missing.Language);
    }

    [Fact]
    public void A_family_that_is_not_in_the_collection_is_reported_for_drawn_text()
    {
        PdfDocument document = new(Small());
        document.Add(Chart(c => c.DrawText("x", 5, 20, new TextStyle { Family = "Nope Sans" })));
        _ = DocumentTestKit.Save(document);

        Assert.Equal(["Nope Sans"], document.Diagnostics.MissingFamilies);
    }

    [Fact]
    public void A_picture_is_drawn_into_a_rectangle_of_the_canvas()
    {
        PdfImage picture = PdfImage.FromBytes(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Images", "Files", "gradient-24x16.jpg")));
        byte[] pdf = Save(Small(), Chart(c => c.DrawImage(picture, 10, 5, 48, 32, 0.5)));

        using UglyToad.PdfPig.PdfDocument read = Open(pdf);
        IPdfImage image = Assert.Single(read.GetPage(1).GetImages());
        Assert.Equal(10 + 10, image.BoundingBox.Left, 3);
        Assert.Equal(48, image.BoundingBox.Width, 3);
        Assert.Equal(32, image.BoundingBox.Height, 3);
        Assert.Equal(200 - 10 - 5, image.BoundingBox.Top, 3);
    }

    [Fact]
    public void A_drawing_that_does_not_fit_moves_to_the_next_page()
    {
        byte[] pdf = Save(Small(), new Paragraph(string.Join('\n', Enumerable.Range(0, 11).Select(i => $"fill{i}"))), Chart(c => c.Fill(Rectangle(0, 0, 100, 50), Color.Red)));

        using UglyToad.PdfPig.PdfDocument read = Open(pdf);
        Assert.Equal(2, read.NumberOfPages);
        Assert.Empty(Paths(read.GetPage(1)));
        Assert.Single(Paths(read.GetPage(2)), p => p.IsFilled);
    }

    [Fact]
    public void A_drawing_wider_than_the_space_or_taller_than_a_page_fails_the_save()
    {
        InvalidOperationException wide = Assert.Throws<InvalidOperationException>(() => Save(Small(), Chart(_ => { }, width: 400)));
        InvalidOperationException tall = Assert.Throws<InvalidOperationException>(() => Save(Small(), Chart(_ => { }, height: 400)));
        InvalidOperationException narrow = Assert.Throws<InvalidOperationException>(() => Save(Small(), new Columns([Chart(_ => { }, width: 200), P("x")])));

        Assert.Contains("wide", wide.Message);
        Assert.Contains("high", tall.Message);
        Assert.Contains("wide", narrow.Message);
    }

    [Fact]
    public void A_callback_that_throws_stops_the_save_and_nothing_is_written()
    {
        PdfDocument document = new(Small());
        document.Add(Chart(_ => throw new FormatException("chart failed")));
        using MemoryStream stream = new();

        Assert.Throws<FormatException>(() => document.Save(stream));

        Assert.Equal(0, stream.Length);
    }

    [Fact]
    public void A_drawing_validates_its_arguments_and_keeps_its_alt_text()
    {
        Assert.Equal("width", Assert.Throws<ArgumentOutOfRangeException>(() => new DrawingBlock(Length.Zero, Length.Pt(1), _ => { })).ParamName);
        Assert.Equal("height", Assert.Throws<ArgumentOutOfRangeException>(() => new DrawingBlock(Length.Pt(1), Length.Pt(-1), _ => { })).ParamName);
        Assert.Equal("draw", Assert.Throws<ArgumentNullException>(() => new DrawingBlock(Length.Pt(1), Length.Pt(1), null!)).ParamName);
        Assert.Equal("SpaceBefore", Assert.Throws<ArgumentOutOfRangeException>(() => new DrawingBlock(Length.Pt(1), Length.Pt(1), _ => { }) { SpaceBefore = Length.Pt(-1) }).ParamName);
        Assert.Equal("Bar chart", new DrawingBlock(Length.Pt(1), Length.Pt(1), _ => { }) { AltText = "Bar chart" }.AltText);
    }

    [Fact]
    public void The_public_canvas_checks_its_numbers()
    {
        PdfDocument document = new(Small());
        List<Exception> errors = [];
        document.Add(Chart(c =>
        {
            void Try(Action action)
            {
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    errors.Add(e);
                }
            }

            Try(() => c.Translate(double.NaN, 0));
            Try(() => c.Scale(0, 1));
            Try(() => c.Fill(null!, Color.Black));
            Try(() => c.Fill(Rectangle(0, 0, 1, 1), Color.Black, 2));
            Try(() => c.DrawText(null!, 0, 0));
            Try(() => c.DrawText("x", 0, 0, alignment: (TextAlignment)9));
            Try(() => c.DrawImage(null!, 0, 0, 1, 1));
            Try(() => c.Clip(Rectangle(0, 0, 1, 1), (FillRule)4));
            Try(() => c.Stroke(Rectangle(0, 0, 1, 1), (DrawingPen)null!));
        }));
        _ = DocumentTestKit.Save(document);

        Assert.Equal(9, errors.Count);
        Assert.IsType<ArgumentException>(errors[0]);
        Assert.IsType<ArgumentException>(errors[1]);
        Assert.IsType<ArgumentNullException>(errors[2]);
        Assert.IsType<ArgumentException>(errors[3]);
        Assert.IsType<ArgumentNullException>(errors[4]);
        Assert.IsType<ArgumentOutOfRangeException>(errors[5]);
        Assert.IsType<ArgumentNullException>(errors[6]);
        Assert.IsType<ArgumentOutOfRangeException>(errors[7]);
        Assert.IsType<ArgumentNullException>(errors[8]);
    }

    [Fact]
    public void The_path_builder_and_the_pen_validate_and_the_path_is_immutable()
    {
        Assert.Throws<ArgumentException>(() => new DrawingPathBuilder().MoveTo(double.NaN, 0));
        Assert.Throws<InvalidOperationException>(() => new DrawingPathBuilder().LineTo(1, 1));
        Assert.Throws<ArgumentException>(() => new DrawingPen(Color.Black, width: -1));
        Assert.Throws<ArgumentException>(() => new DrawingPen(Color.Black, dashes: [0, 0]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DrawingPen(Color.Black, cap: (LineCap)9));
        DrawingPathBuilder builder = new();
        DrawingPath first = builder.MoveTo(0, 0).LineTo(1, 1).Build();
        _ = builder.LineTo(5, 5);
        Assert.False(first.IsEmpty);
        Assert.True(new DrawingPathBuilder().Build().IsEmpty);
        DrawingPen pen = new(Color.Red, 3, dashes: [1, 2]);
        Assert.Equal([1d, 2d], pen.Dashes);
        Assert.Equal(3, pen.Width);
    }

    // --- images ---------------------------------------------------------------------------------------------------------------

    private static PdfImage Jpeg() => PdfImage.FromBytes(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Images", "Files", "gradient-24x16.jpg")));

    private static PdfImage PngWithAlpha() => PdfImage.FromBytes(ImageTestKit.Png(8, 4, 8, 6));

    [Fact]
    public void An_image_has_its_natural_size_at_96_pixels_to_the_inch()
    {
        byte[] pdf = Save(Small(), new Image(Jpeg()));

        using UglyToad.PdfPig.PdfDocument read = Open(pdf);
        IPdfImage image = Assert.Single(read.GetPage(1).GetImages());
        Assert.Equal(18, image.BoundingBox.Width, 3);
        Assert.Equal(12, image.BoundingBox.Height, 3);
        Assert.Equal(10, image.BoundingBox.Left, 3);
        Assert.Equal(190, image.BoundingBox.Top, 3);
    }

    [Fact]
    public void One_given_side_sets_the_other_by_the_pictures_proportions()
    {
        byte[] byWidth = Save(Small(), new Image(Jpeg()) { Width = Length.Pt(60) });
        byte[] byHeight = Save(Small(), new Image(Jpeg()) { Height = Length.Pt(30) });

        using UglyToad.PdfPig.PdfDocument a = Open(byWidth);
        using UglyToad.PdfPig.PdfDocument b = Open(byHeight);
        Assert.Equal(40, a.GetPage(1).GetImages().Single().BoundingBox.Height, 3);
        Assert.Equal(45, b.GetPage(1).GetImages().Single().BoundingBox.Width, 3);
    }

    [Fact]
    public void An_image_wider_than_the_space_is_scaled_down_in_proportion()
    {
        byte[] pdf = Save(Small(), new Image(Jpeg()) { Width = Length.Pt(560), Height = Length.Pt(100) });

        using UglyToad.PdfPig.PdfDocument read = Open(pdf);
        IPdfImage image = read.GetPage(1).GetImages().Single();
        Assert.Equal(280, image.BoundingBox.Width, 3);
        Assert.Equal(50, image.BoundingBox.Height, 3);
    }

    [Fact]
    public void An_image_taller_than_a_page_is_scaled_to_fit_the_page()
    {
        byte[] pdf = Save(Small(), new Image(Jpeg()) { Height = Length.Pt(900), Width = Length.Pt(100) });

        using UglyToad.PdfPig.PdfDocument read = Open(pdf);
        Assert.Equal(1, read.NumberOfPages);
        Assert.Equal(180, read.GetPage(1).GetImages().Single().BoundingBox.Height, 3);
    }

    [Fact]
    public void An_image_inside_a_column_cell_is_scaled_to_the_cell_and_can_be_centred_or_end_aligned()
    {
        byte[] pdf = Save(
            Small(),
            new Columns([[new Image(Jpeg()) { Width = Length.Pt(300) }], [new Image(Jpeg()) { Width = Length.Pt(40), Alignment = TextAlignment.Center }], [new Image(Jpeg()) { Width = Length.Pt(40), Alignment = TextAlignment.End }]]) { Gap = Length.Pt(10) });

        using UglyToad.PdfPig.PdfDocument read = Open(pdf);
        IPdfImage[] images = [.. read.GetPage(1).GetImages().OrderBy(i => i.BoundingBox.Left)];
        double cell = (280 - 20) / 3.0;
        Assert.Equal(cell, images[0].BoundingBox.Width, 2);
        Assert.Equal(10 + cell + 10 + ((cell - 40) / 2), images[1].BoundingBox.Left, 2);
        Assert.Equal(10 + (2 * (cell + 10)) + cell - 40, images[2].BoundingBox.Left, 2);
    }

    [Fact]
    public void An_end_aligned_image_in_a_right_to_left_document_is_at_the_left_edge()
    {
        byte[] pdf = Save(Small(b => b.Direction = TextDirection.RightToLeft), new Image(Jpeg()) { Width = Length.Pt(40), Alignment = TextAlignment.End }, new Image(Jpeg()) { Width = Length.Pt(40) });

        using UglyToad.PdfPig.PdfDocument read = Open(pdf);
        IPdfImage[] images = [.. read.GetPage(1).GetImages().OrderByDescending(i => i.BoundingBox.Top)];
        Assert.Equal(10, images[0].BoundingBox.Left, 3);
        Assert.Equal(250, images[1].BoundingBox.Left, 3);
    }

    [Fact]
    public void A_png_with_alpha_keeps_its_transparency_and_a_picture_is_written_once_however_often_it_is_used()
    {
        PdfImage png = PngWithAlpha();
        byte[] pdf = Save(Small(), new Image(png), new Image(png) { Width = Length.Pt(20) }, Chart(c => c.DrawImage(png, 0, 0, 10, 5)));

        Assert.True(png.HasTransparency);
        Assert.Equal(8, png.PixelWidth);
        Assert.Equal(4, png.PixelHeight);
        string content = Content(pdf);
        Assert.Contains("/SMask", content);
        Assert.Equal(2, CountOf(content, "/Subtype /Image")); // the picture and its soft mask, once
        using UglyToad.PdfPig.PdfDocument read = Open(pdf);
        Assert.Equal(3, read.GetPage(1).GetImages().Count());
        DocumentTestKit.AssertValid(pdf, "images");
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
    public void An_image_that_does_not_fit_moves_to_the_next_page()
    {
        byte[] pdf = Save(Small(), new Paragraph(string.Join('\n', Enumerable.Range(0, 12).Select(i => $"fill{i}"))), new Image(Jpeg()) { Width = Length.Pt(120) });

        using UglyToad.PdfPig.PdfDocument read = Open(pdf);
        Assert.Empty(read.GetPage(1).GetImages());
        Assert.Single(read.GetPage(2).GetImages());
    }

    [Fact]
    public void Bad_picture_data_is_refused_when_the_picture_is_made()
    {
        ArgumentException notAnImage = Assert.Throws<ArgumentException>(() => PdfImage.FromBytes(new byte[] { 1, 2, 3, 4 }));
        byte[] cut = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Images", "Files", "gradient-24x16.jpg"));
        ArgumentException truncated = Assert.Throws<ArgumentException>(() => PdfImage.FromBytes(cut.AsSpan(0, cut.Length / 2)));

        Assert.Equal("bytes", notAnImage.ParamName);
        Assert.Null(notAnImage.InnerException);
        Assert.Contains("JPEG or PNG", notAnImage.Message);
        Assert.Equal("bytes", truncated.ParamName);
    }

    [Fact]
    public void An_image_block_validates_and_compares_by_picture()
    {
        PdfImage picture = Jpeg();
        Assert.Throws<ArgumentNullException>(() => new Image(null!));
        Assert.Equal("Width", Assert.Throws<ArgumentOutOfRangeException>(() => new Image(picture) { Width = Length.Zero }).ParamName);
        Assert.Equal("Height", Assert.Throws<ArgumentOutOfRangeException>(() => new Image(picture) { Height = Length.Pt(-1) }).ParamName);
        Assert.Equal("Alignment", Assert.Throws<ArgumentOutOfRangeException>(() => new Image(picture) { Alignment = (TextAlignment)8 }).ParamName);
        Assert.Equal(new Image(picture) { AltText = "x" }, new Image(picture) { AltText = "x" });
        Assert.NotEqual(new Image(picture), new Image(Jpeg()));
        Assert.Equal("Logo", new Image(picture) { AltText = "Logo" }.AltText);
    }

    [Fact]
    public void Everything_in_this_slice_gives_a_file_a_reader_accepts()
    {
        PdfImage png = PngWithAlpha();
        byte[] pdf = Save(
            Small(),
            new Heading(1, "Report"),
            new Callout([new Columns([P("a"), P("b"), P("c")]), P("note")]) { CornerRadius = Length.Pt(4) },
            Chart(c => c.DrawText("chart", 5, 20)),
            new Image(png),
            new Callout(Lines(20)));

        DocumentTestKit.AssertValid(pdf, "layout-blocks");
        Assert.True(DocumentTestKit.PageTexts(pdf).Length >= 2);
    }
}
