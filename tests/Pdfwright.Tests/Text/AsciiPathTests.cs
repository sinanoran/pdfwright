using System.Globalization;
using System.Text;
using Pdfwright.Tests.Document;
using Pdfwright.Text;
using Pdfwright.Unicode;

namespace Pdfwright.Tests.Text;

/// <summary>
/// What the simple path (printable ASCII, left to right) does beside the general code, with <see cref="TextShaper.FastPaths"/> off and on: the font of each character chosen without the loop over clusters when the first font of a style covers
/// what the text uses, the styles filled span by span, the line breaker and the grapheme breaker working from tables. The result must be the same, glyph by glyph, line by line and byte by byte, whichever fonts lack which glyphs.
/// </summary>
[Collection("ShapingFastPaths")]
public class AsciiPathTests
{
    private static readonly string[] Prose =
    [
        "Revenue grew over the year, led by the northern region, while costs stayed flat. The figures in this report are made up for a performance measurement.",
        "Customers renewed at a higher rate than in the previous year, and the support team closed most tickets within a day. (See the notes.)",
        "Orders, returns and refunds: $1,234.50 (12.5%) at 3/4 of -7.25; \"quoted\" and 'single' and [bracketed] {braced} <angled> text - dashes -- and ... dots.",
        "Row 12 cell 3 text", "Col 1", "a", " ", "  two  spaces  ", "x\ty", "tab\t\tbetween", "1234567890", "()", "\"\"", "a-b-c", "ABC DEF", "http://example.com/a/b?c=d&e=f#g",
    ];

    private static string Describe(ShapedParagraph p)
    {
        StringBuilder sb = new();
        sb.Append(p.CodePointCount).Append('|').Append(p.ParagraphLevel).Append('|').Append(string.Join(',', p.Missing)).Append('|');
        sb.Append(string.Join(',', p.CodePoints.Take(p.CodePointCount))).Append('|').Append(string.Join(',', p.Utf16Index.Take(p.CodePointCount + 1))).Append('|');
        sb.Append(string.Join(',', p.Bidi.Levels.Take(p.CodePointCount))).Append('|').Append(string.Join(',', p.Bidi.Classes.Take(p.CodePointCount))).Append('|');
        foreach (ShapedRun r in p.Runs)
        {
            sb.Append(CultureInfo.InvariantCulture, $"[{r.Utf16Start},{r.Utf16Length},{r.Level},{r.Script},{r.FontIndex},{r.Style}:{string.Join(";", r.Glyphs.Select(g => $"{g.Glyph}/{g.Cluster}/{g.XAdvance}/{g.XOffset}/{g.YOffset}"))}]");
        }

        return sb.ToString();
    }

    private static string Describe(ParagraphLayout layout) =>
        string.Concat(layout.Lines.Select(l =>
            FormattableString.Invariant($"<{l.Start},{l.Length},{l.Width:R},{l.X:R},{l.Pitch:R},{l.Ascent:R},{l.Descent:R}")
            + string.Concat(l.Runs.Select(r => FormattableString.Invariant($"({r.Run.Utf16Start},{r.Run.Utf16Length},{r.Run.FontIndex},{r.Run.Style},{r.X:R},{r.Width:R},{r.IsTrailingSpace},{string.Join(';', r.Run.Glyphs.Select(g => g.Glyph))})")))
            + ">"))
        + "|" + string.Join(',', layout.Missing) + "|" + string.Join(',', layout.Styles.Select(s => FormattableString.Invariant($"{s.Size:R}/{string.Join('.', s.FontOrder)}")));

    /// <summary>The result of <paramref name="make"/> with the fast paths off, then on; they must be the same.</summary>
    private static void Same(Func<string> make, string what)
    {
        string general;
        TextShaper.FastPaths = false;
        try
        {
            general = make();
        }
        finally
        {
            TextShaper.FastPaths = true;
        }

        Assert.Equal(general, make());
        Assert.Equal(general, make());
    }

    private static ShapingFont[][] Orders() =>
    [
        LayoutTestKit.Fonts(LayoutTestKit.Latin),
        LayoutTestKit.Fonts(LayoutTestKit.Latin, LayoutTestKit.Arabic, LayoutTestKit.Hebrew),
        LayoutTestKit.Fonts(LayoutTestKit.Arabic, LayoutTestKit.Latin),
        LayoutTestKit.Fonts(LayoutTestKit.Hebrew, LayoutTestKit.Arabic, LayoutTestKit.Latin),
        LayoutTestKit.Fonts(LayoutTestKit.Arabic),
    ];

