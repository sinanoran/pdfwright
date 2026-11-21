using UglyToad.PdfPig.Content;

namespace Pdfwright.Tests.Document;

/// <summary>The list block: markers, the hanging indent, direction, nesting, pagination, extraction, validation and equality.</summary>
public class ItemListTests
{
    // A page of 300 by 200 points with a margin of 10: a content area of 280 by 180.
    private static PdfDocumentOptions Small(Action<DocumentTestKit.OptionsBuilder>? change = null) =>
        DocumentTestKit.Options(b =>
        {
            b.PageSize = new PageSize(Length.Pt(300), Length.Pt(200));
            b.Margins = Margins.Uniform(Length.Pt(10));
            change?.Invoke(b);
        });

    private static (byte[] Pdf, PdfDocument Document) Save(PdfDocumentOptions options, params Block[] blocks)
    {
        PdfDocument document = new(options);
        foreach (Block block in blocks)
        {
            document.Add(block);
        }

        return (DocumentTestKit.Save(document), document);
    }

    private static UglyToad.PdfPig.PdfDocument Open(byte[] pdf) => UglyToad.PdfPig.PdfDocument.Open(pdf);

    private static Word Find(Page page, string text) => page.GetWords().FirstOrDefault(w => w.Text == text) ?? throw new InvalidOperationException($"No word {text}; the words are: " + string.Join(" | ", page.GetWords().Select(w => w.Text)));

    private static string Joined(string[] texts) => DocumentTestKit.Strip(string.Concat(texts));

    private const string ArabicPhrase = "مرحبا بالعالم";

    private const string HebrewPhrase = "שלום עולם";

    // --- markers and the hanging indent ---------------------------------------------------------------------------------------

    [Fact]
    public void A_left_to_right_item_has_its_marker_at_the_indent_and_its_text_after_the_marker_gap()
    {
        (byte[] pdf, _) = Save(Small(), new ItemList(["alpha", "beta"]) { Marker = ListMarker.Decimal, Indent = Length.Pt(20), MarkerGap = Length.Pt(8) });

        using UglyToad.PdfPig.PdfDocument read = Open(pdf);
        Page page = read.GetPage(1);
        Word one = Find(page, "1.");
        Word alpha = Find(page, "alpha");
        Word two = Find(page, "2.");
        Word beta = Find(page, "beta");
        Assert.Equal(10 + 20, one.BoundingBox.Left, 0);
        Assert.Equal(one.BoundingBox.Left, two.BoundingBox.Left, 3);
        Assert.InRange(alpha.BoundingBox.Left - one.BoundingBox.Right, 7, 9.5);
        Assert.Equal(alpha.BoundingBox.Left, beta.BoundingBox.Left, 3);
        Assert.True(beta.BoundingBox.Top < alpha.BoundingBox.Top, "the second item is below the first (PDF y is up)");
        DocumentTestKit.AssertValid(pdf, "list-ltr");
    }

    [Fact]
    public void The_text_of_every_item_starts_at_the_same_place_whatever_the_width_of_its_marker()
    {
        (byte[] pdf, _) = Save(Small(), new ItemList(Enumerable.Range(1, 10).Select(i => $"item{i}")) { Marker = ListMarker.Decimal });

        using UglyToad.PdfPig.PdfDocument read = Open(pdf);
        Page page = read.GetPage(1);
        double[] lefts = [.. Enumerable.Range(1, 10).Select(i => Find(page, $"item{i}").BoundingBox.Left)];
        Assert.All(lefts, left => Assert.Equal(lefts[0], left, 2));
        Word ten = Find(page, "10.");
        Assert.True(ten.BoundingBox.Right < lefts[9], "the widest marker ends before the text");
        DocumentTestKit.AssertValid(pdf, "list-column");
    }

