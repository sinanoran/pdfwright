using System.Text;
using Pdfwright.Fonts;
using Pdfwright.Tests.Document;
using Pdfwright.Text;

namespace Pdfwright.Tests.Text;

/// <summary>
/// Shaping word by word (the word cache of <see cref="ShapingFont"/>) gives exactly the glyphs of shaping the whole run, for every font that passes the check for the space glyph, and the
/// fonts that do not pass are shaped as before. Every text is shaped with <see cref="TextShaper.FastPaths"/> off (the general path: whole runs, no cache), then on, and compared glyph by glyph.
/// </summary>
[Collection("ShapingFastPaths")]
public class WordShapingTests
{
    private static readonly string[] AllFonts = DocumentTestKit.AllFonts;

    /// <summary>The fonts the check lets through. The others are refused: Noto Sans Italic (the space glyph is in the input set of GPOS lookup 2), Arabic (GPOS lookup 3, input) and Devanagari (GPOS lookup 3, before).</summary>
    private static readonly string[] Passing = ["NotoSans-Regular.ttf", "NotoSans-Bold.ttf", "NotoSansHebrew-Regular.ttf", "NotoSansBengali-Regular.ttf", "NotoSansTamil-Regular.ttf", "NotoSansThai-Regular.ttf"];

    private const int Auto = Pdfwright.Unicode.UnicodeBidi.AutoLevel;

    private static readonly string?[] Languages = [null, "en", "tr", "de"];

    private static ShapingFont Fresh(string file, IShaper? shaper = null)
    {
        TrueTypeFont font = TrueTypeFont.Parse(File.ReadAllBytes(TestPaths.Font(file)));
        return shaper is null ? new ShapingFont(font) : new ShapingFont(font, shaper);
    }

    /// <summary>A shaper that passes every call on to HarfBuzz and remembers the texts, to see which pieces a run was cut into.</summary>
    private sealed class CountingShaper : IShaper
    {
        internal readonly List<string> Texts = [];

        public ShaperFont CreateFont(byte[] fontBytes, int unitsPerEm) => HarfBuzzShaper.Instance.CreateFont(fontBytes, unitsPerEm);

        public ShapedGlyph[] Shape(ShaperFont font, string text, int start, int length, bool rightToLeft, string scriptTag, string? language)
        {
            lock (Texts)
            {
                Texts.Add(text.Substring(start, length));
            }

            return HarfBuzzShaper.Instance.Shape(font, text, start, length, rightToLeft, scriptTag, language);
        }
    }

    private static string D(ShapedGlyph[] glyphs) => string.Join(";", glyphs.Select(g => $"{g.Glyph}/{g.Cluster}/{g.XAdvance}/{g.XOffset}/{g.YOffset}"));

    private static string D(ShapedParagraph p)
    {
        StringBuilder sb = new();
        sb.Append(p.ParagraphLevel).Append('|').Append(string.Join(',', p.Missing)).Append('|').Append(string.Join(',', p.Bidi.Levels)).Append('|');
        foreach (ShapedRun r in p.Runs)
        {
            sb.Append($"[{r.Utf16Start},{r.Utf16Length},{r.Level},{r.Script},{r.FontIndex},{r.Style}:{D(r.Glyphs)}]");
        }

        return sb.ToString();
    }

    private static string D(ParagraphLayout l)
    {
        StringBuilder sb = new();
        foreach (LaidOutLine line in l.Lines)
        {
            sb.Append(System.FormattableString.Invariant($"<{line.Start},{line.Length},{line.Width:R},{line.X:R},{line.Ascent:R},{line.Descent:R},{line.Pitch:R}"));
            foreach (PlacedRun run in line.Runs)
            {
                sb.Append(System.FormattableString.Invariant($"({run.Run.Utf16Start},{run.Run.Utf16Length},{run.Run.FontIndex},{run.X:R},{run.Width:R},{run.IsTrailingSpace}:{D(run.Run.Glyphs)})"));
            }

            sb.Append('>');
        }

        return sb.ToString() + "|" + string.Join(',', l.Missing);
    }

    private static T WithoutFastPaths<T>(Func<T> f)
    {
        TextShaper.FastPaths = false;
        try
        {
            return f();
        }
        finally
        {
            TextShaper.FastPaths = true;
        }
    }

