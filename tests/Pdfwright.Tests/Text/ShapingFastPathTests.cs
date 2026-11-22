using System.Text;
using Pdfwright.Tests.Conformance;
using Pdfwright.Tests.Document;
using Pdfwright.Text;

namespace Pdfwright.Tests.Text;

/// <summary>
/// The faster paths of the shaper (the simple left-to-right path, the cache of shaped runs) give exactly what the general code gives: the same runs, glyphs and positions, the same
/// lines, and the same bytes for the page corpus. Each text is shaped with <see cref="TextShaper.FastPaths"/> off, then on twice (the second time out of the cache) and compared.
/// </summary>
[Collection("ShapingFastPaths")]
public class ShapingFastPathTests
{
    private static readonly string[] Pieces =
    [
        "hello", "World", "Row 12 cell 3 text", "price: $9.99", "(a [b] {c})", "x\ty", " ", "  ", "-", "1,234.5", "é", "straße", "İstanbul", "ıi",
        "مرحبا", "العربية", "ـ", "123", "שלום", "עברית", "ִ", "‏", "‍", "😀", "日本語", "नमस्ते", "สวัสดี", "ก็", "̇", "—", "“q”", "­", "Á̂",
    ];

    private static string Random(Random random, int words)
    {
        StringBuilder text = new();
        for (int i = 0; i < words; i++)
        {
            text.Append(Pieces[random.Next(Pieces.Length)]);
            if (random.Next(3) > 0)
            {
                text.Append(' ');
            }
        }

        return text.ToString();
    }

    private static string Describe(ShapedParagraph p)
    {
        StringBuilder sb = new();
        sb.Append(p.ParagraphLevel).Append('|').Append(string.Join(',', p.Missing)).Append('|').Append(string.Join(',', p.CodePoints)).Append('|').Append(string.Join(',', p.Utf16Index)).Append('|');
        sb.Append(string.Join(',', p.Bidi.Levels)).Append('|').Append(string.Join(',', p.Bidi.Classes)).Append('|').Append(p.Bidi.ParagraphLevel).Append('|');
        foreach (ShapedRun r in p.Runs)
        {
            sb.Append($"[{r.Utf16Start},{r.Utf16Length},{r.Level},{r.Script},{r.FontIndex},{r.Style}:");
            foreach (ShapedGlyph g in r.Glyphs)
            {
                sb.Append($"{g.Glyph}/{g.Cluster}/{g.XAdvance}/{g.XOffset}/{g.YOffset};");
            }

            sb.Append(']');
        }

        return sb.ToString();
    }

    private static string Describe(ParagraphLayout l)
    {
        StringBuilder sb = new();
        foreach (LaidOutLine line in l.Lines)
        {
            sb.Append(System.FormattableString.Invariant($"<{line.Start},{line.Length},{line.Width:R},{line.X:R},{line.Ascent:R},{line.Descent:R},{line.Pitch:R}"));
            foreach (PlacedRun run in line.Runs)
            {
                sb.Append(System.FormattableString.Invariant($"({run.Run.Utf16Start},{run.Run.Utf16Length},{run.Run.FontIndex},{run.X:R},{run.Width:R},{run.IsTrailingSpace})"));
            }

            sb.Append('>');
        }

        return sb.ToString() + "|" + string.Join(',', l.Missing);
    }

    private static void Same(string text, ShapingFont[] fonts, int level, double? width)
    {
        string general, fast, cached, generalLayout, fastLayout, cachedLayout;
        TextShaper.FastPaths = false;
        try
        {
            general = Describe(TextShaper.Shape(text.Replace('\t', ' '), fonts, level, null, text));
            generalLayout = Describe(LayoutTestKit.Layout(text, fonts, width, paragraphLevel: level));
        }
        finally
        {
            TextShaper.FastPaths = true;
        }

        fast = Describe(TextShaper.Shape(text.Replace('\t', ' '), fonts, level, null, text));
        fastLayout = Describe(LayoutTestKit.Layout(text, fonts, width, paragraphLevel: level));
        cached = Describe(TextShaper.Shape(text.Replace('\t', ' '), fonts, level, null, text));
        cachedLayout = Describe(LayoutTestKit.Layout(text, fonts, width, paragraphLevel: level));
        Assert.Equal(general, fast);
        Assert.Equal(general, cached);
        Assert.Equal(generalLayout, fastLayout);
        Assert.Equal(generalLayout, cachedLayout);
    }

    [Fact]
    public void Every_text_of_the_corpus_is_shaped_and_laid_out_the_same_with_and_without_the_fast_paths()
    {
        foreach (CorpusTests.Case c in CorpusTests.Load())
        {
            ShapingFont[] fonts = LayoutTestKit.Fonts(c.Fonts);
            foreach (int level in new[] { -1, 0, 1 })
            {
                Same(c.Text, fonts, level, null);
                Same(c.Text, fonts, level, 120);
            }
        }
    }

    [Fact]
    public void Random_mixed_script_texts_are_shaped_and_laid_out_the_same()
    {
        ShapingFont[][] orders =
        [
            LayoutTestKit.Fonts(LayoutTestKit.Latin, LayoutTestKit.Arabic, LayoutTestKit.Hebrew),
            LayoutTestKit.Fonts(LayoutTestKit.Hebrew, LayoutTestKit.Latin, LayoutTestKit.Arabic),
            LayoutTestKit.Fonts(LayoutTestKit.Latin),
        ];
        Random random = new(20260101);
        for (int i = 0; i < 400; i++)
        {
            string text = Random(random, 1 + random.Next(14));
            Same(text, orders[random.Next(orders.Length)], new[] { -1, 0, 1 }[random.Next(3)], random.Next(3) == 0 ? null : 40 + random.Next(300));
        }

        // Plain ASCII, the shape the simple path is for, including tabs, digits only and punctuation only.
        for (int i = 0; i < 400; i++)
        {
            StringBuilder text = new();
            int length = random.Next(0, 40);
            for (int k = 0; k < length; k++)
            {
                text.Append(random.Next(12) == 0 ? '\t' : (char)random.Next(' ', '~' + 1));
            }

            Same(text.ToString(), orders[random.Next(orders.Length)], new[] { -1, 0, 1 }[random.Next(3)], random.Next(3) == 0 ? null : 30 + random.Next(200));
        }
    }

    [Fact]
    public void The_pages_of_the_corpus_are_the_same_bytes_with_and_without_the_fast_paths()
    {
        foreach (PageCorpus corpus in PageCorpus.LoadAll())
        {
            foreach (bool isLong in new[] { false, true })
            {
                byte[] fast = DocumentTestKit.Save(corpus.Build(isLong).Document);
                byte[] general;
                TextShaper.FastPaths = false;
                try
                {
                    general = DocumentTestKit.Save(corpus.Build(isLong).Document);
                }
                finally
                {
                    TextShaper.FastPaths = true;
                }

                Assert.True(general.AsSpan().SequenceEqual(fast), $"{corpus.Group} ({(isLong ? "long" : "short")}) differs");
            }
        }
    }
}