    [Fact]
    public void The_wrapped_lines_of_an_item_are_aligned_with_its_text_and_not_with_its_marker()
    {
        string text = DocumentTestKit.LongText(30);
        (byte[] pdf, _) = Save(Small(), new ItemList([text, "second"]));

        using UglyToad.PdfPig.PdfDocument read = Open(pdf);
        Page page = read.GetPage(1);
        Word bullet = page.GetWords().First(w => w.Text.Length == 1 && w.Text[0] == (char)0x2022);
        Word first = Find(page, "word0");
        List<Word> words = [.. page.GetWords().Where(w => w.Text.StartsWith("word", StringComparison.Ordinal))];
        double[] tops = [.. words.Select(w => Math.Round(w.BoundingBox.Top, 0)).Distinct()];
        Assert.True(tops.Length >= 3, $"{tops.Length} lines");
        foreach (double top in tops)
        {
            double left = words.Where(w => Math.Round(w.BoundingBox.Top, 0) == top).Min(w => w.BoundingBox.Left);
            Assert.Equal(first.BoundingBox.Left, left, 1);
        }

        Assert.True(first.BoundingBox.Left > bullet.BoundingBox.Right + 4);
        Assert.Equal(10 + 12, bullet.BoundingBox.Left, 0);
        DocumentTestKit.AssertValid(pdf, "list-hanging");
    }

    [Fact]
    public void A_nested_list_is_indented_from_the_text_of_its_item()
    {
        ItemList nested = new(["inner"]) { Marker = ListMarker.LowerAlpha, Indent = Length.Pt(10) };
        ItemList outer = new([new ListItem([new Paragraph("outer"), nested]), new ListItem("last")]) { Marker = ListMarker.Decimal };
        (byte[] pdf, _) = Save(Small(), outer);

        using UglyToad.PdfPig.PdfDocument read = Open(pdf);
        Page page = read.GetPage(1);
        Word outerText = Find(page, "outer");
        Word a = Find(page, "a.");
        Word inner = Find(page, "inner");
        Assert.Equal(outerText.BoundingBox.Left + 10, a.BoundingBox.Left, 0);
        Assert.True(inner.BoundingBox.Left > a.BoundingBox.Right);
        Assert.True(inner.BoundingBox.Top < outerText.BoundingBox.Top, "the nested item is below its parent (PDF y is up)");
        Assert.True(Find(page, "last").BoundingBox.Top < inner.BoundingBox.Top);
        Assert.Equal(outerText.BoundingBox.Left, Find(page, "last").BoundingBox.Left, 2);
        DocumentTestKit.AssertValid(pdf, "list-nested");
    }

    [Fact]
    public void Start_sets_the_first_number_and_the_markers_count_in_their_kind()
    {
        (byte[] pdf, _) = Save(
            Small(b => b.Language = "en"),
            new ItemList(["a", "b", "c"]) { Marker = ListMarker.Decimal, Start = 9 },
            new ItemList(["a", "b"]) { Marker = ListMarker.LowerAlpha, Start = 26 },
            new ItemList(["a", "b"]) { Marker = ListMarker.UpperAlpha, Start = 52 });

        string text = DocumentTestKit.PageTexts(pdf)[0];
        Assert.Contains("9.", text, StringComparison.Ordinal);
        Assert.Contains("10.", text, StringComparison.Ordinal);
        Assert.Contains("11.", text, StringComparison.Ordinal);
        Assert.Contains("z.", text, StringComparison.Ordinal);
        Assert.Contains("aa.", text, StringComparison.Ordinal);
        Assert.Contains("AZ.", text, StringComparison.Ordinal);
        Assert.Contains("BA.", text, StringComparison.Ordinal);
        Assert.Equal(DocumentTestKit.Strip("9.a10.b11.cz.aaa.bAZ.aBA.b"), DocumentTestKit.Strip(text));
        DocumentTestKit.AssertValid(pdf, "list-start");
    }