    /// <summary>Shapes the text the general way (whole runs, no cache) and the fast way (word by word) with the same font list and compares the runs, glyph by glyph.</summary>
    private static void Same(string text, ShapingFont[] fonts, string? language)
    {
        string general = WithoutFastPaths(() => D(TextShaper.Shape(text, fonts, Auto, language)));
        string fast = D(TextShaper.Shape(text, fonts, Auto, language));
        Assert.Equal(general, fast);
    }

    private static string RandomAscii(Random random)
    {
        int length = random.Next(0, 4) == 0 ? random.Next(0, 301) : random.Next(0, 60);
        StringBuilder text = new();
        int mode = random.Next(8);
        for (int i = 0; i < length; i++)
        {
            char c = mode switch
            {
                0 => random.Next(3) == 0 ? ' ' : (char)random.Next('!', '~' + 1),
                1 => random.Next(2) == 0 ? ' ' : (char)random.Next('0', '9' + 1),
                2 => random.Next(2) == 0 ? ' ' : "!\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~"[random.Next(32)],
                3 => random.Next(6) == 0 ? ' ' : (char)random.Next('a', 'z' + 1),
                4 => random.Next(10) == 0 ? ' ' : (char)random.Next('A', 'Z' + 1),
                5 => random.Next(2) == 0 ? ' ' : (char)random.Next(' ', '~' + 1),
                6 => i < length / 3 || i > 2 * length / 3 ? ' ' : (char)random.Next('a', 'z' + 1),
                _ => (char)random.Next(' ', '~' + 1),
            };
            text.Append(c);
        }

        return text.ToString();
    }

    [Fact]
    public void The_verdict_of_the_space_check_is_known_for_every_test_font()
    {
        foreach (string file in AllFonts)
        {
            using ShapingFont font = Fresh(file);
            string? refusal = font.Handle.WordShapingRefusal;
            Assert.True(Passing.Contains(file) == (refusal is null), $"{file}: {refusal ?? "passes"}");
            Assert.Equal(refusal is null, font.Handle.CanShapeWordByWord);
            Assert.Equal(refusal, font.Handle.WordShapingRefusal);
        }
    }

    [Fact]
    public void Random_printable_ASCII_is_shaped_word_by_word_as_it_is_whole()
    {
        foreach (string file in Passing)
        {
            ShapingFont[] fonts = [Fresh(file)];
            for (int pass = 0; pass < 2; pass++)
            {
                // The whole texts are dropped between the passes, so that the second pass reaches the word cache, now warm, and not the cache of whole texts.
                fonts[0].ClearWholeTextCache();
                Random random = new(20260401);
                for (int i = 0; i < 3000; i++)
                {
                    Same(RandomAscii(random), fonts, Languages[i % Languages.Length]);
                }
            }
        }
    }

    [Fact]
    public void Edge_shapes_of_text_are_shaped_word_by_word_as_they_are_whole()
    {
        string[] texts =
        [
            "", " ", "  ", "   ", "a", "a b", " a", "a ", " a ", "  a  b  ", "a  b", "a   b   c", "1", "12 34", "1,234.56 7.8", "...", "-- --", "()", "[ ]", "fi fl ffi ffl", "office difficult", "AV To Ty", "A V", "T. A.", "f i", "ff  fi",
            "Row 1 cell 1 text", "The quick brown fox jumps over the lazy dog.", new string('x', 64), new string('x', 65), new string('x', 63) + " " + new string('y', 64) + " " + new string('z', 65), new string(' ', 64), new string(' ', 65), new string(' ', 200) + "a",
            new string('w', 70) + " " + new string('w', 70), "a" + new string(' ', 70) + "b",
        ];
        foreach (string file in Passing)
        {
            ShapingFont[] fonts = [Fresh(file)];
            foreach (string? language in Languages)
            {
                foreach (string text in texts.Concat(texts))
                {
                    Same(text, fonts, language);
                }
            }
        }
    }

