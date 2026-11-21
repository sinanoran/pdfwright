using System.Text;
using Pdfwright.Document;
using UglyToad.PdfPig.Content;

namespace Pdfwright.Tests.Document;

/// <summary>The table block: column widths, pagination, streamed rows, direction, borders and fills, extraction, limits and validation.</summary>
public class TableTests
{
    private static readonly TableColumn[] Two = [new(ColumnWidth.Fixed(Length.Pt(100))), new(ColumnWidth.Fixed(Length.Pt(200)))];

    private static IEnumerable<TableRow> Rows(int count, Func<int, string[]>? cells = null) =>
        Enumerable.Range(0, count).Select(i => new TableRow(cells is null ? [$"a{i}", $"b{i}"] : cells(i)));

    private static PdfDocument Document(Table table, Action<DocumentTestKit.OptionsBuilder>? change = null)
    {
        PdfDocument document = new(DocumentTestKit.Options(change));
        document.Add(table);
        return document;
    }

    private static double Left(Page page, string word) => page.GetWords().First(w => w.Text == word).BoundingBox.Left;

    private static double Top(Page page, string word) => page.GetWords().First(w => w.Text == word).BoundingBox.Top;

    // --- widths ---------------------------------------------------------------------------------------------------------------

    private static double[] Widths(double available, double[] autoMin, double[] autoMax, params TableColumn[] columns) =>
        DocumentRenderer.ColumnWidths(columns, available, autoMin, autoMax, 10);

    [Fact]
    public void Fixed_columns_take_their_length_and_a_table_with_no_fraction_is_as_wide_as_its_columns()
    {
        double[] widths = Widths(500, [0, 0], [0, 0], Two);

        Assert.Equal([100, 200], widths);
    }

    [Fact]
    public void Fraction_columns_share_what_the_fixed_columns_leave_by_weight()
    {
        double[] widths = Widths(400, [0, 0, 0], [0, 0, 0], new TableColumn(ColumnWidth.Fixed(Length.Pt(100))), new TableColumn(ColumnWidth.Fraction(1)), new TableColumn(ColumnWidth.Fraction(2)));

        // The fractions' own minimum is the padding (10), which comes first; the rest, 400 - 100 - 20 = 280, is shared 1:2.
        Assert.Equal(100, widths[0], 6);
        Assert.Equal(10 + (280 / 3.0), widths[1], 6);
        Assert.Equal(10 + (280 * 2 / 3.0), widths[2], 6);
        Assert.Equal(400, widths.Sum(), 6);
    }

    [Fact]
    public void Auto_columns_take_what_they_want_when_there_is_room_and_fractions_share_the_rest()
    {
        double[] widths = Widths(400, [30, 0, 0], [80, 0, 0], new TableColumn(), new TableColumn(ColumnWidth.Fraction(1)), new TableColumn(ColumnWidth.Fraction(1)));

        Assert.Equal(80, widths[0], 6);
        Assert.Equal(widths[1], widths[2], 6);
        Assert.Equal(400, widths.Sum(), 6);
    }

    [Fact]
    public void Auto_columns_share_the_room_by_how_much_each_wants_when_there_is_not_enough()
    {
        double[] widths = Widths(160, [20, 20], [220, 120], new TableColumn(), new TableColumn());

        // Minimums 40, room 120 to share 200:100 (the growth each wants).
        Assert.Equal(20 + 80, widths[0], 6);
        Assert.Equal(20 + 40, widths[1], 6);
    }

    [Fact]
    public void An_auto_column_that_wants_less_than_the_room_leaves_the_rest_unused_without_fractions()
    {
        double[] widths = Widths(500, [20, 20], [60, 90], new TableColumn(), new TableColumn());

        Assert.Equal([60, 90], widths);
    }

    [Fact]
    public void When_the_minimums_do_not_fit_every_column_shrinks_in_the_same_proportion()
    {
        double[] widths = Widths(150, [0, 100], [0, 0], new TableColumn(ColumnWidth.Fixed(Length.Pt(200))), new TableColumn());

        Assert.Equal(150, widths.Sum(), 6);
        Assert.Equal(200 * 150 / 300.0, widths[0], 6);
        Assert.Equal(100 * 150 / 300.0, widths[1], 6);
    }

    [Fact]
    public void Fixed_columns_put_the_cells_where_the_widths_say()
    {
        PdfDocument document = Document(new Table(Two, new TableRow("Left", "Right"), Rows(3)) { CellPadding = Margins.Pt(2, 7, 2, 7) });

        byte[] pdf = DocumentTestKit.Save(document);

        using UglyToad.PdfPig.PdfDocument read = UglyToad.PdfPig.PdfDocument.Open(pdf);
        Page page = read.GetPage(1);
        double margin = 15 * 72 / 25.4;
        Assert.Equal(margin + 7, Left(page, "Left"), 0.5);
        Assert.Equal(margin + 100 + 7, Left(page, "Right"), 0.5);
        Assert.Equal(margin + 100 + 7, Left(page, "b2"), 0.5);
    }