    private static string RandomAscii(Random random, int maxLength, string? alphabet = null)
    {
        StringBuilder text = new();
        int length = random.Next(0, maxLength + 1);
        for (int k = 0; k < length; k++)
        {
            text.Append(random.Next(14) == 0 ? '\t' : alphabet is null ? (char)random.Next(' ', '~' + 1) : alphabet[random.Next(alphabet.Length)]);
        }

        return text.ToString();
    }

    /// <summary>Spans that tile <paramref name="length"/> characters (some of them empty), with a style from 0 to <paramref name="styles"/> - 1 for each.</summary>
    private static StyledSpan[] RandomSpans(Random random, int length, int styles)
    {
        List<StyledSpan> spans = [];
        int at = 0;
        while (at < length || spans.Count == 0)
        {
            int n = random.Next(4) == 0 ? 0 : random.Next(1, Math.Max(2, length / 2 + 1));
            n = Math.Min(n, length - at);
            spans.Add(new StyledSpan(at, n, random.Next(styles)));
            at += n;
        }

        return [.. spans];
    }

    private static ShapeStyling Styling(StyledSpan[] spans, int[][] orders, int[] keys) =>
        new() { Spans = spans, FontOrders = orders, ShapeKeys = keys, Languages = [.. orders.Select((_, i) => i % 2 == 1 ? "en" : (string?)null)] };

    [Fact]
    public void Shaping_with_one_style_gives_the_same_fonts_glyphs_and_missing_list_with_and_without_the_fast_paths()
    {
        Random random = new(11);
        foreach (ShapingFont[] fonts in Orders())
        {
            List<string> texts = [.. Prose, "12 345", "1,2.3-4", "(1) (2)", "a", "ا", "1 ا", string.Empty];
            for (int i = 0; i < 150; i++)
            {
                texts.Add(RandomAscii(random, 40, i % 3 == 0 ? "0123456789 .,-()$%" : null));
            }

            foreach (string text in texts)
            {
                foreach (int level in new[] { -1, 0 })
                {
                    Same(() => Describe(TextShaper.Shape(text.Replace('\t', ' '), fonts, level, null, text)), text);
                }
            }
        }
    }

    [Fact]
    public void Shaping_with_styles_that_change_inside_the_text_gives_the_same_fonts_glyphs_and_missing_list()
    {
        Random random = new(12);
        ShapingFont[] fonts = LayoutTestKit.Fonts(LayoutTestKit.Latin, LayoutTestKit.Arabic, LayoutTestKit.Hebrew);

        // Orders that begin with a font that has every ASCII glyph, with one that lacks the letters, with one that lacks some, in groups that agree on their first font and groups that do not.
        int[][] orders = [[0, 1, 2], [1, 0, 2], [2, 1, 0], [0], [1], [0, 2], [1, 2, 0]];
        for (int i = 0; i < 1500; i++)
        {
            string text = RandomAscii(random, 40, i % 3 == 0 ? "0123456789 .,-()$%" : null);
            string shaped = text.Replace('\t', ' ');
            int styles = random.Next(1, 5);
            int[][] used = [.. Enumerable.Range(0, styles).Select(_ => orders[random.Next(orders.Length)])];
            int[] keys = [.. Enumerable.Range(0, styles).Select(_ => random.Next(3))];
            ShapeStyling styling = Styling(RandomSpans(random, shaped.Length, styles), used, keys);
            if (shaped.Length == 0)
            {
                continue;
            }

            Same(() => Describe(TextShaper.Shape(shaped, fonts, 0, null, text, styling)), text);
        }
    }

    [Fact]
    public void Spans_that_overlap_are_read_as_they_were_and_spans_that_leave_a_gap_are_refused_as_ever()
    {
        ShapingFont[] fonts = LayoutTestKit.Fonts(LayoutTestKit.Latin);
        foreach (StyledSpan[] spans in new[]
        {
            new[] { new StyledSpan(0, 3, 0) },
            new[] { new StyledSpan(1, 5, 0) },
            new[] { new StyledSpan(0, 3, 0), new StyledSpan(4, 2, 0) },
            Array.Empty<StyledSpan>(),
        })
        {
            ShapeStyling styling = Styling(spans, [[0]], [0]);
            foreach (bool fast in new[] { false, true })
            {
                TextShaper.FastPaths = fast;
                try
                {
                    Assert.ThrowsAny<ArgumentException>(() => TextShaper.Shape("abcdef", fonts, 0, null, null, styling));
                }
                finally
                {
                    TextShaper.FastPaths = true;
                }
            }
        }

        // Spans that overlap are accepted by the general loop (each character takes the first span that reaches it), so the filling by spans leaves them to it, and the result is the same.
        ShapeStyling overlapping = Styling([new StyledSpan(0, 4, 0), new StyledSpan(3, 3, 1)], [[0], [0]], [0, 1]);
        Same(() => Describe(TextShaper.Shape("abcdef", fonts, 0, null, null, overlapping)), "overlap");

        // Spans whose lengths add up past int.MaxValue and wrap back to the length of the text: the sum must not wrap, so that they are left to the general loop as before.
        ShapeStyling wrapping = Styling([new StyledSpan(0, int.MaxValue, 1), new StyledSpan(int.MaxValue, int.MaxValue, 1), new StyledSpan(-2, 8, 1)], [[0], [0]], [0, 1]);
        Same(() => Describe(TextShaper.Shape("abcdef", fonts, 0, null, null, wrapping)), "wrapping");
    }