    [Fact]
    public void The_cells_of_the_table_benchmark_are_shaped_word_by_word_as_they_are_whole()
    {
        foreach (string file in new[] { "NotoSans-Regular.ttf", "NotoSans-Bold.ttf" })
        {
            ShapingFont[] fonts = [Fresh(file)];
            for (int r = 0; r < 1000; r++)
            {
                for (int c = 1; c <= 6; c++)
                {
                    Same($"Row {r} cell {c} text", fonts, "en");
                }
            }

            for (int c = 1; c <= 6; c++)
            {
                Same($"Col {c}", fonts, "en");
            }
        }
    }

    [Fact]
    public void English_prose_is_shaped_word_by_word_as_it_is_whole()
    {
        string prose =
            "Shaping is the step that turns characters into glyphs and places them. In Latin text it is mostly a lookup, with kerning between pairs such as AV, To and Ty, and ligatures such as fi and fl in some fonts. " +
            "A browser shapes word by word and keeps the words it has shaped, because most of the words of a page occur again and again: the, of, and, to, in. We do the same. (Numbers like 1,234.50 and 99% and dates like 2026-04-01 are words too.)";
        foreach (string file in Passing)
        {
            ShapingFont[] fonts = [Fresh(file)];
            foreach (string? language in Languages)
            {
                Same(prose, fonts, language);
                foreach (string sentence in prose.Split(". "))
                {
                    Same(sentence, fonts, language);
                }

                // Prefixes, so that a text ends at every kind of character.
                for (int n = 0; n <= prose.Length; n += 7)
                {
                    Same(prose[..n], fonts, language);
                }
            }
        }
    }

    [Fact]
    public void Words_are_cut_at_the_spaces_and_kept_in_the_word_cache()
    {
        CountingShaper counting = new();
        using ShapingFont font = Fresh("NotoSans-Regular.ttf", counting);
        Assert.True(font.CanShapeWordByWord);
        ShapingFont[] fonts = [font];
        TextShaper.Shape("Row 1 cell 2 text", fonts, Auto, "en");
        Assert.Equal(["Row", " ", "1", "cell", "2", "text"], counting.Texts);
        counting.Texts.Clear();

        // Another text of the same words: only the new word is shaped.
        TextShaper.Shape("Row 3 cell 2 text", fonts, Auto, "en");
        Assert.Equal(["3"], counting.Texts);
        counting.Texts.Clear();

        // The same text is a hit of the cache of whole texts.
        TextShaper.Shape("Row 3 cell 2 text", fonts, Auto, "en");
        Assert.Empty(counting.Texts);

        // Another language is another key, so its words are shaped again; a text of one word is shaped whole, as it was before.
        TextShaper.Shape("Row 3", fonts, Auto, "de");
        Assert.Equal(["Row", " ", "3"], counting.Texts);
        counting.Texts.Clear();
        TextShaper.Shape("Hello", fonts, Auto, "de");
        Assert.Equal(["Hello"], counting.Texts);
    }

    [Fact]
    public void A_word_of_more_than_64_characters_is_shaped_every_time_and_not_kept()
    {
        CountingShaper counting = new();
        using ShapingFont font = Fresh("NotoSans-Regular.ttf", counting);
        ShapingFont[] fonts = [font];
        string longWord = new('x', ShapingFont.WordCacheLength + 1);
        TextShaper.Shape("a " + longWord, fonts, Auto, null);
        counting.Texts.Clear();
        TextShaper.Shape("b " + longWord, fonts, Auto, null);
        Assert.Equal(["b", longWord], counting.Texts);
        counting.Texts.Clear();

        string word = new('x', ShapingFont.WordCacheLength);
        TextShaper.Shape("a " + word, fonts, Auto, null);
        counting.Texts.Clear();
        TextShaper.Shape("c " + word, fonts, Auto, null);
        Assert.Equal(["c"], counting.Texts);
    }

    [Fact]
    public void Without_the_fast_paths_the_text_is_shaped_whole_and_nothing_is_cached()
    {
        CountingShaper counting = new();
        using ShapingFont font = Fresh("NotoSans-Regular.ttf", counting);
        ShapingFont[] fonts = [font];
        WithoutFastPaths(() => TextShaper.Shape("Row 1 cell 2 text", fonts, Auto, "en"));
        Assert.Equal(["Row 1 cell 2 text"], counting.Texts);
        Assert.Equal(0, font.WordCacheCount);
    }