    [Fact]
    public void The_item_spacing_separates_two_items_and_the_space_after_the_list_separates_it_from_the_next_block()
    {
        (byte[] pdf, _) = Save(Small(), new ItemList(["one", "two"]) { ItemSpacing = Length.Pt(10), SpaceAfter = Length.Pt(30) }, new Paragraph("next"));

        using UglyToad.PdfPig.PdfDocument read = Open(pdf);
        Page page = read.GetPage(1);
        double pitch = Find(page, "one").BoundingBox.Bottom - Find(page, "two").BoundingBox.Bottom;
        double toNext = Find(page, "two").BoundingBox.Bottom - Find(page, "next").BoundingBox.Bottom;
        Assert.Equal(20, toNext - pitch, 0);
        DocumentTestKit.AssertValid(pdf, "list-spacing");
    }

    // --- direction ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void In_a_right_to_left_list_the_marker_is_at_the_right_edge_and_the_text_hangs_to_its_left()
    {
        string text = string.Join(' ', Enumerable.Repeat(ArabicPhrase, 8));
        (byte[] pdf, PdfDocument document) = Save(Small(), new ItemList([text, ArabicPhrase]) { Direction = TextDirection.RightToLeft, Marker = ListMarker.Decimal, Indent = Length.Pt(12), MarkerGap = Length.Pt(6) });

        using UglyToad.PdfPig.PdfDocument read = Open(pdf);
        Page page = read.GetPage(1);
        Assert.Empty(document.Diagnostics.MissingGlyphs);
        Word one = Find(page, ".1");
        Word two = Find(page, ".2");

        // The marker "1." reads, in a right-to-left paragraph, with the digit at the right and the full stop at its left: the glyph of the digit ends at the indent from the right edge.
        Assert.Equal(10 + 280 - 12, one.BoundingBox.Right, 0);
        Assert.Equal(one.BoundingBox.Right, two.BoundingBox.Right, 2);
        double textRight = page.GetWords().Where(w => w.Text != ".1" && w.Text != ".2" && w.Text.Length > 1 && w.BoundingBox.Top > one.BoundingBox.Bottom).Max(w => w.BoundingBox.Right);
        Assert.True(textRight < one.BoundingBox.Left - 4, "the text ends left of the marker, with a gap");
        Assert.True(textRight >= one.BoundingBox.Left - 12, "and not further than the marker column and the gap");

        // The full stop is to the left of the digit.
        var letters = one.Letters.OrderBy(l => l.BoundingBox.Left).ToList();
        Assert.Equal("1", letters[^1].Value);
        Assert.Equal(".", letters[0].Value);
        DocumentTestKit.AssertValid(pdf, "list-rtl");
    }

    [Fact]
    public void The_wrapped_lines_of_a_right_to_left_item_end_where_the_first_line_ends()
    {
        string text = string.Join(' ', Enumerable.Repeat(ArabicPhrase, 20));
        (byte[] pdf, _) = Save(Small(), new ItemList([text]) { Direction = TextDirection.RightToLeft });

        using UglyToad.PdfPig.PdfDocument read = Open(pdf);
        Page page = read.GetPage(1);
        Word bullet = page.GetWords().First(w => w.Text == ((char)0x2022).ToString());
        Word[] words = [.. page.GetWords().Where(w => w.Text != bullet.Text && w.Text.Length > 1)];
        double[] tops = [.. words.Select(w => Math.Round(w.BoundingBox.Top, 0)).Distinct()];
        Assert.True(tops.Length >= 3, $"{tops.Length} lines");
        double[] rights = [.. tops.Select(top => words.Where(w => Math.Round(w.BoundingBox.Top, 0) == top).Max(w => w.BoundingBox.Right))];
        Assert.Equal(rights[0], rights[1], 1);
        Assert.True(rights[0] < bullet.BoundingBox.Left, "the text is left of the marker");
        Assert.Equal(10 + 280 - 12, bullet.BoundingBox.Right, 0);
        DocumentTestKit.AssertValid(pdf, "list-rtl-wrap");
    }

