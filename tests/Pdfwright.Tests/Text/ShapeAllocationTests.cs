using System.Buffers;
using System.Globalization;
using System.Text;
using Pdfwright.Document;
using Pdfwright.Tests.Document;
using Pdfwright.Text;
using Pdfwright.Unicode;

namespace Pdfwright.Tests.Text;

/// <summary>
/// The shaper rents the arrays that live only while a paragraph is laid out, the layer above keeps one <see cref="RunStyle"/> for every style that cells share, and a layout allocates little more than what it returns.
/// None of it may change a result: a rented array is longer than the text and holds what its last user left.
/// </summary>
[Collection("ShapingFastPaths")]
public class ShapeAllocationTests
{
    private static readonly string[] Texts =
    [
        "Row 12 cell 3 text", "a", " ", "  two  spaces  ", "price: $9.99 (net)", "Row 12\tcell\t3", "hello world hello world hello world hello world",
        "é", "straße and café", "مرحبا بالعالم", "abc مرحبا def", "שלום עולם", "日本語のテキスト", "a\u2028b", "x 😀 y", "ﬁne office", "Ta Te To",
    ];

    /// <summary>
    /// The bytes a layout of a one-line cell may allocate: what it returns (the layout, its line, its run and the glyphs, about 1.5 KB) with room for the runtime's variations. Before the arrays of the shaper and the line builder
    /// were rented and the delegates and boxed enumerators taken out it was over 2.7 KB, which is the limit this must stay under.
    /// </summary>
    private const int OneLineBudget = 2200;

    /// <summary>The same for a cell that wraps over several lines (it was over 9.3 KB).</summary>
    private const int WrappedBudget = 7000;

    /// <summary>Dirties pooled arrays of every type the shaper rents, a dozen of each size so that the per-core stacks are filled as well as the slot of this thread.</summary>
    private static void Poison()
    {
        static void Fill<T>(T value)
        {
            for (int size = 8; size <= ScratchArray.PoolLimit; size *= 2)
            {
                T[][] held = new T[12][];
                for (int k = 0; k < held.Length; k++)
                {
                    held[k] = ArrayPool<T>.Shared.Rent(size);
                    Array.Fill(held[k], value);
                }

                foreach (T[] array in held)
                {
                    ArrayPool<T>.Shared.Return(array);
                }
            }
        }

        Fill(987654);
        Fill((byte)5);
        Fill(BidiClass.R);
        Fill(1e9);
        Fill(true);
    }

    private static string Describe(ShapedParagraph p)
    {
        int n = p.CodePointCount;
        StringBuilder sb = new();
        _ = sb.Append(n).Append('|').Append(p.ParagraphLevel).Append('|').Append(string.Join(',', p.Missing)).Append('|');
        _ = sb.Append(string.Join(',', p.CodePoints.Take(n))).Append('|').Append(string.Join(',', p.Utf16Index.Take(n + 1))).Append('|');
        _ = sb.Append(string.Join(',', p.Bidi.Levels.Take(n))).Append('|').Append(string.Join(',', p.Bidi.Classes.Take(n))).Append('|');
        foreach (ShapedRun r in p.Runs)
        {
            _ = sb.Append(CultureInfo.InvariantCulture, $"[{r.Utf16Start},{r.Utf16Length},{r.Level},{r.Script},{r.FontIndex},{r.Style}:{string.Join(";", r.Glyphs.Select(g => $"{g.Glyph}/{g.Cluster}/{g.XAdvance}/{g.XOffset}/{g.YOffset}"))}]");
        }

        return sb.ToString();
    }

    private static string Describe(ParagraphLayout layout) =>
        string.Concat(layout.Lines.Select(l =>
            FormattableString.Invariant($"<{l.Start},{l.Length},{l.Width:R},{l.X:R},{l.Pitch:R}")
            + string.Concat(l.Runs.Select(r => FormattableString.Invariant($"({r.Run.Utf16Start},{r.Run.FontIndex},{r.X:R},{r.Width:R},{string.Join(';', r.Run.Glyphs.Select(g => g.Glyph))})")))
            + ">"))
        + "|" + string.Join(',', layout.Styles.Select(s => FormattableString.Invariant($"{s.Size:R}/{string.Join('.', s.FontOrder)}")));