    [Fact]
    public void A_font_that_fails_the_check_is_shaped_as_before()
    {
        foreach (string file in AllFonts)
        {
            CountingShaper counting = new();
            using ShapingFont font = Fresh(file, counting);
            font.WordShapingOverride = false;
            ShapingFont[] fonts = [font];
            foreach (string text in new[] { "Row 1 cell 2 text", "a b", "Hello world  again", " x " })
            {
                counting.Texts.Clear();
                string fast = D(TextShaper.Shape(text, fonts, Auto, "en"));
                Assert.Equal([text], counting.Texts);
                Assert.Equal(WithoutFastPaths(() => D(TextShaper.Shape(text, fonts, Auto, "en"))), fast);
            }

            Assert.Equal(0, font.WordCacheCount);
        }

        // The bundled fonts that really fail the check are never cut into words either.
        foreach (string file in AllFonts.Except(Passing))
        {
            CountingShaper counting = new();
            using ShapingFont font = Fresh(file, counting);
            Assert.False(font.CanShapeWordByWord);
            TextShaper.Shape("a b c", [font], Auto, null);
            Assert.Equal(["a b c"], counting.Texts);
            Assert.Equal(0, font.WordCacheCount);
        }
    }

    [Fact]
    public void Right_to_left_and_other_scripts_are_not_cut_into_words_whatever_the_verdict()
    {
        CountingShaper counting = new();
        using ShapingFont font = Fresh("NotoSansArabic-Regular.ttf", counting);
        font.WordShapingOverride = true;
        TextShaper.Shape("مرحبا بالعالم", [font], Auto, null);
        Assert.Equal(["مرحبا بالعالم"], counting.Texts);
        Assert.Equal(0, font.WordCacheCount);
    }

    [Fact]
    public void Layouts_in_several_sizes_and_styles_are_the_same_word_by_word_as_whole()
    {
        ShapingFont[][] orders =
        [
            LayoutTestKit.Fonts("NotoSans-Regular.ttf"),
            LayoutTestKit.Fonts("NotoSans-Bold.ttf"),
            LayoutTestKit.Fonts("NotoSans-Italic.ttf"),
            LayoutTestKit.Fonts(LayoutTestKit.Arabic, "NotoSans-Regular.ttf"),
            LayoutTestKit.Fonts("NotoSans-Bold.ttf", "NotoSans-Italic.ttf", LayoutTestKit.Arabic),
        ];
        Random random = new(7);
        for (int i = 0; i < 600; i++)
        {
            string text = RandomAscii(random);
            ShapingFont[] fonts = orders[i % orders.Length];
            double size = new[] { 6.0, 8, 9.5, 10, 12, 14, 24, 40 }[random.Next(8)];
            double? width = random.Next(3) == 0 ? null : 40 + random.Next(300);
            string general = WithoutFastPaths(() => D(LayoutTestKit.Layout(text, fonts, width, size: size)));
            string fast = D(LayoutTestKit.Layout(text, fonts, width, size: size));
            string again = D(LayoutTestKit.Layout(text, fonts, width, size: size));
            Assert.Equal(general, fast);
            Assert.Equal(general, again);
        }
    }

    [Fact]
    public void Text_that_changes_font_inside_a_run_list_is_the_same_word_by_word_as_whole()
    {
        // The digits and spaces at the start of a text may take the first font of the list while the letters take the second: the runs are parts of the text, shaped from the middle of it.
        ShapingFont[][] orders =
        [
            [Fresh("NotoSansArabic-Regular.ttf"), Fresh("NotoSans-Regular.ttf")],
            [Fresh("NotoSansHebrew-Regular.ttf"), Fresh("NotoSansThai-Regular.ttf"), Fresh("NotoSans-Bold.ttf")],
            [Fresh("NotoSansDevanagari-Regular.ttf"), Fresh("NotoSans-Italic.ttf")],
        ];
        Random random = new(11);
        foreach (ShapingFont[] fonts in orders)
        {
            for (int i = 0; i < 1500; i++)
            {
                Same(RandomAscii(random), fonts, Languages[i % Languages.Length]);
            }
        }
    }