    [Fact]
    public void Auto_columns_are_measured_from_the_header_and_the_sample_and_wrap_below_their_minimum()
    {
        TableColumn[] columns = [new(), new(ColumnWidth.Fraction(1))];
        PdfDocument document = Document(new Table(columns, new TableRow("Identifier of the thing", "Note"), [new TableRow("x1", "y1"), new TableRow("a much longer identifier", "y2")]));

        byte[] pdf = DocumentTestKit.Save(document);

        using UglyToad.PdfPig.PdfDocument read = UglyToad.PdfPig.PdfDocument.Open(pdf);
        Page page = read.GetPage(1);
        double margin = 15 * 72 / 25.4;
        double second = Left(page, "Note");
        // The first column is as wide as its longest line ("Identifier of the thing", bold) plus the padding; the note column starts after it.
        Assert.InRange(second - margin, 100, 200);
        Assert.Equal(second, Left(page, "y1"), 0.5);
        // The header and both rows are one line each: they are in a row of their own and not wrapped.
        Assert.Equal(3, page.GetWords().Count(w => w.Text is "x1" or "Note" or "y2"));
    }

    [Fact]
    public void A_table_wider_than_the_page_shrinks_and_a_long_word_breaks_instead_of_overflowing()
    {
        TableColumn[] columns = [new(ColumnWidth.Fixed(Length.Pt(400))), new(ColumnWidth.Fixed(Length.Pt(400)))];
        string word = new('W', 60);
        PdfDocument document = Document(new Table(columns, [new TableRow(word, "ok")]));

        byte[] pdf = DocumentTestKit.Save(document);

        using UglyToad.PdfPig.PdfDocument read = UglyToad.PdfPig.PdfDocument.Open(pdf);
        Page page = read.GetPage(1);
        double contentRight = page.Width - (15 * 72 / 25.4);
        Assert.All(page.Letters, l => Assert.True(l.EndBaseLine.X <= contentRight + 0.5, $"{l.Value} at {l.EndBaseLine.X}"));
        Assert.Equal(word, DocumentTestKit.Strip(string.Concat(page.GetWords().Where(w => w.Text.StartsWith('W')).Select(w => w.Text))));
        Assert.True(page.GetWords().Count(w => w.Text.StartsWith('W')) > 1);
    }

    [Fact]
    public void A_cell_may_hold_several_paragraphs_and_an_alignment_of_its_own()
    {
        TableColumn[] columns = [new(ColumnWidth.Fixed(Length.Pt(200))) { Alignment = TextAlignment.Start }];
        TableRow row = new([new TableCell([new Paragraph("first"), new Paragraph("second")]) { Alignment = TextAlignment.End }]);
        PdfDocument document = Document(new Table(columns, [row]) { CellPadding = Margins.Uniform(Length.Pt(4)) });

        byte[] pdf = DocumentTestKit.Save(document);

        using UglyToad.PdfPig.PdfDocument read = UglyToad.PdfPig.PdfDocument.Open(pdf);
        Page page = read.GetPage(1);
        double margin = 15 * 72 / 25.4;
        Assert.True(Top(page, "first") > Top(page, "second"));
        Assert.Equal(margin + 200 - 4, page.GetWords().First(w => w.Text == "first").BoundingBox.Right, 1.0);
    }

    // --- pagination -------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_table_runs_over_several_pages_with_the_header_repeated_on_each()
    {
        PdfDocument document = Document(new Table(Two, new TableRow("HeadA", "HeadB"), Rows(200)));

        byte[] pdf = DocumentTestKit.Save(document);

        using UglyToad.PdfPig.PdfDocument read = UglyToad.PdfPig.PdfDocument.Open(pdf);
        Page[] pages = DocumentTestKit.Pages(read);
        Assert.True(pages.Length >= 3, $"{pages.Length} pages");
        Assert.All(pages, p =>
        {
            Assert.Contains(p.GetWords(), w => w.Text == "HeadA");
            Assert.Contains(p.GetWords(), w => w.Text == "HeadB");
            // The header is the topmost thing of the page.
            Assert.Equal(p.GetWords().Max(w => w.BoundingBox.Top), Top(p, "HeadA"), 0.5);
        });
        string[] texts = [.. pages.Select(p => p.Text)];
        string all = DocumentTestKit.Strip(string.Concat(texts));
        string expected = DocumentTestKit.Strip(string.Concat(Enumerable.Range(0, 200).Select(i => $"a{i}b{i}")));
        Assert.Equal(expected, DocumentTestKit.Strip(string.Concat(texts.Select(t => t.Replace("HeadA", "", StringComparison.Ordinal).Replace("HeadB", "", StringComparison.Ordinal)))));
        Assert.Equal(pages.Length, all.Split("HeadA").Length - 1);
        Assert.Equal(pages.Length, document.Diagnostics.PageCount);
        DocumentTestKit.AssertValid(pdf, "table-pages");
    }

    [Fact]
    public void The_header_is_not_repeated_when_that_is_switched_off()
    {
        PdfDocument document = Document(new Table(Two, new TableRow("HeadA", "HeadB"), Rows(200)) { RepeatHeader = false });

        byte[] pdf = DocumentTestKit.Save(document);

        string[] texts = DocumentTestKit.PageTexts(pdf);
        Assert.True(texts.Length >= 3);
        Assert.Contains("HeadA", texts[0], StringComparison.Ordinal);
        Assert.All(texts.Skip(1), t => Assert.DoesNotContain("HeadA", t, StringComparison.Ordinal));
    }