    [Fact]
    public void A_font_is_known_for_the_printable_ASCII_it_has_and_a_font_that_lacks_a_glyph_gives_way_to_the_next()
    {
        foreach (string file in new[] { LayoutTestKit.Latin, LayoutTestKit.Arabic, LayoutTestKit.Hebrew })
        {
            ShapingFont font = LayoutTestKit.Font(file);
            ShapingFont.AsciiCoverage coverage = font.PrintableAsciiCoverage;
            Assert.Same(coverage, font.PrintableAsciiCoverage);
            for (int c = ' '; c <= '~'; c++)
            {
                ulong low = c - ' ' < 64 ? 1UL << (c - ' ') : 0;
                ulong high = c - ' ' < 64 ? 0 : 1UL << (c - ' ' - 64);
                Assert.Equal(font.Font.GlyphForCodePoint(c) != 0, coverage.Covers(low, high));
            }
        }

        Assert.True(LayoutTestKit.Font(LayoutTestKit.Latin).PrintableAsciiCoverage.Covers(ulong.MaxValue, (1UL << 31) - 1), "The Latin font has every printable ASCII glyph.");

        // A letter that the first font lacks goes to the next font, and the characters around it stay where the general code puts them.
        ShapingFont[] fonts = LayoutTestKit.Fonts(LayoutTestKit.Arabic, LayoutTestKit.Latin);
        ShapedParagraph paragraph = TextShaper.Shape("12 ab 34", fonts);
        Assert.Contains(paragraph.Runs, r => r.FontIndex == 1);
        Same(() => Describe(TextShaper.Shape("12 ab 34", fonts)), "mixed");
    }

    [Fact]
    public void The_coverage_of_a_font_is_the_same_whichever_thread_asks_first()
    {
        ShapingFont first = new(LayoutTestKit.Font(LayoutTestKit.Latin).Font);
        ShapingFont other = new(LayoutTestKit.Font(LayoutTestKit.Arabic).Font);
        try
        {
            ShapingFont.AsciiCoverage[] seen = new ShapingFont.AsciiCoverage[64];
            Parallel.For(0, seen.Length, new ParallelOptions { MaxDegreeOfParallelism = 16 }, i => seen[i] = (i % 2 == 0 ? first : other).PrintableAsciiCoverage);
            for (int i = 0; i < seen.Length; i += 2)
            {
                Assert.Same(seen[0], seen[i]);
                Assert.Same(seen[1], seen[i + 1]);
            }

            Assert.Equal(LayoutTestKit.Font(LayoutTestKit.Latin).PrintableAsciiCoverage.Low, seen[0].Low);
            Assert.Equal(LayoutTestKit.Font(LayoutTestKit.Arabic).PrintableAsciiCoverage.High, seen[1].High);
        }
        finally
        {
            first.Dispose();
            other.Dispose();
        }
    }

    [Fact]
    public void Layouts_of_the_cells_prose_and_random_ASCII_are_the_same_with_and_without_the_fast_paths()
    {
        Random random = new(13);
        ShapingFont[][] orders = Orders();
        List<string> texts = [.. Prose];
        for (int r = 0; r < 20; r++)
        {
            for (int c = 1; c <= 6; c++)
            {
                texts.Add($"Row {r} cell {c} text");
            }
        }

        for (int i = 0; i < 300; i++)
        {
            texts.Add(RandomAscii(random, 120, i % 4 == 0 ? "0123456789 .,-()$%/" : i % 4 == 1 ? "'\" ()ab" : null));
        }

        foreach (string text in texts)
        {
            ShapingFont[] fonts = orders[random.Next(orders.Length)];
            double? width = random.Next(3) == 0 ? null : 20 + random.Next(300);
            int level = random.Next(2) == 0 ? -1 : 0;
            Same(() => Describe(LayoutTestKit.Layout(text, fonts, width, paragraphLevel: level)), text);
        }
    }