    [Fact]
    public void A_list_follows_the_direction_of_the_document_and_a_paragraph_may_decide_for_its_item()
    {
        (byte[] pdf, _) = Save(
            Small(b => b.Direction = TextDirection.RightToLeft),
            new ItemList([new ListItem(ArabicPhrase), new ListItem(new Paragraph("left") { Direction = TextDirection.LeftToRight })]) { Marker = ListMarker.Decimal });

        using UglyToad.PdfPig.PdfDocument read = Open(pdf);
        Page page = read.GetPage(1);
        Assert.Equal(10 + 280 - 12, Find(page, ".1").BoundingBox.Right, 0);
        Assert.Equal(10 + 12, Find(page, "2.").BoundingBox.Left, 0);
        Assert.True(Find(page, "left").BoundingBox.Left > Find(page, "2.").BoundingBox.Right);
        DocumentTestKit.AssertValid(pdf, "list-per-item");
    }

    [Fact]
    public void An_automatic_direction_lets_each_items_text_decide_the_side_of_its_marker()
    {
        (byte[] pdf, _) = Save(Small(), new ItemList([new ListItem(HebrewPhrase), new ListItem("latin")]) { Direction = TextDirection.Auto, Marker = ListMarker.Decimal });

        using UglyToad.PdfPig.PdfDocument read = Open(pdf);
        Page page = read.GetPage(1);
        Assert.Equal(10 + 280 - 12, Find(page, ".1").BoundingBox.Right, 0);
        Assert.Equal(10 + 12, Find(page, "2.").BoundingBox.Left, 0);
        DocumentTestKit.AssertValid(pdf, "list-auto");
    }

    [Fact]
    public void Arabic_indic_digits_are_the_markers_of_an_arabic_list()
    {
        (byte[] pdf, PdfDocument document) = Save(Small(), new ItemList([ArabicPhrase, ArabicPhrase]) { Direction = TextDirection.RightToLeft, Marker = ListMarker.ArabicIndicDecimal, Start = 3 });

        Assert.Empty(document.Diagnostics.MissingGlyphs);
        string text = DocumentTestKit.PageTexts(pdf)[0];
        Assert.Contains(((char)0x0663).ToString(), text, StringComparison.Ordinal);
        Assert.Contains(((char)0x0664).ToString(), text, StringComparison.Ordinal);
        DocumentTestKit.AssertValid(pdf, "list-arabic-indic");
    }

    // --- pagination -----------------------------------------------------------------------------------------------------------

    [Fact]
    public void Items_are_split_between_pages_by_their_lines_and_a_marker_never_stands_without_its_first_line()
    {
        string[] items = [.. Enumerable.Range(0, 24).Select(i => DocumentTestKit.LongText(24, $"i{i}w"))];
        (byte[] pdf, PdfDocument document) = Save(Small(), new ItemList(items) { Marker = ListMarker.Decimal });

        using UglyToad.PdfPig.PdfDocument read = Open(pdf);
        Page[] pages = DocumentTestKit.Pages(read);
        Assert.True(pages.Length >= 3, $"{pages.Length} pages");
        Assert.Equal(pages.Length, document.Diagnostics.PageCount);
        bool split = false;
        foreach (Page page in pages)
        {
            Word[] words = [.. page.GetWords()];
            foreach (Word marker in words.Where(w => w.Text.EndsWith('.') && w.Text[..^1].All(char.IsDigit)))
            {
                Assert.Contains(words, w => w != marker && !w.Text.EndsWith('.') && Math.Abs(w.BoundingBox.Bottom - marker.BoundingBox.Bottom) < 1.0);
            }

            // A page that starts with a line of text and not with a marker starts inside an item.
            double topLine = words.Max(w => w.BoundingBox.Top);
            split |= !words.Any(w => Math.Abs(w.BoundingBox.Top - topLine) < 1.0 && w.Text.EndsWith('.') && w.Text[..^1].All(char.IsDigit));
        }

        Assert.True(split, "some item went on at the top of the next page");
        string all = Joined(DocumentTestKit.PageTexts(pdf));
        Assert.Equal(Joined([.. items.Select((t, i) => $"{i + 1}.{t}")]), all);
        DocumentTestKit.AssertValid(pdf, "list-pages");
    }