    [Fact]
    public void A_pooled_paragraph_is_what_an_unpooled_one_is_whatever_the_pooled_arrays_hold()
    {
        ShapingFont[] fonts = LayoutTestKit.Fonts(LayoutTestKit.Latin, LayoutTestKit.Arabic, LayoutTestKit.Hebrew);
        int[] order = [0, 1, 2];
        foreach (string text in Texts)
        {
            // The text the layouter shapes has its tabs as spaces, and the bidi text keeps them.
            string shaped = text.Replace('\t', ' ');
            foreach (bool styled in new[] { false, true })
            {
                foreach (int level in new[] { -1, 0, 1 })
                {
                    int cut = Math.Min(4, shaped.Length);
                    ShapeStyling? styling = styled
                        ? new ShapeStyling { Spans = [new StyledSpan(0, cut, 0), new StyledSpan(cut, shaped.Length - cut, 1)], FontOrders = [order, order], ShapeKeys = [0, 1], Languages = [null, "en"] }
                        : null;
                    if (styling is not null && cut == shaped.Length)
                    {
                        styling = new ShapeStyling { Spans = [new StyledSpan(0, cut, 0)], FontOrders = [order], ShapeKeys = [0], Languages = [null] };
                    }

                    string expected = Describe(TextShaper.Shape(shaped, fonts, level, null, text, styling));
                    for (int round = 0; round < 3; round++)
                    {
                        Poison();
                        ShapedParagraph pooled = TextShaper.Shape(shaped, fonts, level, null, text, styling, pooled: true);
                        try
                        {
                            Assert.Equal(expected, Describe(pooled));
                        }
                        finally
                        {
                            pooled.Release();
                        }
                    }
                }
            }
        }
    }

    [Fact]
    public void Only_printable_ASCII_is_rented_and_the_rest_is_shaped_as_ever()
    {
        ShapingFont[] fonts = LayoutTestKit.Fonts(LayoutTestKit.Latin, LayoutTestKit.Arabic);
        ShapedParagraph ascii = TextShaper.Shape("Row 12 cell 3 text", fonts, pooled: true);
        Assert.True(ascii.Pooled);
        Assert.Equal(18, ascii.CodePointCount);
        ascii.Release();

        // A paragraph that does not take the simple path, and an empty one, hold plain arrays of the exact length and have nothing to give back.
        foreach (string text in new[] { "é", "مرحبا", string.Empty })
        {
            ShapedParagraph other = TextShaper.Shape(text, fonts, pooled: true);
            Assert.False(other.Pooled);
            Assert.Equal(other.CodePoints.Length, other.CodePointCount);
            other.Release();
        }
    }

    [Fact]
    public void A_paragraph_gives_its_arrays_back_once_however_often_it_is_released()
    {
        ShapingFont[] fonts = LayoutTestKit.Fonts(LayoutTestKit.Latin);
        ShapedParagraph paragraph = TextShaper.Shape("Row 12 cell 3 text of some length", fonts, pooled: true);
        int[] codePoints = paragraph.CodePoints;
        paragraph.Release();
        paragraph.Release();
        paragraph.Release();

        // An array that was returned twice would be handed out twice.
        List<int[]> rented = [];
        for (int i = 0; i < 6; i++)
        {
            rented.Add(ScratchArray.Rent<int>(codePoints.Length));
        }

        Assert.Equal(rented.Count, rented.Distinct().Count());
        foreach (int[] array in rented)
        {
            ScratchArray.Return(array);
        }
    }

    [Fact]
    public void A_paragraph_that_cannot_be_shaped_gives_its_arrays_back_and_a_later_one_is_unharmed()
    {
        ShapingFont[] fonts = LayoutTestKit.Fonts(LayoutTestKit.Latin);
        string text = "Row 12 cell 3 text";
        string expected = Describe(TextShaper.Shape(text, fonts));
        int[] order = [0];

        // The spans do not tile the text: the shaper throws after it has rented the paragraph's arrays and its scratch.
        ShapeStyling bad = new() { Spans = [new StyledSpan(0, 5, 0)], FontOrders = [order], ShapeKeys = [0], Languages = [null] };
        for (int i = 0; i < 20; i++)
        {
            _ = Assert.Throws<ArgumentException>(() => TextShaper.Shape(text, fonts, UnicodeBidi.AutoLevel, null, null, bad, pooled: true));
        }

        List<int[]> rented = [];
        for (int i = 0; i < 6; i++)
        {
            rented.Add(ScratchArray.Rent<int>(text.Length));
        }

        Assert.Equal(rented.Count, rented.Distinct().Count());
        foreach (int[] array in rented)
        {
            ScratchArray.Return(array);
        }

        Poison();
        ShapedParagraph again = TextShaper.Shape(text, fonts, pooled: true);
        Assert.Equal(expected, Describe(again));
        again.Release();
    }