    [Fact]
    public void Many_threads_get_the_glyphs_that_one_thread_gets()
    {
        string[] vocabulary = [.. Enumerable.Range(0, 3000).Select(i => "w" + (i * 7919 % 10007))];
        using ShapingFont shared = Fresh("NotoSans-Regular.ttf");
        ShapingFont[] fonts = [shared];
        string[] texts = [.. Enumerable.Range(0, 20000).Select(i => string.Join(' ', Enumerable.Range(0, 1 + i % 9).Select(k => vocabulary[(i * 31 + k * 977) % vocabulary.Length])))];
        string[] serial = WithoutFastPaths(() => texts.Select(t => D(TextShaper.Shape(t, fonts, Auto, "en"))).ToArray());
        string[] parallel = new string[texts.Length];
        Parallel.For(0, texts.Length, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i => parallel[i] = D(TextShaper.Shape(texts[i], fonts, Auto, "en")));
        Assert.Equal(serial, parallel);
        // The bound is approximate under threads (a few words may be added while the tables are emptied).
        Assert.InRange(shared.WordCacheCount, 1, ShapingFont.WordCacheCapacity + 64);
    }

    [Fact]
    public void More_words_than_the_cache_holds_are_still_shaped_right()
    {
        using ShapingFont font = Fresh("NotoSans-Regular.ttf");
        ShapingFont[] fonts = [font];
        string[] texts = [.. Enumerable.Range(0, 10000).Select(i => $"alpha{i} beta{i * 3 + 1}")];
        string[] general = WithoutFastPaths(() => texts.Select(t => D(TextShaper.Shape(t, fonts, Auto, null))).ToArray());
        for (int round = 0; round < 2; round++)
        {
            string[] fast = [.. texts.Select(t => D(TextShaper.Shape(t, fonts, Auto, null)))];
            Assert.Equal(general, fast);
            Assert.InRange(font.WordCacheCount, 1, ShapingFont.WordCacheCapacity);
        }
    }

    [Fact]
    public void The_tables_of_the_languages_are_bounded_and_dropped_when_the_cache_is_cleared()
    {
        using ShapingFont font = Fresh("NotoSans-Regular.ttf");
        ShapingFont[] fonts = [font];
        for (int i = 0; i < 100; i++)
        {
            string text = "alpha beta";
            string language = $"en-x-{i}";
            Assert.Equal(WithoutFastPaths(() => D(TextShaper.Shape(text, fonts, Auto, language))), D(TextShaper.Shape(text, fonts, Auto, language)));
        }

        Assert.Equal(ShapingFont.WordTableLimit, font.WordTableCount);

        // More words than the cache holds clear it, and the tables of the languages go with the words.
        for (int i = 0; i < 5000; i++)
        {
            TextShaper.Shape($"w{i} x", fonts, Auto, "en-x-0");
        }

        Assert.InRange(font.WordTableCount, 0, 2);
        Assert.InRange(font.WordCacheCount, 1, ShapingFont.WordCacheCapacity);
    }

    [Fact]
    public void Whole_texts_in_more_languages_than_the_cache_keeps_tables_for_are_shaped_as_without_the_cache()
    {
        // The cache of whole texts keeps one table for each direction, script and language and is emptied when it would hold more tables than the word cache may: every text, cached or not, is shaped as the general code shapes it.
        using ShapingFont font = Fresh("NotoSans-Regular.ttf");
        ShapingFont[] fonts = [font];
        for (int round = 0; round < 3; round++)
        {
            for (int i = 0; i < ShapingFont.WordTableLimit * 2; i++)
            {
                string language = $"en-x-{i % (ShapingFont.WordTableLimit + 7)}";
                foreach (string text in new[] { "Row 1", "alpha beta", "x", "مرحبا" })
                {
                    Assert.Equal(WithoutFastPaths(() => D(TextShaper.Shape(text, fonts, Auto, language))), D(TextShaper.Shape(text, fonts, Auto, language)));
                }
            }
        }
    }