    [Fact]
    public void A_heading_before_a_list_stays_with_its_first_line()
    {
        (byte[] pdf, _) = Save(Small(), new Spacer(Length.Pt(150)), new Heading(2, "Title"), new ItemList(["first item", "second"]));

        string[] pages = DocumentTestKit.PageTexts(pdf);
        Assert.Equal(2, pages.Length);
        Assert.Contains("Title", pages[1], StringComparison.Ordinal);
        DocumentTestKit.AssertValid(pdf, "list-heading");
    }

    // --- places ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_list_in_a_callout_and_in_a_column_cell_is_laid_out_inside_them()
    {
        (byte[] pdf, _) = Save(
            Small(),
            new Callout(new ItemList(["boxed one", "boxed two"])) { Padding = Margins.Pt(5, 20, 5, 20), Border = null },
            new Columns([new ItemList(["cell one"]) { Marker = ListMarker.Decimal }, new Paragraph("beside")]) { Gap = Length.Pt(20) });

        using UglyToad.PdfPig.PdfDocument read = Open(pdf);
        Page page = read.GetPage(1);
        Word boxedBullet = page.GetWords().First(w => w.Text == ((char)0x2022).ToString());
        Assert.Equal(10 + 20 + 12, boxedBullet.BoundingBox.Left, 0);
        Word cellMarker = Find(page, "1.");
        Assert.Equal(10 + 12, cellMarker.BoundingBox.Left, 0);
        Assert.True(Find(page, "beside").BoundingBox.Left > 140);
        DocumentTestKit.AssertValid(pdf, "list-in-boxes");
    }

    [Fact]
    public void A_callout_that_holds_a_long_list_splits_between_its_items_and_lines()
    {
        string[] items = [.. Enumerable.Range(0, 12).Select(i => DocumentTestKit.LongText(20, $"c{i}w"))];
        (byte[] pdf, _) = Save(Small(), new Callout(new ItemList(items) { Marker = ListMarker.Decimal }));

        string[] pages = DocumentTestKit.PageTexts(pdf);
        Assert.True(pages.Length >= 2);
        Assert.Equal(Joined([.. items.Select((t, i) => $"{i + 1}.{t}")]), Joined(pages));
        DocumentTestKit.AssertValid(pdf, "list-callout-split");
    }