    [Fact]
    public void A_row_is_never_split_between_pages_and_the_rows_on_a_page_are_whole()
    {
        // Each row is three lines tall; no page may end with a part of one.
        TableColumn[] columns = [new(ColumnWidth.Fixed(Length.Pt(60))), new(ColumnWidth.Fixed(Length.Pt(200)))];
        PdfDocument document = Document(new Table(columns, Rows(120, i => [$"r{i}", $"one{i} two{i}\nthree{i}\nfour{i}"])));

        byte[] pdf = DocumentTestKit.Save(document);

        using UglyToad.PdfPig.PdfDocument read = UglyToad.PdfPig.PdfDocument.Open(pdf);
        foreach (Page page in read.GetPages())
        {
            string[] words = [.. page.GetWords().Select(w => w.Text)];
            foreach (string w in words.Where(w => w.StartsWith("r", StringComparison.Ordinal)))
            {
                string n = w[1..];
                Assert.Contains($"one{n}", words);
                Assert.Contains($"four{n}", words);
            }
        }
    }

    [Fact]
    public void A_table_whose_header_does_not_fit_with_a_row_under_it_starts_on_the_next_page()
    {
        PdfDocument document = new(DocumentTestKit.Options(o => o.PageSize = PageSize.A4));
        double contentHeight = (297 - 40) * 72 / 25.4;
        document.Add(new Paragraph("before"));
        // Leave room for a header (about 20 points) but not for the header and a row.
        document.Add(new Spacer(Length.Pt(contentHeight - 14 - 8 - 30)));
        document.Add(new Table(Two, new TableRow("HeadA", "HeadB"), Rows(3)));

        byte[] pdf = DocumentTestKit.Save(document);

        string[] pages = DocumentTestKit.PageTexts(pdf);
        Assert.Equal(2, pages.Length);
        Assert.DoesNotContain("HeadA", pages[0], StringComparison.Ordinal);
        Assert.Contains("HeadA", pages[1], StringComparison.Ordinal);
        Assert.Contains("a0", pages[1], StringComparison.Ordinal);
    }

    [Fact]
    public void A_row_taller_than_a_page_is_split_between_lines_of_its_cells_the_same_way_in_every_cell()
    {
        // The first cell is a long run of lines, the second is short: the short one is drawn once, with the first piece, and the long one is complete.
        TableColumn[] columns = [new(ColumnWidth.Fixed(Length.Pt(80))), new(ColumnWidth.Fixed(Length.Pt(80)))];
        string tall = string.Join('\n', Enumerable.Range(0, 180).Select(i => $"line{i}"));
        string alsoTall = string.Join('\n', Enumerable.Range(0, 90).Select(i => $"other{i}"));
        PdfDocument document = Document(new Table(columns, new TableRow("HeadA", "HeadB"), [new TableRow("first", "x"), new TableRow(tall, alsoTall), new TableRow("last", "y")]));

        byte[] pdf = DocumentTestKit.Save(document);

        using UglyToad.PdfPig.PdfDocument read = UglyToad.PdfPig.PdfDocument.Open(pdf);
        Page[] pages = DocumentTestKit.Pages(read);
        Assert.True(pages.Length >= 3, $"{pages.Length} pages");
        string[] words = [.. pages.SelectMany(p => p.GetWords()).Select(w => w.Text).Where(w => w.StartsWith("line", StringComparison.Ordinal) || w.StartsWith("other", StringComparison.Ordinal))];
        Assert.Equal(270, words.Length);
        Assert.Equal(Enumerable.Range(0, 180).Select(i => $"line{i}"), words.Where(w => w.StartsWith("line", StringComparison.Ordinal)));
        Assert.Equal(Enumerable.Range(0, 90).Select(i => $"other{i}"), words.Where(w => w.StartsWith("other", StringComparison.Ordinal)));
        Assert.Contains(pages[^1].GetWords(), w => w.Text == "last");
        Assert.All(pages.Skip(1), p => Assert.Contains(p.GetWords(), w => w.Text == "HeadA"));

        // Cut at the same height in both cells: the last line of "other" on the first page of the row is level with a line of "line" on the same page.
        Page first = pages.First(p => p.GetWords().Any(w => w.Text == "line0"));
        double lastLeft = first.GetWords().Where(w => w.Text.StartsWith("line", StringComparison.Ordinal)).Min(w => w.BoundingBox.Bottom);
        double lastRight = first.GetWords().Where(w => w.Text.StartsWith("other", StringComparison.Ordinal)).Min(w => w.BoundingBox.Bottom);
        Assert.Equal(lastLeft, lastRight, 0.5);
        DocumentTestKit.AssertValid(pdf, "table-tall-row");
    }

    // --- streamed rows ----------------------------------------------------------------------------------------------------------

    private sealed class CountingRows(int count) : IEnumerable<TableRow>
    {
        internal int Enumerations { get; private set; }

        internal int Produced { get; private set; }

