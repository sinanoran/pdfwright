using Pdfwright.Text;
using Pdfwright.Unicode;

namespace Pdfwright.Tests.Text;

/// <summary>What <see cref="ParagraphLayouter"/> promises about lines: where they break, how wide they are, where they sit, and the order of their runs.</summary>
public class ParagraphLayoutTests
{
    private const double Tolerance = 1e-6;
    private const double Size = 14;

    /// <summary>U+2028 LINE SEPARATOR: a hard line break that is not a paragraph separator, so it may stand inside a paragraph.</summary>
    private const char LineSeparator = (char)0x2028;

    private static readonly int[] NarrowWidths = [40, 90, 200];

    private const string LatinSentence = "The quick brown fox jumps over the lazy dog, then keeps running through the quiet valley until night falls.";

    /// <summary>Every corpus line at three widths narrower than the line itself.</summary>
    public static TheoryData<string, int> CorpusAtNarrowWidths()
    {
        TheoryData<string, int> data = [];
        foreach (CorpusTests.Case c in CorpusTests.Load())
        {
            foreach (int width in NarrowWidths)
            {
                data.Add(c.Id, width);
            }
        }

        return data;
    }

    public static TheoryData<string> MixedDirectionCases()
    {
        TheoryData<string> data = [];
        foreach (CorpusTests.Case c in CorpusTests.Load().Where(x => x.Id.StartsWith("mixed-", StringComparison.Ordinal)))
        {
            data.Add(c.Id);
        }

        return data;
    }

    private static string LineText(string text, LaidOutLine line) => text.Substring(line.Start, line.Length);

    private static double MetricScale(ShapingFont font) => Size / font.Font.UnitsPerEm;

    /// <summary>The lines' text tiles the paragraph: one after another from 0 to the end, none empty.</summary>
    private static void AssertTiles(string text, ParagraphLayout layout)
    {
        int next = 0;
        foreach (LaidOutLine line in layout.Lines)
        {
            Assert.Equal(next, line.Start);
            Assert.True(line.Length > 0, "a line of a non-empty text is empty");
            next += line.Length;
        }

        Assert.Equal(text.Length, next);
    }

    [Theory]
    [MemberData(nameof(CorpusAtNarrowWidths))]
    public void Lines_tile_the_text_and_start_only_at_cluster_boundaries(string id, int width)
    {
        CorpusTests.Case c = LayoutTestKit.Case(id);
        ShapingFont[] fonts = LayoutTestKit.Fonts(c.Fonts);

        ParagraphLayout layout = LayoutTestKit.Layout(c.Text, fonts, width);

        AssertTiles(c.Text, layout);

        // Two independent views of "inside a cluster": the glyph clusters of a fresh shaping, and the grapheme clusters of UAX #29.
        ShapedParagraph shaped = TextShaper.Shape(c.Text, fonts);
        HashSet<int> glyphBoundaries = [.. shaped.Runs.Select(r => r.Utf16Start), .. shaped.Runs.SelectMany(r => r.Glyphs).Select(g => g.Cluster), c.Text.Length];
        int[] codePoints = UnicodeText.CodePoints(c.Text, out int[] utf16Index);
        bool[] graphemeBoundary = UnicodeGraphemeBreaker.Boundaries(codePoints);
        foreach (LaidOutLine line in layout.Lines)
        {
            Assert.Contains(line.Start, glyphBoundaries);
            int cp = Array.IndexOf(utf16Index, line.Start);
            Assert.True(cp >= 0, "a line starts inside a surrogate pair");
            Assert.True(cp == codePoints.Length || graphemeBoundary[cp], $"line at {line.Start} starts inside a grapheme cluster");
        }
    }

    [Theory]
    [MemberData(nameof(CorpusAtNarrowWidths))]
    public void A_line_fits_its_width_unless_it_is_one_unbreakable_unit(string id, int width)
    {
        CorpusTests.Case c = LayoutTestKit.Case(id);

        ParagraphLayout layout = LayoutTestKit.Layout(c.Text, LayoutTestKit.Fonts(c.Fonts), width);

        foreach (LaidOutLine line in layout.Lines)
        {
            if (line.Width <= width + Tolerance)
            {
                continue;
            }

            // Too wide: then nothing inside the line's content may be a place where it could have been cut.
            int[] content = UnicodeText.CodePoints(LineText(c.Text, line).TrimEnd(), out _);
            LineBreakKind[] breaks = UnicodeLineBreaker.Breaks(content);
            for (int i = 1; i < content.Length; i++)
            {
                Assert.Equal(LineBreakKind.None, breaks[i]);
            }
        }
    }