    [Fact]
    public void Layouts_with_many_styles_and_fonts_that_lack_glyphs_are_the_same_with_and_without_the_fast_paths()
    {
        Random random = new(14);
        ShapingFont[] fonts = LayoutTestKit.Fonts(LayoutTestKit.Latin, LayoutTestKit.Arabic, LayoutTestKit.Hebrew);
        int[][] orders = [[0, 1, 2], [1, 0, 2], [2, 1, 0], [0], [1], [0, 2]];
        for (int i = 0; i < 600; i++)
        {
            string text = RandomAscii(random, 80, i % 3 == 0 ? "0123456789 .,-()$%" : null);
            if (text.Length == 0)
            {
                continue;
            }

            int count = random.Next(1, 5);
            RunStyle[] styles =
            [
                .. Enumerable.Range(0, count).Select(_ => new RunStyle
                {
                    FontOrder = orders[random.Next(orders.Length)],
                    Size = 8 + random.Next(3) * 2,
                    Language = random.Next(3) == 0 ? "en" : null,
                }),
            ];
            ParagraphOptions options = new()
            {
                Size = 10,
                MaxWidth = random.Next(3) == 0 ? null : 30 + random.Next(250),
                Styles = styles,
                Spans = RandomSpans(random, text.Length, count),
            };
            Same(() => Describe(ParagraphLayouter.Layout(text, fonts, options)), text);
        }
    }

    [Fact]
    public void The_layouter_refuses_spans_whose_lengths_wrap_around_to_the_length_of_the_text()
    {
        ShapingFont[] fonts = LayoutTestKit.Fonts(LayoutTestKit.Latin);
        ParagraphOptions options = new()
        {
            Size = 10,
            Styles = [new RunStyle { FontOrder = [0], Size = 10 }],
            Spans = [new StyledSpan(0, int.MaxValue, 0), new StyledSpan(int.MaxValue, int.MaxValue, 0), new StyledSpan(-2, 8, 0)],
        };

        Assert.Throws<ArgumentException>(() => ParagraphLayouter.Layout("abcdef", fonts, options));
    }

    [Fact]
    public void A_table_and_a_mixed_document_are_the_same_bytes_with_and_without_the_fast_paths()
    {
        byte[] Table() => DocumentTestKit.Save(BuildTable());
        byte[] Mixed() => DocumentTestKit.Save(BuildMixed());
        foreach (Func<byte[]> build in new[] { Table, Mixed })
        {
            byte[] fast = build();
            byte[] general;
            TextShaper.FastPaths = false;
            try
            {
                general = build();
            }
            finally
            {
                TextShaper.FastPaths = true;
            }

            Assert.True(general.AsSpan().SequenceEqual(fast), "The document differs.");
        }
    }

    private static PdfDocument BuildTable()
    {
        PdfDocument document = new(DocumentTestKit.Options());
        document.Add(new Heading(1, "Report"));
        document.Add(new Table(
            Enumerable.Range(0, 6).Select(_ => new TableColumn(ColumnWidth.Fraction(1))),
            new TableRow(Enumerable.Range(1, 6).Select(c => $"Col {c}").ToArray()),
            Enumerable.Range(0, 300).Select(r => new TableRow(Enumerable.Range(1, 6).Select(c => $"Row {r} cell {c} text").ToArray())))
        {
            Borders = Borders.Horizontal,
            CellPadding = Margins.Pt(1, 3, 1, 3),
            Style = new TextStyle { Size = Length.Pt(9) },
        });
        return document;
    }

    private static PdfDocument BuildMixed()
    {
        PdfDocument document = new(DocumentTestKit.Options());
        document.Add(new Heading(1, "Annual report 2026"));
        for (int p = 0; p < 6; p++)
        {
            document.Add(new Paragraph($"Paragraph {p}. " + string.Join(' ', Prose)));
            document.Add(new Paragraph(
            [
                new TextRun("Plain, "),
                new TextRun("bold (12.5%) ", new TextStyle { Weight = 700 }),
                new TextRun("small $1,234.50 ", new TextStyle { Size = Length.Pt(7) }),
                new TextRun("and a tab\tinside, then ا Arabic and é accents", new TextStyle { Language = "en" }),
            ]));
        }

        document.Add(new Heading(2, "Invoices"));
        document.Add(new Table(
            [new TableColumn(ColumnWidth.Fraction(1)), new TableColumn(ColumnWidth.Fixed(Length.Mm(30))) { Alignment = TextAlignment.End }, new TableColumn()],
            new TableRow("Customer", "Amount", "Status"),
            Enumerable.Range(1, 60).Select(i => new TableRow($"Customer {i}", ((i * 37 % 1000) + 0.5m).ToString("N2", CultureInfo.InvariantCulture), i % 3 == 0 ? "Open" : "Paid"))));
        document.Add(new Paragraph("ارتفعت المبيعات بنسبة 12% خلال الربع. Latin words inside.") { Direction = TextDirection.RightToLeft });
        return document;
    }
}