        public IEnumerator<TableRow> GetEnumerator()
        {
            Enumerations++;
            for (int i = 0; i < count; i++)
            {
                Produced++;
                yield return new TableRow($"a{i}", $"b{i}");
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Fact]
    public void The_rows_are_enumerated_once_for_each_save_and_not_before()
    {
        CountingRows rows = new(50);
        Table table = new(Two, rows);
        PdfDocument document = Document(table);

        Assert.Equal(0, rows.Enumerations);
        _ = DocumentTestKit.Save(document);
        Assert.Equal(1, rows.Enumerations);
        Assert.Equal(50, rows.Produced);
        _ = DocumentTestKit.Save(document);
        Assert.Equal(2, rows.Enumerations);
    }

    [Fact]
    public void Automatic_columns_still_enumerate_the_rows_once()
    {
        CountingRows rows = new(500);
        PdfDocument document = Document(new Table([new TableColumn(), new TableColumn(ColumnWidth.Fraction(1))], rows));

        byte[] pdf = DocumentTestKit.Save(document);

        Assert.Equal(1, rows.Enumerations);
        Assert.Equal(500, rows.Produced);
        string text = string.Concat(DocumentTestKit.PageTexts(pdf));
        Assert.Contains("a499", text, StringComparison.Ordinal);
        Assert.Contains("b0", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_hundred_thousand_lazy_rows_are_not_held_and_the_file_is_made_in_reasonable_time()
    {
        const int Count = 100_000;
        List<WeakReference<TableRow>> seen = [];
        int survivors = -1;
        IEnumerable<TableRow> Lazy()
        {
            for (int i = 0; i < Count; i++)
            {
                TableRow row = new($"{i}");
                if (i % 1000 == 0)
                {
                    seen.Add(new WeakReference<TableRow>(row));
                }

                if (i == Count - 1)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                    survivors = seen.Count(w => w.TryGetTarget(out _));
                }

                yield return row;
            }
        }

        PdfDocument document = new(DocumentTestKit.Options(o => o.Compress = true));
        document.Add(new Table([new TableColumn(ColumnWidth.Fixed(Length.Pt(60)))], Lazy()) { CellPadding = Margins.Pt(0, 2, 0, 2) });

        System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        using MemoryStream stream = new();
        document.Save(stream);
        clock.Stop();

        // Only the last rows (still being laid out when the check ran) can be alive: the rows from the start of the table are all gone.
        Assert.InRange(survivors, 0, 3);
        Assert.True(document.Diagnostics.PageCount > 1000, $"{document.Diagnostics.PageCount} pages");
        Assert.True(clock.Elapsed < TimeSpan.FromMinutes(2), $"{clock.Elapsed}");
    }

    [Fact]
    public void A_row_of_a_table_that_is_null_or_has_too_many_cells_stops_the_save_with_a_clear_error()
    {
        PdfDocument tooMany = Document(new Table(Two, [new TableRow("a", "b"), new TableRow("a", "b", "c")]));
        PdfDocument nullRow = Document(new Table(Two, new TableRow?[] { null }.Select(r => r!)));

        InvalidOperationException many = Assert.Throws<InvalidOperationException>(() => DocumentTestKit.Save(tooMany));
        Assert.Contains("3 cells", many.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => DocumentTestKit.Save(nullRow));
    }

    [Fact]
    public async Task A_table_is_saved_asynchronously_too_and_the_rows_are_enumerated_once()
    {
        CountingRows rows = new(300);
        PdfDocument document = Document(new Table(Two, new TableRow("HeadA", "HeadB"), rows));

        using MemoryStream stream = new();
        await document.SaveAsync(stream, TestContext.Current.CancellationToken);

        Assert.Equal(1, rows.Enumerations);
        Assert.Contains("a299", string.Concat(DocumentTestKit.PageTexts(stream.ToArray())), StringComparison.Ordinal);
    }

    // --- direction -------------------------------------------------------------------------------------------------------------

    [Fact]
    public void In_a_right_to_left_table_the_columns_run_from_the_right()
    {
        TableColumn[] columns = [new(ColumnWidth.Fixed(Length.Pt(100))), new(ColumnWidth.Fixed(Length.Pt(100))), new(ColumnWidth.Fixed(Length.Pt(100)))];
        PdfDocument document = Document(new Table(columns, new TableRow("one", "two", "three"), [new TableRow("uno", "dos", "tres")]) { Direction = TextDirection.RightToLeft });

        byte[] pdf = DocumentTestKit.Save(document);

        using UglyToad.PdfPig.PdfDocument read = UglyToad.PdfPig.PdfDocument.Open(pdf);
        Page page = read.GetPage(1);
        Assert.True(Left(page, "one") > Left(page, "two"));
        Assert.True(Left(page, "two") > Left(page, "three"));
        Assert.True(Left(page, "uno") > Left(page, "dos"));
        // The table sits at the right edge of the content area, and the first column's text ends at its right edge less the padding (5 points).
        double margin = 15 * 72 / 25.4;
        Assert.Equal(page.Width - margin - 5, page.GetWords().First(w => w.Text == "one").BoundingBox.Right, 1.0);
    }

    [Fact]
    public void A_document_direction_of_right_to_left_makes_its_tables_right_to_left()
    {
        TableColumn[] columns = [new(ColumnWidth.Fixed(Length.Pt(100))), new(ColumnWidth.Fixed(Length.Pt(100)))];
        PdfDocument rtl = Document(new Table(columns, [new TableRow("first", "second")]), o => o.Direction = TextDirection.RightToLeft);
        PdfDocument auto = Document(new Table(columns, [new TableRow("first", "second")]), o => o.Direction = TextDirection.Auto);

        using UglyToad.PdfPig.PdfDocument a = UglyToad.PdfPig.PdfDocument.Open(DocumentTestKit.Save(rtl));
        using UglyToad.PdfPig.PdfDocument b = UglyToad.PdfPig.PdfDocument.Open(DocumentTestKit.Save(auto));

        Assert.True(Left(a.GetPage(1), "first") > Left(a.GetPage(1), "second"));
        Assert.True(Left(b.GetPage(1), "first") < Left(b.GetPage(1), "second"));
    }

    [Fact]
    public void A_table_in_latin_and_arabic_over_several_pages_reads_back_in_logical_order_in_each_cell()
    {
        string arabic = Corpus("arabic-sentence");
        string mixed = Corpus("mixed-en-in-ar");
        string diacritics = Corpus("arabic-diacritics");
        string latin = Corpus("latin-de");
        TableColumn[] columns = [new(ColumnWidth.Fixed(Length.Pt(90))), new(ColumnWidth.Fraction(1)), new(ColumnWidth.Fraction(1))];
        List<string> expected = ["No", "Latin", "Arabic"];
        List<TableRow> rows = [];
        for (int i = 0; i < 120; i++)
        {
            string[] cells = [$"n{i}", i % 2 == 0 ? latin : mixed, i % 3 == 0 ? arabic : diacritics];
            rows.Add(new TableRow(cells.Select(c => new TableCell(new Paragraph(c) { Style = c.Any(ch => ch >= 0x600 && ch <= 0x6FF) ? new TextStyle { Language = "ar" } : null }))));
            expected.AddRange(cells);
        }

        foreach (bool rightToLeft in new[] { false, true })
        {
            PdfDocument document = Document(new Table(columns, new TableRow("No", "Latin", "Arabic"), rows) { RepeatHeader = false, Direction = rightToLeft ? TextDirection.RightToLeft : TextDirection.LeftToRight });

            byte[] pdf = DocumentTestKit.Save(document);

            string[] pages = DocumentTestKit.PageTexts(pdf);
            Assert.True(pages.Length >= 3, $"{pages.Length} pages");
            Assert.Empty(document.Diagnostics.MissingGlyphs);
            // The content stream is written row by row, cell by cell in logical order, so the text of the pages is the cells' text in the order they were given.
            Assert.Equal(DocumentTestKit.Strip(string.Concat(expected)), DocumentTestKit.Strip(string.Concat(pages)));
            DocumentTestKit.AssertValid(pdf, rightToLeft ? "table-arabic-rtl" : "table-arabic");
        }
    }

    private static string Corpus(string id) => CorpusTests.Load().Single(c => c.Id == id).Text;

    // --- borders and fills ----------------------------------------------------------------------------------------------------

    private static string Content(byte[] pdf) => Encoding.Latin1.GetString(pdf);

    private static int Count(string text, string part) => text.Split(part).Length - 1;

    [Fact]
    public void Borders_are_drawn_as_lines_of_the_kinds_asked_for()
    {
        TableColumn[] columns = [new(ColumnWidth.Fixed(Length.Pt(100))), new(ColumnWidth.Fixed(Length.Pt(100)))];
        Table table = new(columns, [new TableRow("a", "b"), new TableRow("c", "d"), new TableRow("e", "f")])
        {
            Borders = new Borders { InnerHorizontal = new BorderLine(Length.Pt(2), Color.Red) },
        };

        string content = Content(DocumentTestKit.Save(Document(table)));

        // Three rows, two lines between them: no outer line, no vertical line.
        Assert.Equal(2, Count(content, "\n2 w\n"));
        Assert.Equal(2, Count(content, "1 0 0 RG"));
    }

    [Fact]
    public void Borders_all_draws_the_box_the_rows_and_the_columns()
    {
        TableColumn[] columns = [new(ColumnWidth.Fixed(Length.Pt(100))), new(ColumnWidth.Fixed(Length.Pt(100)))];
        Table table = new(columns, [new TableRow("a", "b"), new TableRow("c", "d")]) { Borders = Borders.All };

        string content = Content(DocumentTestKit.Save(Document(table)));

        // Horizontal: top, between, bottom (3); vertical for each of the two rows: left, between, right (6).
        Assert.Equal(9, Count(content, "0.5 w"));
    }

    [Fact]
    public void Border_lines_sit_on_the_edges_of_the_cells_around_the_text()
    {
        TableColumn[] columns = [new(ColumnWidth.Fixed(Length.Pt(100))), new(ColumnWidth.Fixed(Length.Pt(100)))];
        Table table = new(columns, [new TableRow("top", "b"), new TableRow("bottom", "d")]) { Borders = Borders.All };

        using UglyToad.PdfPig.PdfDocument read = UglyToad.PdfPig.PdfDocument.Open(DocumentTestKit.Save(Document(table)));

        Page page = read.GetPage(1);
        double margin = 15 * 72 / 25.4;
        var lines = page.Paths.Where(p => p.IsStroked).Select(p => p.GetBoundingRectangle()!.Value).ToList();
        double[] horizontals = [.. lines.Where(r => r.Height < 0.01).Select(r => r.Bottom).Order()];
        double[] verticals = [.. lines.Where(r => r.Width < 0.01).Select(r => r.Left).Distinct().Order()];
        Assert.Equal(3, horizontals.Length);
        Assert.Equal(margin, verticals[0], 0.01);
        Assert.Equal(margin + 100, verticals[1], 0.01);
        Assert.Equal(margin + 200, verticals[2], 0.01);
        // The lines are the top of the first row, the line between the rows and the bottom of the second: the words are between them.
        Assert.True(horizontals[2] > Top(page, "top") && Top(page, "top") > horizontals[1]);
        Assert.True(horizontals[1] > Top(page, "bottom") && Top(page, "bottom") > horizontals[0]);
    }

    [Fact]
    public void Borders_none_and_horizontal_and_outer_differ()
    {
        TableColumn[] columns = [new(ColumnWidth.Fixed(Length.Pt(100))), new(ColumnWidth.Fixed(Length.Pt(100)))];
        TableRow[] rows = [new("a", "b"), new("c", "d")];

        int none = Count(Content(DocumentTestKit.Save(Document(new Table(columns, rows) { Borders = Borders.None }))), "0.5 w");
        int horizontal = Count(Content(DocumentTestKit.Save(Document(new Table(columns, rows) { Borders = Borders.Horizontal }))), "0.5 w");
        int outer = Count(Content(DocumentTestKit.Save(Document(new Table(columns, rows) { Borders = Borders.Outer }))), "0.5 w");

        Assert.Equal(0, none);
        Assert.Equal(3, horizontal);
        Assert.Equal(2 + 4, outer);
    }

    [Fact]
    public void Each_page_of_a_table_is_closed_with_the_bottom_line_and_opened_with_the_top_line()
    {
        Table table = new(Two, new TableRow("HeadA", "HeadB"), Rows(200)) { Borders = new Borders { OuterHorizontal = new BorderLine(Length.Pt(3), Color.Blue) } };
        PdfDocument document = Document(table);

        byte[] pdf = DocumentTestKit.Save(document);

        Assert.Equal(2 * document.Diagnostics.PageCount, Count(Content(pdf), "\n3 w\n"));
    }

    [Fact]
    public void Fills_cover_the_header_the_zebra_rows_and_the_cells_that_ask_for_one()
    {
        Color header = Color.FromRgb(200, 0, 0);
        Color zebra = Color.FromRgb(0, 200, 0);
        Color cell = Color.FromRgb(0, 0, 200);
        TableRow[] rows =
        [
            new("r0", "x"),
            new([new TableCell("r1"), new TableCell("y") { Fill = cell }]),
            new("r2", "z"),
            new("r3", "w") { Fill = Color.Gray(0.5) },
        ];
        Table table = new(Two, new TableRow("H1", "H2"), rows) { HeaderFill = header, ZebraFill = zebra };

        byte[] pdf = DocumentTestKit.Save(Document(table));

        using UglyToad.PdfPig.PdfDocument read = UglyToad.PdfPig.PdfDocument.Open(pdf);
        Page page = read.GetPage(1);
        List<(double Red, double Green, double Blue, double Left, double Width)> fills = [.. page.Paths.Where(p => p.IsFilled && p.FillColor is not null).Select(p =>
        {
            (double r, double g, double b) = p.FillColor!.ToRGBValues();
            var box = p.GetBoundingRectangle()!.Value;
            return (r, g, b, box.Left, box.Width);
        })];

        // Header: one rectangle over both columns (the same colour is joined). Zebra: the second and fourth body rows; the fourth has a fill of its own, and the second has a cell with one,
        // so the zebra colour is left on the first cell of the second row only.
        Assert.Single(fills, f => f.Red > 0.7 && f.Green < 0.1);
        Assert.Equal(300, fills.Single(f => f.Red > 0.7).Width, 0.5);
        Assert.Equal(100, fills.Single(f => f.Green > 0.7 && f.Red < 0.1 && f.Blue < 0.1).Width, 0.5);
        Assert.Single(fills, f => f.Blue > 0.7 && f.Red < 0.1);
        Assert.Equal(200, fills.Single(f => f.Blue > 0.7).Width, 0.5);
        Assert.Contains(fills, f => Math.Abs(f.Red - 0.5) < 0.01 && Math.Abs(f.Green - 0.5) < 0.01 && Math.Abs(f.Blue - 0.5) < 0.01);
    }

    // --- limits and diagnostics ------------------------------------------------------------------------------------------------

    [Fact]
    public void A_table_row_limit_of_the_table_draws_the_rows_up_to_it_and_reports_the_table()
    {
        CountingRows rows = new(50);
        PdfDocument document = new(DocumentTestKit.Options());
        document.Add(new Paragraph("intro"));
        document.Add(new Table(Two, rows) { MaxRows = 5 });

        byte[] pdf = DocumentTestKit.Save(document);

        string text = string.Concat(DocumentTestKit.PageTexts(pdf));
        Assert.Contains("a4", text, StringComparison.Ordinal);
        Assert.DoesNotContain("a5", text, StringComparison.Ordinal);
        TruncatedTable report = Assert.Single(document.Diagnostics.TruncatedTables);
        Assert.Equal(1, report.BlockIndex);
        Assert.Equal(5, report.MaxRows);
        Assert.True(rows.Produced < 50);
    }

    [Fact]
    public void A_table_with_exactly_as_many_rows_as_the_limit_is_not_reported()
    {
        PdfDocument document = Document(new Table(Two, Rows(5)) { MaxRows = 5 });

        _ = DocumentTestKit.Save(document);

        Assert.Empty(document.Diagnostics.TruncatedTables);
    }

    [Fact]
    public void The_document_limit_on_table_rows_stops_the_save_with_the_typed_exception()
    {
        PdfDocument document = Document(new Table(Two, Rows(50)), o => o.Limits = new DocumentLimits { MaxTableRows = 10 });

        using MemoryStream stream = new();
        PdfLimitExceededException error = Assert.Throws<PdfLimitExceededException>(() => document.Save(stream));

        Assert.Equal(DocumentLimit.MaxTableRows, error.Limit);
        Assert.Equal(10, error.Maximum);
        Assert.Equal(0, stream.Length);
        PdfDocument fits = Document(new Table(Two, Rows(10)), o => o.Limits = new DocumentLimits { MaxTableRows = 10 });
        _ = DocumentTestKit.Save(fits);
        Assert.Throws<ArgumentOutOfRangeException>(() => new DocumentLimits { MaxTableRows = 0 });
    }

    [Fact]
    public void The_smaller_of_the_two_row_limits_applies()
    {
        PdfDocument truncates = Document(new Table(Two, Rows(50)) { MaxRows = 5 }, o => o.Limits = new DocumentLimits { MaxTableRows = 10 });
        PdfDocument throws = Document(new Table(Two, Rows(50)) { MaxRows = 20 }, o => o.Limits = new DocumentLimits { MaxTableRows = 10 });

        _ = DocumentTestKit.Save(truncates);
        Assert.Single(truncates.Diagnostics.TruncatedTables);
        Assert.Throws<PdfLimitExceededException>(() => DocumentTestKit.Save(throws));
    }

    [Fact]
    public void A_character_no_font_has_in_a_cell_is_reported_with_the_page_it_is_on()
    {
        string missing = char.ConvertFromUtf32(0x6F22);
        PdfDocument document = Document(new Table(Two, new TableRow("HeadA", "HeadB"), Rows(200, i => [$"a{i}", i == 199 ? "x" + missing : $"b{i}"])));

        _ = DocumentTestKit.Save(document);

        MissingGlyph glyph = Assert.Single(document.Diagnostics.MissingGlyphs);
        Assert.Equal(0x6F22, glyph.CodePoint);
        Assert.Equal(document.Diagnostics.PageCount, glyph.Page);
        Assert.True(glyph.Page > 1);
    }

    [Fact]
    public void A_family_missing_from_the_collection_in_a_cell_is_listed()
    {
        TableRow row = new([new TableCell("x") { Style = new TextStyle { Family = "Nowhere Sans" } }]);
        PdfDocument document = Document(new Table([new TableColumn()], [row]));

        _ = DocumentTestKit.Save(document);

        Assert.Equal(["Nowhere Sans"], document.Diagnostics.MissingFamilies);
    }

    [Fact]
    public void An_empty_table_draws_nothing_and_a_table_with_only_a_header_draws_the_header()
    {
        PdfDocument empty = Document(new Table(Two, []));
        PdfDocument headerOnly = Document(new Table(Two, new TableRow("HeadA", "HeadB"), []));

        Assert.Equal("", DocumentTestKit.PageTexts(DocumentTestKit.Save(empty)).Single());
        Assert.Contains("HeadA", DocumentTestKit.PageTexts(DocumentTestKit.Save(headerOnly)).Single(), StringComparison.Ordinal);
    }

    [Fact]
    public void Text_after_a_table_starts_below_it()
    {
        PdfDocument document = new(DocumentTestKit.Options());
        document.Add(new Table(Two, Rows(3)));
        document.Add(new Paragraph("afterwards"));

        using UglyToad.PdfPig.PdfDocument read = UglyToad.PdfPig.PdfDocument.Open(DocumentTestKit.Save(document));

        Page page = read.GetPage(1);
        Assert.True(Top(page, "afterwards") < page.GetWords().Where(w => w.Text.StartsWith('a') && w.Text != "afterwards").Min(w => w.BoundingBox.Bottom));
    }

    // --- values -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Column_widths_are_checked_where_they_are_made()
    {
        Assert.Throws<ArgumentOutOfRangeException>("width", () => ColumnWidth.Fixed(Length.Zero));
        Assert.Throws<ArgumentOutOfRangeException>("width", () => ColumnWidth.Fixed(Length.Pt(-1)));
        Assert.Throws<ArgumentOutOfRangeException>("weight", () => ColumnWidth.Fraction(0));
        Assert.Throws<ArgumentOutOfRangeException>("weight", () => ColumnWidth.Fraction(double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>("weight", () => ColumnWidth.Fraction(double.PositiveInfinity));
        Assert.Equal(ColumnWidthKind.Auto, default(ColumnWidth).Kind);
        Assert.Equal(ColumnWidth.Auto, new TableColumn().Width);
        Assert.Equal(ColumnWidthKind.Fixed, ColumnWidth.Fixed(Length.Pt(5)).Kind);
        Assert.Equal(Length.Pt(5), ColumnWidth.Fixed(Length.Pt(5)).Length);
        Assert.Equal(2, ColumnWidth.Fraction(2).Weight);
        Assert.Equal(ColumnWidth.Fraction(2), ColumnWidth.Fraction(2));
        Assert.NotEqual(ColumnWidth.Fraction(2), ColumnWidth.Fixed(Length.Pt(2)));
    }

    [Fact]
    public void Enumerations_and_lengths_are_checked_where_they_are_set_and_name_the_property()
    {
        Assert.Throws<ArgumentOutOfRangeException>("Alignment", () => new TableColumn { Alignment = (TextAlignment)99 });
        Assert.Throws<ArgumentOutOfRangeException>("Alignment", () => new TableCell("x") { Alignment = (TextAlignment)99 });
        Assert.Throws<ArgumentOutOfRangeException>("Padding", () => new TableCell("x") { Padding = Margins.Pt(-1, 0, 0, 0) });
        Assert.Throws<ArgumentOutOfRangeException>("CellPadding", () => new Table(Two, []) { CellPadding = Margins.Pt(0, double.MaxValue * 0 + 0, 0, -1) });
        Assert.Throws<ArgumentOutOfRangeException>("Direction", () => new Table(Two, []) { Direction = (TextDirection)7 });
        Assert.Throws<ArgumentOutOfRangeException>("SpaceBefore", () => new Table(Two, []) { SpaceBefore = Length.Pt(-1) });
        Assert.Throws<ArgumentOutOfRangeException>("SpaceAfter", () => new Table(Two, []) { SpaceAfter = Length.Pt(-1) });
        Assert.Throws<ArgumentOutOfRangeException>("MaxRows", () => new Table(Two, []) { MaxRows = 0 });
        Assert.Throws<ArgumentNullException>("Borders", () => new Table(Two, []) { Borders = null! });
        Assert.Throws<ArgumentOutOfRangeException>("width", () => new BorderLine(Length.Zero, Color.Black));
        Assert.Throws<ArgumentOutOfRangeException>("OuterHorizontal", () => new Borders { OuterHorizontal = default(BorderLine) });
        Assert.Null(new Table(Two, []).MaxRows);
    }

    [Fact]
    public void A_table_cell_row_and_table_must_be_complete_when_they_are_made()
    {
        Assert.Throws<ArgumentException>("paragraphs", () => new TableCell(Array.Empty<Paragraph>()));
        Assert.Throws<ArgumentException>("paragraphs", () => new TableCell(new Paragraph[] { null! }));
        Assert.Throws<ArgumentNullException>("paragraphs", () => new TableCell((IEnumerable<Paragraph>)null!));
        Assert.Throws<ArgumentException>("cells", () => new TableRow(Array.Empty<TableCell>()));
        Assert.Throws<ArgumentException>("cells", () => new TableRow(new TableCell[] { null! }));
        Assert.Throws<ArgumentNullException>("texts", () => new TableRow(new string[] { null! }));
        Assert.Throws<ArgumentException>("columns", () => new Table(Array.Empty<TableColumn>(), []));
        Assert.Throws<ArgumentException>("columns", () => new Table(new TableColumn[] { null! }, []));
        Assert.Throws<ArgumentNullException>("columns", () => new Table(null!, []));
        Assert.Throws<ArgumentNullException>("rows", () => new Table(Two, (IEnumerable<TableRow>)null!));
        Assert.Throws<ArgumentException>("header", () => new Table(Two, new TableRow("a", "b", "c"), []));
        Assert.Throws<ArgumentNullException>("header", () => new Table(Two, (TableRow)null!, []));
        Assert.Throws<ArgumentException>("header", () => new Table(Two, new TableRow?[] { null }.Select(r => r!), []));
    }

    [Fact]
    public void Cells_rows_and_tables_compare_by_value_and_the_rows_of_a_table_by_reference()
    {
        Assert.Equal(new TableCell("a") { Fill = Color.Red }, new TableCell("a") { Fill = Color.Red });
        Assert.NotEqual(new TableCell("a"), new TableCell("b"));
        Assert.Equal(new TableCell("a").GetHashCode(), new TableCell("a").GetHashCode());
        Assert.Equal(new TableCell([new Paragraph("a"), new Paragraph("b")]), new TableCell([new Paragraph("a"), new Paragraph("b")]));
        Assert.Equal(new TableRow("a", "b"), new TableRow("a", "b"));
        Assert.Equal(new TableRow("a", "b").GetHashCode(), new TableRow("a", "b").GetHashCode());
        Assert.NotEqual(new TableRow("a", "b"), new TableRow("a", "c"));
        Assert.NotEqual(new TableRow("a", "b"), new TableRow("a", "b") { Fill = Color.Red });
        Assert.Equal(new TableColumn(ColumnWidth.Fraction(1)), new TableColumn(ColumnWidth.Fraction(1)));
        Assert.NotEqual(new TableColumn(ColumnWidth.Fraction(1)) { Alignment = TextAlignment.End }, new TableColumn(ColumnWidth.Fraction(1)));
        Assert.Equal(Borders.All, Borders.All with { });
        Assert.NotEqual(Borders.All, Borders.Horizontal);
        Assert.Equal(new BorderLine(Length.Pt(1), Color.Black), new BorderLine(Length.Pt(1), Color.Black));

        TableRow[] rows = [new("a", "b")];
        Table one = new(Two, new TableRow("h", "i"), rows);
        Table same = new([new TableColumn(ColumnWidth.Fixed(Length.Pt(100))), new TableColumn(ColumnWidth.Fixed(Length.Pt(200)))], [new TableRow("h", "i")], rows);
        Assert.Equal(one, same);
        Assert.Equal(one.GetHashCode(), same.GetHashCode());
        Assert.NotEqual(one, new Table(Two, new TableRow("h", "i"), [new TableRow("a", "b")]));
        Assert.NotEqual(one, one with { RepeatHeader = false });
        Assert.NotEqual(one, one with { ZebraFill = Color.Red });
        Assert.NotEqual<Block>(one, new Paragraph("h"));
    }

    [Fact]
    public void The_cells_of_a_row_and_the_columns_of_a_table_are_copied_when_made()
    {
        List<TableCell> cells = [new("a")];
        TableRow row = new(cells);
        cells.Add(new TableCell("b"));
        List<TableColumn> columns = [new()];
        Table table = new(columns, [row]);
        columns.Add(new TableColumn());

        Assert.Single(row.Cells);
        Assert.Single(table.Columns);
        Assert.Throws<NotSupportedException>(() => ((IList<TableCell>)row.Cells).Add(new TableCell("c")));
    }

    [Fact]
    public void A_table_is_a_block_that_can_be_added_to_any_number_of_documents()
    {
        Table table = new(Two, new TableRow("HeadA", "HeadB"), Rows(3));

        byte[] first = DocumentTestKit.Save(Document(table));
        byte[] second = DocumentTestKit.Save(Document(table));

        Assert.Equal(first, second);
    }
}