    [Fact]
    public void Lines_of_a_latin_sentence_break_at_spaces_and_the_space_hangs()
    {
        ShapingFont[] fonts = LayoutTestKit.Fonts(LayoutTestKit.Latin);
        double two = TextMeasurer.Measure("aaa bbb", fonts, Size).Width;

        // The room is exactly the width of "aaa bbb": the space after it does not count, so the first line still holds it
        // (counted, "aaa bbb " would not fit and the line would end after "aaa ").
        ParagraphLayout layout = LayoutTestKit.Layout("aaa bbb ccc", fonts, two);

        Assert.Equal(2, layout.LineCount);
        Assert.Equal(8, layout.Lines[0].Length);
        Assert.Equal(two, layout.Lines[0].Width, Tolerance);
        Assert.Equal(3, layout.Lines[1].Length);
        Assert.Contains(layout.Lines[0].Runs, r => r.IsTrailingSpace);
    }

    [Fact]
    public void White_space_between_two_right_to_left_words_goes_back_to_the_paragraph_level_when_a_line_ends_in_it()
    {
        ShapingFont[] fonts = LayoutTestKit.Fonts(LayoutTestKit.Latin, LayoutTestKit.Arabic);
        string[] words = LayoutTestKit.Case("arabic-hello").Text.Split(' ');
        string first = "abc " + words[0];
        string text = first + "  " + words[1];
        double room = TextMeasurer.Measure(first, fonts, Size).Width + 0.01;

        // In a left-to-right paragraph the two spaces between the Arabic words are right-to-left (between two letters of that direction),
        // until the line ends after them: rule L1 puts them back at level 0, after the Arabic word on the right.
        ParagraphLayout layout = LayoutTestKit.Layout(text, fonts, room, paragraphLevel: 0);

        Assert.Equal(2, layout.LineCount);
        LaidOutLine line = layout.Lines[0];
        Assert.Equal(first.Length + 2, line.Length);
        PlacedRun trailing = Assert.Single(line.Runs, r => r.IsTrailingSpace);
        Assert.Equal(0, trailing.Run.Level);
        Assert.Equal(2, trailing.Run.Glyphs.Length);
        Assert.True(trailing.Run.Glyphs[0].Cluster < trailing.Run.Glyphs[1].Cluster, "glyphs of a left-to-right piece are listed in text order");
        Assert.Equal(line.Runs.Max(r => r.X), trailing.X, Tolerance);
        Assert.Equal(line.X + line.Width, trailing.X, Tolerance);
        Assert.Contains(line.Runs, r => r.Run.Level == 1 && !r.IsTrailingSpace);
    }

    [Fact]
    public void White_space_at_the_end_of_a_line_is_not_counted_in_its_width_and_hangs_after_the_content()
    {
        ShapingFont[] fonts = LayoutTestKit.Fonts(LayoutTestKit.Latin);

        ParagraphLayout bare = LayoutTestKit.Layout("word", fonts, null);
        ParagraphLayout spaced = LayoutTestKit.Layout("word   ", fonts, null);

        LaidOutLine line = Assert.Single(spaced.Lines);
        Assert.Equal(7, line.Length);
        Assert.Equal(bare.Lines[0].Width, line.Width, Tolerance);
        PlacedRun trailing = Assert.Single(line.Runs, r => r.IsTrailingSpace);
        Assert.True(trailing.Width > 0);
        Assert.Equal(line.X + line.Width, trailing.X, Tolerance);
    }