    [Fact]
    public void A_list_cannot_be_in_a_header()
    {
        PdfDocument document = new(Small(b => b.Header = _ => new ItemList(["x"])));
        document.Add(new Paragraph("body"));

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => DocumentTestKit.Save(document));
        Assert.Contains("header", error.Message, StringComparison.Ordinal);
    }

    // --- diagnostics and extraction -------------------------------------------------------------------------------------------

    [Fact]
    public void A_marker_no_font_can_draw_is_reported_with_the_page_it_is_on()
    {
        FontCollection latinOnly = DocumentTestKit.Collection("NotoSans-Regular.ttf");
        string[] items = [.. Enumerable.Range(0, 30).Select(i => $"entry {i}")];
        (_, PdfDocument document) = Save(Small(b => b.Fonts = latinOnly), new ItemList(items) { Marker = ListMarker.ArabicIndicDecimal });

        Assert.True(document.Diagnostics.PageCount >= 2);
        Assert.Contains(document.Diagnostics.MissingGlyphs, m => m.CodePoint == 0x0661 && m.Page == 1);
        Assert.Contains(document.Diagnostics.MissingGlyphs, m => m.Page == document.Diagnostics.PageCount);
    }

    [Fact]
    public void A_missing_glyph_in_an_item_is_reported_with_the_page_of_the_item()
    {
        FontCollection latinOnly = DocumentTestKit.Collection("NotoSans-Regular.ttf");
        (_, PdfDocument document) = Save(Small(b => b.Fonts = latinOnly), new Spacer(Length.Pt(170)), new ItemList(["one", "日本"]));

        Assert.Contains(document.Diagnostics.MissingGlyphs, m => m.CodePoint == 0x65E5 && m.Page == 2);
    }

    [Theory]
    [InlineData("latin", "First item", "Second item")]
    [InlineData("arabic", "مرحبا بالعالم", "هذا هو البند الثاني")]
    [InlineData("hebrew", "שלום עולם", "זה הפריט השני")]
    public void A_list_reads_back_with_each_marker_before_its_item_in_logical_order(string script, string first, string second)
    {
        (byte[] pdf, PdfDocument document) = Save(
            DocumentTestKit.Options(b => b.Language = script switch { "arabic" => "ar", "hebrew" => "he", _ => "en" }),
            new ItemList([first, second]) { Marker = ListMarker.Decimal, Direction = script == "latin" ? TextDirection.LeftToRight : TextDirection.RightToLeft });

        Assert.Empty(document.Diagnostics.MissingGlyphs);
        Assert.Equal(DocumentTestKit.Strip($"1.{first}2.{second}"), DocumentTestKit.Strip(DocumentTestKit.PageTexts(pdf)[0]));
        DocumentTestKit.AssertValid(pdf, "list-" + script);
    }

    [Fact]
    public void A_bullet_list_reads_back_with_the_bullet_before_each_item()
    {
        (byte[] pdf, _) = Save(DocumentTestKit.Options(), new ItemList(["alpha", "beta"]));

        string bullet = ((char)0x2022).ToString();
        Assert.Equal(bullet + "alpha" + bullet + "beta", DocumentTestKit.Strip(DocumentTestKit.PageTexts(pdf)[0]));
    }

    // --- validation and equality ----------------------------------------------------------------------------------------------

    [Fact]
    public void A_list_is_checked_where_it_is_made()
    {
        _ = Assert.Throws<ArgumentNullException>(() => new ItemList((IEnumerable<ListItem>)null!));
        _ = Assert.Throws<ArgumentNullException>(() => new ItemList((IEnumerable<string>)null!));
        _ = Assert.Throws<ArgumentException>(() => new ItemList(Array.Empty<ListItem>()));
        _ = Assert.Throws<ArgumentException>(() => new ItemList(new ListItem[] { null! }));
        _ = Assert.Throws<ArgumentException>(() => new ItemList(new string[] { null! }));
        _ = Assert.Throws<ArgumentNullException>(() => new ListItem((string)null!));
        _ = Assert.Throws<ArgumentNullException>(() => new ListItem((Block)null!));
        _ = Assert.Throws<ArgumentException>(() => new ListItem(Array.Empty<Block>()));
        _ = Assert.Throws<ArgumentException>(() => new ListItem(new Block[] { new Paragraph("a"), null! }));
        _ = Assert.Throws<ArgumentException>(() => new ListItem(new Heading(1, "no")));
        _ = Assert.Throws<ArgumentException>(() => new ListItem(new Block[] { new Paragraph("a"), new Spacer(Length.Pt(1)) }));
        _ = Assert.Throws<ArgumentException>(() => new ListItem(new Block[] { new ItemList(["a"]), new Paragraph("b") }));

        ArgumentOutOfRangeException marker = Assert.Throws<ArgumentOutOfRangeException>(() => new ItemList(["a"]) { Marker = (ListMarker)99 });
        Assert.Equal("Marker", marker.ParamName);
        Assert.Equal("Start", Assert.Throws<ArgumentOutOfRangeException>(() => new ItemList(["a"]) { Start = 0 }).ParamName);
        Assert.Equal("Start", Assert.Throws<ArgumentOutOfRangeException>(() => new ItemList(["a"]) { Start = 1_000_001 }).ParamName);
        Assert.Equal("Indent", Assert.Throws<ArgumentOutOfRangeException>(() => new ItemList(["a"]) { Indent = Length.Pt(-1) }).ParamName);
        Assert.Equal("MarkerGap", Assert.Throws<ArgumentOutOfRangeException>(() => new ItemList(["a"]) { MarkerGap = Length.Pt(-2) }).ParamName);
        Assert.Equal("ItemSpacing", Assert.Throws<ArgumentOutOfRangeException>(() => new ItemList(["a"]) { ItemSpacing = Length.Pt(-1) }).ParamName);
        Assert.Equal("Direction", Assert.Throws<ArgumentOutOfRangeException>(() => new ItemList(["a"]) { Direction = (TextDirection)9 }).ParamName);
        Assert.Equal("SpaceBefore", Assert.Throws<ArgumentOutOfRangeException>(() => new ItemList(["a"]) { SpaceBefore = Length.Pt(-1) }).ParamName);
        Assert.Equal("SpaceAfter", Assert.Throws<ArgumentOutOfRangeException>(() => new ItemList(["a"]) { SpaceAfter = Length.Pt(-1) }).ParamName);
    }

    [Fact]
    public void Lists_nest_one_level_deep_and_deeper_is_refused()
    {
        ItemList level3 = new(["c"]);
        ItemList level2 = new([new ListItem([new Paragraph("b"), level3])]);
        ItemList level1 = new([new ListItem([new Paragraph("a"), new ItemList(["b"])])]);
        Assert.Equal(2, level1.Depth);
        Assert.Equal(2, level2.Depth);

        ArgumentException deeper = Assert.Throws<ArgumentException>(() => new ListItem([new Paragraph("a"), level2]));
        Assert.Contains("one level", deeper.Message, StringComparison.Ordinal);
        _ = Assert.Throws<ArgumentException>(() => new ItemList([new ListItem([new Paragraph("a"), new ItemList([new ListItem([new Paragraph("x"), new ItemList(["y"])])])])]));
    }

    [Fact]
    public void Two_lists_are_equal_when_their_items_and_settings_are()
    {
        static ItemList Make(string second = "two", ListMarker marker = ListMarker.Decimal) => new(["one", second]) { Marker = marker, Start = 3, Indent = Length.Pt(5), Style = new TextStyle { Weight = 700 }, Direction = TextDirection.RightToLeft };

        Assert.Equal(Make(), Make());
        Assert.Equal(Make().GetHashCode(), Make().GetHashCode());
        Assert.NotEqual(Make(), Make("other"));
        Assert.NotEqual(Make(), Make(marker: ListMarker.LowerAlpha));
        Assert.NotEqual(Make(), Make() with { Start = 4 });
        Assert.NotEqual(Make(), Make() with { MarkerGap = Length.Pt(1) });
        Assert.NotEqual(Make(), Make() with { ItemSpacing = Length.Pt(1) });
        Assert.NotEqual(Make(), Make() with { MarkerStyle = new TextStyle { Weight = 300 } });
        Assert.Equal(new ListItem("x"), new ListItem(new Paragraph("x")));
        Assert.Equal(new ListItem("x").GetHashCode(), new ListItem(new Paragraph("x")).GetHashCode());
        Assert.NotEqual(new ListItem("x"), new ListItem("y"));
        Assert.Equal(new ItemList([new ListItem([new Paragraph("a"), new ItemList(["b"])])]), new ItemList([new ListItem([new Paragraph("a"), new ItemList(["b"])])]));
        Assert.Equal(["one", "two"], Make().Items.Select(i => ((Paragraph)i.Content[0]).Runs[0].Text));
    }

    [Fact]
    public void A_list_whose_indent_leaves_no_room_for_text_fails_the_save_and_says_why()
    {
        PdfDocument document = new(Small());
        document.Add(new ItemList(["x"]) { Indent = Length.Pt(279) });

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => DocumentTestKit.Save(document));
        Assert.Contains("indent", error.Message, StringComparison.Ordinal);
    }
}