    private static PdfDocument Table(FontCollection fonts, int rows)
    {
        PdfDocument document = new(new PdfDocumentOptions
        {
            Fonts = fonts,
            DefaultStyle = new TextStyle { Family = "Noto Sans", Size = Length.Pt(10), Language = "en" },
            PageSize = PageSize.A4,
            Margins = Margins.Mm(top: 20, right: 8, bottom: 20, left: 8),
            Title = "Report",
            Language = "en",
            Deterministic = true,
            Header = page => new Paragraph("Report") { Style = new TextStyle { Size = Length.Pt(8) } },
            Footer = page => new Paragraph($"Page {page.Number} of {page.Count}") { Alignment = TextAlignment.End, Style = new TextStyle { Size = Length.Pt(8) } },
        });
        document.Add(new Heading(1, "Report"));
        document.Add(new Table(
            Enumerable.Range(0, 6).Select(_ => new TableColumn(ColumnWidth.Fraction(1))),
            new TableRow(Enumerable.Range(1, 6).Select(c => $"Col {c}").ToArray()),
            Enumerable.Range(0, rows).Select(r => new TableRow(Enumerable.Range(1, 6).Select(c => $"Row {r} cell {c} text").ToArray())))
        {
            Borders = Borders.Horizontal,
            CellPadding = Margins.Pt(1, 3, 1, 3),
            Style = new TextStyle { Size = Length.Pt(9) },
        });
        return document;
    }

    private static PdfDocument Mixed(FontCollection fonts)
    {
        PdfDocument document = new(new PdfDocumentOptions { Fonts = fonts, DefaultStyle = new TextStyle { Family = "Noto Sans", Size = Length.Pt(11), Language = "en" }, PageSize = PageSize.A4, Deterministic = true });
        document.Add(new Heading(1, "Word by word: AV To Ty fi fl"));
        for (int i = 0; i < 12; i++)
        {
            document.Add(new Paragraph(
            [
                new TextRun($"Paragraph {i} starts plain, then "),
                new TextRun("goes bold and wide with spaces  and  more", new TextStyle { Weight = 700 }),
                new TextRun(" then italic, the numbers 1,234.50 and (99%)", new TextStyle { Italic = true, Size = Length.Pt(13) }),
                new TextRun(" and a long word " + new string('x', 70) + " and an Arabic one: مرحبا بالعالم, and Hebrew שלום."),
            ]));
        }

        document.Add(new Table(
            [new TableColumn(ColumnWidth.Fraction(1)), new TableColumn(ColumnWidth.Fixed(Length.Mm(30))) { Alignment = TextAlignment.End }, new TableColumn()],
            new TableRow("Customer", "Amount", "Status"),
            Enumerable.Range(1, 80).Select(i => new TableRow($"Customer {i}", ((i * 37 % 1000) + 0.5m).ToString("N2", System.Globalization.CultureInfo.InvariantCulture), i % 3 == 0 ? "Open" : "Paid"))));
        return document;
    }

    private static void SameBytes(Func<FontCollection, PdfDocument> build)
    {
        string[] files = ["NotoSans-Regular.ttf", "NotoSans-Bold.ttf", "NotoSans-Italic.ttf", "NotoSansArabic-Regular.ttf", "NotoSansHebrew-Regular.ttf"];
        byte[] general = WithoutFastPaths(() => DocumentTestKit.Save(build(DocumentTestKit.Collection(files))));
        FontCollection shared = DocumentTestKit.Collection(files);
        byte[] fast = DocumentTestKit.Save(build(shared));
        byte[] warm = DocumentTestKit.Save(build(shared));
        int at = general.AsSpan().CommonPrefixLength(fast);
        Assert.True(general.AsSpan().SequenceEqual(fast), $"differs at {at} of {general.Length}/{fast.Length}: [{System.Text.Encoding.Latin1.GetString(general, Math.Max(0, at - 80), Math.Min(160, general.Length - Math.Max(0, at - 80)))}] vs [{System.Text.Encoding.Latin1.GetString(fast, Math.Max(0, at - 80), Math.Min(160, fast.Length - Math.Max(0, at - 80)))}]");
        Assert.True(general.AsSpan().SequenceEqual(warm), "the second document, out of the caches, differs");
    }

    [Fact]
    public void The_table_of_the_benchmark_is_the_same_bytes_with_and_without_the_fast_paths() => SameBytes(fonts => Table(fonts, 1000));

    [Fact]
    public void A_mixed_document_is_the_same_bytes_with_and_without_the_fast_paths() => SameBytes(Mixed);
}