    [Fact]
    public void In_a_right_to_left_paragraph_trailing_white_space_hangs_to_the_left_of_the_content()
    {
        ShapingFont[] fonts = LayoutTestKit.Fonts(LayoutTestKit.Arabic, LayoutTestKit.Latin);
        string text = LayoutTestKit.Case("arabic-hello").Text + "  ";

        ParagraphLayout layout = LayoutTestKit.Layout(text, fonts, null);

        LaidOutLine line = Assert.Single(layout.Lines);
        PlacedRun trailing = Assert.Single(line.Runs, r => r.IsTrailingSpace);
        Assert.Equal(1, trailing.Run.Level);
        Assert.Equal(line.X, trailing.X + trailing.Width, Tolerance);
        Assert.Equal(0, line.X, Tolerance);
        Assert.Equal(0, line.Runs.Where(r => !r.IsTrailingSpace).Min(r => r.X), Tolerance);
    }

    [Fact]
    public void Start_end_and_center_follow_the_direction_of_the_paragraph()
    {
        ShapingFont[] latin = LayoutTestKit.Fonts(LayoutTestKit.Latin);
        ShapingFont[] arabic = LayoutTestKit.Fonts(LayoutTestKit.Arabic, LayoutTestKit.Latin);
        const double box = 200;

        foreach ((string text, ShapingFont[] fonts, bool rtl) in new[] { ("Quarterly report", latin, false), (LayoutTestKit.Case("arabic-hello").Text, arabic, true) })
        {
            LaidOutLine start = Assert.Single(LayoutTestKit.Layout(text, fonts, box, TextAlignment.Start).Lines);
            LaidOutLine end = Assert.Single(LayoutTestKit.Layout(text, fonts, box, TextAlignment.End).Lines);
            LaidOutLine center = Assert.Single(LayoutTestKit.Layout(text, fonts, box, TextAlignment.Center).Lines);

            Assert.True(start.Width < box);
            Assert.Equal(start.Width, end.Width, Tolerance);
            Assert.Equal(rtl ? box - start.Width : 0, start.X, Tolerance);
            Assert.Equal(rtl ? 0 : box - start.Width, end.X, Tolerance);
            Assert.Equal((box - start.Width) / 2, center.X, Tolerance);

            // The runs are where X says: the leftmost content run starts at X and the rightmost ends at X plus the width.
            double right = start.Runs.Where(r => !r.IsTrailingSpace).Max(r => r.X + r.Width);
            Assert.Equal(start.X, start.Runs.Where(r => !r.IsTrailingSpace).Min(r => r.X), Tolerance);
            Assert.Equal(start.X + start.Width, right, Tolerance);
        }
    }

    [Fact]
    public void Without_a_width_the_widest_line_is_the_box_so_a_right_to_left_paragraph_aligns_to_its_right_edge()
    {
        ShapingFont[] fonts = LayoutTestKit.Fonts(LayoutTestKit.Arabic, LayoutTestKit.Latin);

        // A hard line break (U+2028) inside one paragraph: a short line over a long one.
        string text = LayoutTestKit.Case("arabic-greeting").Text + LineSeparator + LayoutTestKit.Case("arabic-lam-alef").Text;
        ParagraphLayout layout = LayoutTestKit.Layout(text, fonts, null);

        Assert.Equal(2, layout.LineCount);
        Assert.True(layout.Lines[0].Width < layout.Lines[1].Width);
        Assert.Equal(0, layout.Lines[1].X, Tolerance);
        Assert.Equal(layout.Lines[1].Width - layout.Lines[0].Width, layout.Lines[0].X, Tolerance);
        Assert.Equal(layout.Width, layout.Lines[1].Width, Tolerance);
    }

    [Fact]
    public void A_unit_wider_than_the_box_is_aligned_to_the_start_whatever_the_alignment()
    {
        ShapingFont[] latin = LayoutTestKit.Fonts(LayoutTestKit.Latin);
        ShapingFont[] arabic = LayoutTestKit.Fonts(LayoutTestKit.Arabic);

        foreach (TextAlignment alignment in new[] { TextAlignment.End, TextAlignment.Center })
        {
            LaidOutLine ltr = Assert.Single(LayoutTestKit.Layout("Donaudampfschifffahrt", latin, 10, alignment).Lines);
            Assert.Equal(0, ltr.X, Tolerance);

            LaidOutLine rtl = Assert.Single(LayoutTestKit.Layout(LayoutTestKit.Case("long-unbreakable-ar").Text, arabic, 10, alignment).Lines);
            Assert.True(rtl.Width > 10);
            Assert.Equal(10 - rtl.Width, rtl.X, Tolerance);
        }
    }