    [Fact]
    public void The_glyphs_of_a_run_shaped_word_by_word_are_of_the_size_they_have()
    {
        ShapingFont[] fonts = LayoutTestKit.Fonts(LayoutTestKit.Latin);
        ShapedParagraph paragraph = TextShaper.Shape("hello world hello  world and some more words to shape", fonts);
        foreach (ShapedRun run in paragraph.Runs)
        {
            Assert.Equal(run.Utf16Length, run.Glyphs.Length);
        }
    }

    [Fact]
    public void A_style_that_cells_share_is_one_RunStyle_that_is_what_a_new_one_would_be()
    {
        using FontCollection collection = DocumentTestKit.Collection("NotoSans-Regular.ttf", "NotoSans-Bold.ttf");
        FontCollection.Snapshot snapshot = collection.Current;
        ResolvedStyle style = ResolvedStyle.Base(snapshot, null, "en");
        ResolvedStyle same = ResolvedStyle.Base(snapshot, null, "en");
        ResolvedStyle bold = style.Apply(new TextStyle { Weight = 700 });
        ResolvedStyle red = style.Apply(new TextStyle { Color = Color.Red });

        RunStyle first = snapshot.RunStyleOf(style);
        Assert.Same(first, snapshot.RunStyleOf(same));
        Assert.NotSame(first, snapshot.RunStyleOf(bold));
        Assert.NotSame(first, snapshot.RunStyleOf(red));
        Assert.Equal(red.Color, snapshot.RunStyleOf(red).Color);
        Assert.Equal(snapshot.Order(style.Family, style.Weight, style.Italic), first.FontOrder);
        Assert.Equal(style.Size, first.Size);
        Assert.Equal(style.Language, first.Language);
        Assert.Equal(style.Color, first.Color);
        Assert.NotEqual(snapshot.RunStyleOf(bold).FontOrder, first.FontOrder);

        // More distinct styles than the table keeps: every one is still right, and the table does not grow without limit.
        for (int i = 0; i < 600; i++)
        {
            ResolvedStyle odd = style.Apply(new TextStyle { Size = Length.Pt(5 + (i / 10.0)) });
            RunStyle made = snapshot.RunStyleOf(odd);
            Assert.Equal(odd.Size, made.Size);
            Assert.Equal(first.FontOrder, made.FontOrder);
        }
    }

    [Fact]
    public void A_layout_is_the_same_with_a_cold_table_of_styles_as_with_a_warm_one_and_in_parallel()
    {
        TextRun[][] cells =
        [
            [new TextRun("Row 12 cell 3 text")],
            [new TextRun("Row "), new TextRun("12", new TextStyle { Weight = 700 }), new TextRun(" cell 3 text and some more words so that it wraps")],
            [new TextRun("abc مرحبا def")],
        ];

        static string Run(FontCollection.Snapshot snapshot, TextRun[][] cells)
        {
            ResolvedStyle style = ResolvedStyle.Base(snapshot, null, "en");
            return string.Join("#", cells.Select(c => Describe(BlockText.Layout(c, style, snapshot, 90, TextAlignment.Start, TextDirection.Auto, 1.0).Layout)));
        }

        using FontCollection warm = DocumentTestKit.Collection("NotoSans-Regular.ttf", "NotoSans-Bold.ttf", "NotoSansArabic-Regular.ttf");

        // Another collection has its own table, which is empty: its first run is the cold one.
        using FontCollection other = DocumentTestKit.Collection("NotoSans-Regular.ttf", "NotoSans-Bold.ttf", "NotoSansArabic-Regular.ttf");
        string cold = Run(other.Current, cells);
        string expected = Run(warm.Current, cells);
        Assert.Equal(expected, cold);
        Assert.Equal(expected, Run(warm.Current, cells));
        Assert.Equal(expected, Run(other.Current, cells));

        ParallelOptions options = new() { MaxDegreeOfParallelism = 8, CancellationToken = TestContext.Current.CancellationToken };
        Parallel.For(0, 64, options, _ => Assert.Equal(expected, Run(warm.Current, cells)));
    }

    [Fact]
    public void The_levels_of_a_line_past_the_count_of_a_rented_result_are_refused_though_the_array_is_longer()
    {
        ShapingFont[] fonts = LayoutTestKit.Fonts(LayoutTestKit.Latin);
        ShapedParagraph paragraph = TextShaper.Shape("Row 12 cell 3 text", fonts, pooled: true);
        try
        {
            Assert.Equal(18, paragraph.Bidi.Count);
            Assert.True(paragraph.Bidi.Levels.Length >= 18);
            UnicodeBidi.LineLevelsInto(paragraph.Bidi, 0, 18, new byte[32]);
            _ = Assert.Throws<ArgumentOutOfRangeException>(() => UnicodeBidi.LineLevelsInto(paragraph.Bidi, 0, 19, new byte[32]));
        }
        finally
        {
            paragraph.Release();
        }
    }
    [Fact]
    public void The_levels_of_a_line_written_into_a_buffer_are_those_of_a_new_array()
    {
        Random random = new(20261201);
        string[] pieces = ["abc", " ", "مرحبا", "שלום", "123", "  ", "-", "x y"];
        for (int i = 0; i < 300; i++)
        {
            string text = string.Concat(Enumerable.Range(0, 1 + random.Next(8)).Select(_ => pieces[random.Next(pieces.Length)]));
            int[] codePoints = UnicodeText.CodePoints(text, out _);
            BidiResult bidi = UnicodeBidi.Resolve(codePoints, new[] { UnicodeBidi.AutoLevel, 0, 1 }[random.Next(3)]);
            int start = random.Next(codePoints.Length + 1);
            int end = start + random.Next(codePoints.Length - start + 1);
            byte[] expected = UnicodeBidi.LineLevels(bidi, start, end);
            byte[] buffer = new byte[(end - start) + random.Next(5)];
            Array.Fill(buffer, (byte)9);
            UnicodeBidi.LineLevelsInto(bidi, start, end, buffer);
            Assert.True(expected.AsSpan().SequenceEqual(buffer.AsSpan(0, end - start)));
        }

        _ = Assert.Throws<ArgumentOutOfRangeException>(() => UnicodeBidi.LineLevelsInto(UnicodeBidi.Resolve([65, 66, 67], 0), 0, 3, new byte[2]));
    }

    [Fact]
    public void Laying_out_the_cell_of_a_table_allocates_little_more_than_what_it_returns()
    {
        using FontCollection collection = DocumentTestKit.Collection("NotoSans-Regular.ttf");
        FontCollection.Snapshot snapshot = collection.Current;
        ResolvedStyle style = ResolvedStyle.Base(snapshot, null, "en");
        TextRun[] one = [new TextRun("Row 12 cell 3 text")];
        TextRun[] wrapped = [new TextRun("Some longer text of a cell that wraps over a few lines in a narrow column")];

        // Warm: the fonts, the word tables, the pool and the style table are filled, so that what is measured is a layout and not the first use.
        for (int i = 0; i < 50; i++)
        {
            _ = BlockText.Layout(one, style, snapshot, 100, TextAlignment.Start, TextDirection.Auto, 1.0);
            _ = BlockText.Layout(wrapped, style, snapshot, 100, TextAlignment.Start, TextDirection.Auto, 1.0);
        }

        const int Cells = 400;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < Cells; i++)
        {
            _ = BlockText.Layout(one, style, snapshot, 100, TextAlignment.Start, TextDirection.Auto, 1.0);
        }

        double perCell = (GC.GetAllocatedBytesForCurrentThread() - before) / (double)Cells;

        before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < Cells; i++)
        {
            _ = BlockText.Layout(wrapped, style, snapshot, 100, TextAlignment.Start, TextDirection.Auto, 1.0);
        }

        double perWrapped = (GC.GetAllocatedBytesForCurrentThread() - before) / (double)Cells;
        Assert.True(perCell < OneLineBudget, $"A one-line cell allocated {perCell:F0} bytes.");
        Assert.True(perWrapped < WrappedBudget, $"A cell of several lines allocated {perWrapped:F0} bytes.");
    }
}