    [Fact]
    public void The_number_of_lines_never_decreases_as_the_width_shrinks()
    {
        foreach (string id in new[] { "latin-de", "arabic-lam-alef", "mixed-en-in-ar", "mixed-ar-in-en", "thai-sentence" })
        {
            CorpusTests.Case c = LayoutTestKit.Case(id);
            ShapingFont[] fonts = LayoutTestKit.Fonts(c.Fonts);

            int previous = 0;
            for (int width = 600; width >= 20; width -= 10)
            {
                int count = LayoutTestKit.Layout(c.Text, fonts, width).LineCount;
                Assert.True(count >= previous, $"{id}: {count} lines at {width}, {previous} at {width + 10}");
                previous = count;
            }
        }
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    public void A_width_smaller_than_every_unit_still_ends_with_one_unit_a_line(double width)
    {
        CorpusTests.Case c = LayoutTestKit.Case("latin-long-unbreakable");
        ShapingFont[] fonts = LayoutTestKit.Fonts(c.Fonts);

        ParagraphLayout layout = LayoutTestKit.Layout(c.Text, fonts, width);

        AssertTiles(c.Text, layout);
        Assert.Equal(c.Text.Split(' ').Length, layout.LineCount);
        Assert.All(layout.Lines, l => Assert.True(l.Width > width));
    }

    [Fact]
    public void An_empty_text_is_one_empty_line_with_the_metrics_of_the_first_font()
    {
        ShapingFont[] fonts = LayoutTestKit.Fonts(LayoutTestKit.Arabic, LayoutTestKit.Latin);

        ParagraphLayout layout = LayoutTestKit.Layout(string.Empty, fonts, 100);

        LaidOutLine line = Assert.Single(layout.Lines);
        Assert.Equal(0, line.Start);
        Assert.Equal(0, line.Length);
        Assert.Equal(0, line.Width);
        Assert.Empty(line.Runs);
        double ascent = fonts[0].Font.Ascender * MetricScale(fonts[0]);
        double descent = -fonts[0].Font.Descender * MetricScale(fonts[0]);
        Assert.Equal(ascent, line.Ascent, Tolerance);
        Assert.Equal(descent, line.Descent, Tolerance);
        Assert.Equal(ascent + descent, layout.Height, Tolerance);
        Assert.Equal(0, layout.Width);
    }

    [Fact]
    public void Paragraphs_are_laid_out_one_after_another_and_hard_line_breaks_make_empty_lines()
    {
        ShapingFont[] fonts = LayoutTestKit.Fonts(LayoutTestKit.Latin);
        const string text = "first line\nsecond line is a bit longer\n\nfourth";
        int[] codePoints = UnicodeText.CodePoints(text, out _);

        List<(int Start, int Length)> paragraphs = UnicodeBidi.Paragraphs(codePoints);

        Assert.Equal(4, paragraphs.Count);
        ParagraphLayout[] layouts = [.. paragraphs.Select(p => LayoutTestKit.Layout(text.Substring(p.Start, p.Length), fonts, null))];
        Assert.All(layouts, l => Assert.Equal(1, l.LineCount));
        Assert.Equal(11, layouts[0].Lines[0].Length);

        // The empty paragraph is a line of its own with nothing in it and the height of any other line of the font.
        Assert.Equal(1, layouts[2].Lines[0].Length);
        Assert.Equal(0, layouts[2].Lines[0].Width);
        Assert.Empty(layouts[2].Lines[0].Runs);
        Assert.Equal(layouts[0].Lines[0].Ascent + layouts[0].Lines[0].Descent, layouts[2].Height, Tolerance);
    }

    [Fact]
    public void A_hard_line_break_inside_a_paragraph_ends_a_line_and_two_in_a_row_leave_an_empty_line()
    {
        ShapingFont[] fonts = LayoutTestKit.Fonts(LayoutTestKit.Latin);
        string text = "ab" + LineSeparator + LineSeparator + "cd";

        ParagraphLayout layout = LayoutTestKit.Layout(text, fonts, null);

        Assert.Equal(3, layout.LineCount);
        Assert.Equal([0, 3, 4], layout.Lines.Select(l => l.Start).ToArray());
        Assert.Equal([3, 1, 2], layout.Lines.Select(l => l.Length).ToArray());
        Assert.Empty(layout.Lines[1].Runs);

        // Nothing is drawn for the break itself.
        Assert.All(layout.Lines[0].Runs, r => Assert.True(r.Run.Utf16Start + r.Run.Utf16Length <= 2));
        Assert.Equal(layout.Lines[0].Pitch, layout.Lines[1].Pitch, Tolerance);
    }

    [Fact]
    public void A_line_break_at_the_end_of_the_text_adds_no_empty_line()
    {
        ShapingFont[] fonts = LayoutTestKit.Fonts(LayoutTestKit.Latin);

        ParagraphLayout layout = LayoutTestKit.Layout("abc\n", fonts, null);

        LaidOutLine line = Assert.Single(layout.Lines);
        Assert.Equal(4, line.Length);
        Assert.Equal(LayoutTestKit.Layout("abc", fonts, null).Width, line.Width, Tolerance);
    }

    [Fact]
    public void Line_metrics_come_from_the_fonts_on_the_line_and_the_spacing_scales_the_pitch()
    {
        ShapingFont[] fonts = LayoutTestKit.Fonts(LayoutTestKit.Latin, LayoutTestKit.Arabic);

        ParagraphLayout latin = LayoutTestKit.Layout("Quarterly report", fonts, null, lineSpacing: 1.5);
        ParagraphLayout arabic = LayoutTestKit.Layout(LayoutTestKit.Case("arabic-hello").Text, fonts, null);

        LaidOutLine line = latin.Lines[0];
        Assert.Equal(fonts[0].Font.Ascender * MetricScale(fonts[0]), line.Ascent, Tolerance);
        Assert.Equal(-fonts[0].Font.Descender * MetricScale(fonts[0]), line.Descent, Tolerance);
        Assert.Equal((line.Ascent + line.Descent) * 1.5, line.Pitch, Tolerance);
        Assert.Equal(line.Pitch, latin.Height, Tolerance);

        // A line in the Arabic font is as tall as that font says, not as the first font of the list says.
        Assert.Equal(fonts[1].Font.Ascender * MetricScale(fonts[1]), arabic.Lines[0].Ascent, Tolerance);
        Assert.NotEqual(latin.Lines[0].Ascent, arabic.Lines[0].Ascent);
    }

    [Theory]
    [MemberData(nameof(MixedDirectionCases))]
    public void The_runs_of_a_line_stand_in_the_order_the_bidi_algorithm_gives_its_characters(string id)
    {
        CorpusTests.Case c = LayoutTestKit.Case(id);
        ShapingFont[] fonts = LayoutTestKit.Fonts(c.Fonts);

        // The characters' order, worked out here per code point (rule L1 for the line, then L2), not per run as the layouter does.
        int[] codePoints = UnicodeText.CodePoints(c.Text, out int[] utf16Index);
        BidiResult bidi = UnicodeBidi.Resolve(codePoints);

        foreach (int? width in new int?[] { null, 150, 100 })
        {
            ParagraphLayout layout = LayoutTestKit.Layout(c.Text, fonts, width);
            foreach (LaidOutLine line in layout.Lines)
            {
                int first = Array.IndexOf(utf16Index, line.Start);
                int end = Array.IndexOf(utf16Index, line.Start + line.Length);
                byte[] levels = UnicodeBidi.LineLevels(bidi, first, end);
                int[] visual = UnicodeBidi.VisualOrder(levels, 0, levels.Length);
                int[] rank = new int[levels.Length];
                for (int position = 0; position < visual.Length; position++)
                {
                    rank[visual[position]] = position;
                }

                PlacedRun[] byRank = [.. line.Runs.OrderBy(p => Enumerable.Range(first, end - first)
                    .Where(i => utf16Index[i] >= p.Run.Utf16Start && utf16Index[i] < p.Run.Utf16Start + p.Run.Utf16Length)
                    .Min(i => rank[i - first]))];

                // In that order each run starts where the one before it ends.
                for (int r = 1; r < byRank.Length; r++)
                {
                    Assert.Equal(byRank[r - 1].X + byRank[r - 1].Width, byRank[r].X, Tolerance);
                }

                // The levels the runs carry are the line's levels of their characters.
                foreach (PlacedRun placed in byRank)
                {
                    int cp = Array.IndexOf(utf16Index, placed.Run.Utf16Start);
                    Assert.Equal(levels[cp - first], placed.Run.Level);
                }
            }
        }
    }

    [Fact]
    public void A_run_is_cut_only_where_the_line_is_and_keeps_the_glyphs_of_its_own_clusters()
    {
        ShapingFont[] fonts = LayoutTestKit.Fonts(LayoutTestKit.Latin);

        ParagraphLayout layout = LayoutTestKit.Layout(LatinSentence, fonts, 120);

        ShapedParagraph whole = TextShaper.Shape(LatinSentence, fonts);
        Assert.True(layout.LineCount > 3);
        // Latin is one run of one level: every glyph of the paragraph is on exactly one line, in the order it was shaped.
        ShapedGlyph[] shaped = [.. whole.Runs.SelectMany(r => r.Glyphs)];
        ShapedGlyph[] placed = [.. layout.Lines.SelectMany(l => l.Runs).SelectMany(r => r.Run.Glyphs)];
        Assert.Equal(shaped, placed);
        foreach (LaidOutLine line in layout.Lines)
        {
            Assert.All(line.Runs.SelectMany(r => r.Run.Glyphs), g => Assert.InRange(g.Cluster, line.Start, line.Start + line.Length - 1));
        }
    }

    [Fact]
    public void Laying_the_same_input_out_twice_gives_equal_lines_and_runs()
    {
        foreach (string id in new[] { "latin-de", "mixed-en-in-ar", "mixed-ar-he-en", "devanagari-sentence" })
        {
            CorpusTests.Case c = LayoutTestKit.Case(id);
            ShapingFont[] fonts = LayoutTestKit.Fonts(c.Fonts);

            ParagraphLayout first = LayoutTestKit.Layout(c.Text, fonts, 100, TextAlignment.Center);
            ParagraphLayout second = LayoutTestKit.Layout(c.Text, fonts, 100, TextAlignment.Center);

            Assert.Equal(first.LineCount, second.LineCount);
            Assert.Equal(first.Width, second.Width);
            Assert.Equal(first.Height, second.Height);
            for (int i = 0; i < first.LineCount; i++)
            {
                LaidOutLine a = first.Lines[i];
                LaidOutLine b = second.Lines[i];
                Assert.Equal((a.Start, a.Length, a.Width, a.X, a.Ascent, a.Descent, a.Pitch), (b.Start, b.Length, b.Width, b.X, b.Ascent, b.Descent, b.Pitch));
                Assert.Equal(a.Runs.Count, b.Runs.Count);
                for (int r = 0; r < a.Runs.Count; r++)
                {
                    PlacedRun x = a.Runs[r];
                    PlacedRun y = b.Runs[r];
                    Assert.Equal((x.X, x.Width, x.IsTrailingSpace), (y.X, y.Width, y.IsTrailingSpace));
                    Assert.Equal((x.Run.Utf16Start, x.Run.Utf16Length, x.Run.Level, x.Run.Script, x.Run.FontIndex), (y.Run.Utf16Start, y.Run.Utf16Length, y.Run.Level, y.Run.Script, y.Run.FontIndex));
                    Assert.Equal(x.Run.Glyphs, y.Run.Glyphs);
                }
            }
        }
    }

    [Fact]
    public void Drawing_text_other_than_the_text_that_was_laid_out_is_refused()
    {
        ShapingFont[] fonts = LayoutTestKit.Fonts(LayoutTestKit.Latin);
        ParagraphLayout layout = LayoutTestKit.Layout("abc", fonts, null);

        Assert.Throws<ArgumentException>(() => LayoutTestKit.Draw(layout, "abd", fonts));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    public void A_size_that_is_not_positive_is_refused(double size)
    {
        ShapingFont[] fonts = LayoutTestKit.Fonts(LayoutTestKit.Latin);

        Assert.Throws<ArgumentOutOfRangeException>(() => LayoutTestKit.Layout("abc", fonts, null, size: size));
    }
}
