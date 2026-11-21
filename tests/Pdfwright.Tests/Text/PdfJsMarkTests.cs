using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Pdfwright.Pdf;
using Pdfwright.Text;

namespace Pdfwright.Tests.Text;

/// <summary>
/// What the content stream does so that pdf.js reads the logical order (see the remarks of <see cref="GlyphRunWriter"/>): a glyph whose text holds a
/// non-spacing mark or ends with a format character is shown under a font size of its own, and a text that ends with a format character is given the joiner.
/// </summary>
public static partial class PdfJsMarkTests
{
    private const double RunSize = 14.0;

    [GeneratedRegex(@"<(?<code>[0-9A-F]{4})>\s*<(?<text>[0-9A-F]+)>")]
    private static partial Regex ToUnicodeEntry();

    [GeneratedRegex(@"(?<tf>/(?<font>\S+) (?<size>-?[\d.]+) Tf)|(?<tm>(?<a>-?[\d.]+) -?[\d.]+ -?[\d.]+ -?[\d.]+ -?[\d.]+ -?[\d.]+ Tm)|<(?<shown>[0-9A-F]{4})> Tj")]
    private static partial Regex ContentOperator();

    [GeneratedRegex(@"(?<num>\d+) 0 obj(?<body>.*?)endobj", RegexOptions.Singleline)]
    private static partial Regex IndirectObject();

    [GeneratedRegex(@"/(?<name>F\d+) (?<num>\d+) 0 R")]
    private static partial Regex FontResource();

    [GeneratedRegex(@"/ToUnicode (?<num>\d+) 0 R")]
    private static partial Regex ToUnicodeReference();

    /// <summary>The text of every code of each embedded font, from its <c>ToUnicode</c> map, by the font's resource name.</summary>
    private static Dictionary<string, Dictionary<string, string>> ToUnicodeByFont(string pdf)
    {
        Dictionary<string, string> objects = [];
        foreach (Match o in IndirectObject().Matches(pdf))
        {
            objects[o.Groups["num"].Value] = o.Groups["body"].Value;
        }

        Dictionary<string, Dictionary<string, string>> fonts = [];
        foreach (Match r in objects.Values.SelectMany(b => FontResource().Matches(b)))
        {
            Dictionary<string, string> map = [];
            string cmap = objects[ToUnicodeReference().Match(objects[r.Groups["num"].Value]).Groups["num"].Value];
            foreach (Match m in ToUnicodeEntry().Matches(cmap))
            {
                map[m.Groups["code"].Value] = Encoding.BigEndianUnicode.GetString(Convert.FromHexString(m.Groups["text"].Value));
            }

            fonts[r.Groups["name"].Value] = map;
        }

        return fonts;
    }

    private static bool HoldsMarkOrEndsWithFormat(string text)
    {
        bool mark = text.EnumerateRunes().Any(r => Rune.GetUnicodeCategory(r) == UnicodeCategory.NonSpacingMark);
        return mark || Rune.GetUnicodeCategory(text.EnumerateRunes().Last()) == UnicodeCategory.Format;
    }

    private static string Draw(CorpusTests.Case c) =>
        Encoding.Latin1.GetString(MixedDirectionExtractionTests.Draw(c.Text, c.Fonts, out _, out _));

    [Fact]
    public static void A_glyph_with_a_mark_or_a_format_character_is_shown_under_a_new_font_size_and_every_glyph_is_drawn_at_the_run_size()
    {
        int special = 0;
        int multiFont = 0;
        foreach (CorpusTests.Case c in CorpusTests.Load())
        {
            string pdf = Draw(c);
            Dictionary<string, Dictionary<string, string>> text = ToUnicodeByFont(pdf);
            (string Name, double Size)? font = null;
            (string Name, double Size)? previousShown = null;
            double a = 1;
            foreach (Match m in ContentOperator().Matches(pdf))
            {
                if (m.Groups["tf"].Success)
                {
                    font = (m.Groups["font"].Value, double.Parse(m.Groups["size"].Value, CultureInfo.InvariantCulture));
                }
                else if (m.Groups["tm"].Success)
                {
                    a = double.Parse(m.Groups["a"].Value, CultureInfo.InvariantCulture);
                }
                else
                {
                    Assert.NotNull(font);
                    (string name, double size) = font.Value;
                    string shown = text[name].TryGetValue(m.Groups["shown"].Value, out string? t) ? t : PdfEmbeddedFont.ContinuationText;
                    Assert.Equal(RunSize, size * a, 3);
                    if (c.Fonts.Length > 1)
                    {
                        multiFont++;
                    }

                    if (HoldsMarkOrEndsWithFormat(shown))
                    {
                        special++;
                        if (previousShown is { } before)
                        {
                            Assert.True(before != font, $"{c.Id}: glyph with the text U+{Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(shown))} follows a glyph shown under the same font and size");
                        }
                    }

                    previousShown = font;
                }
            }
        }

        Assert.True(special > 100, $"only {special} glyphs with a mark or a format character: the corpus no longer exercises the switch");
        Assert.True(multiFont > 20, $"only {multiFont} glyphs on lines with several fonts: the corpus no longer exercises them");
    }

    [Theory]
    [InlineData("hebrew-points")]
    [InlineData("arabic-diacritics")]
    public static void Each_combining_mark_of_a_right_to_left_run_has_a_cluster_and_a_text_of_its_own(string id)
    {
        CorpusTests.Case c = CorpusTests.Load().Single(x => x.Id == id);
        _ = MixedDirectionExtractionTests.Draw(c.Text, c.Fonts, out ShapedParagraph paragraph, out _);
        HashSet<int> clusters = [.. paragraph.Runs.Where(r => r.IsRightToLeft).SelectMany(r => r.Glyphs).Select(g => g.Cluster)];
        Dictionary<string, string> text = ToUnicodeByFont(Draw(c)).Values.Single();

        // HarfBuzz puts marks in canonical order first, and a reordered mark shares the cluster of the one it passed: so not quite every mark has a cluster of its own.
        int marks = 0, own = 0;
        for (int i = 0; i < c.Text.Length; i++)
        {
            if (char.GetUnicodeCategory(c.Text[i]) != UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            marks++;
            if (clusters.Contains(i))
            {
                own++;
                if (i + 1 == c.Text.Length || clusters.Contains(i + 1))
                {
                    Assert.Contains(c.Text[i].ToString(), text.Values);
                }
            }

        }

        Assert.True(own * 4 >= marks * 3 && marks > 5, $"{id}: only {own} of {marks} marks have a cluster of their own");
    }

    [Fact]
    public static void The_zero_width_non_joiner_of_a_persian_line_maps_to_a_text_that_ends_with_the_joiner()
    {
        CorpusTests.Case c = CorpusTests.Load().Single(x => x.Id == "arabic-persian");
        Dictionary<string, string> text = ToUnicodeByFont(Draw(c)).Values.Single();
        string[] withZwnj = [.. text.Values.Where(t => t.Contains((char)0x200C, StringComparison.Ordinal))];
        Assert.NotEmpty(withZwnj);
        Assert.All(withZwnj, t => Assert.EndsWith(new string([(char)0x200C, (char)0x034F]), t, StringComparison.Ordinal));
    }

    [Fact]
    public static void A_run_written_after_a_restore_of_the_graphics_state_sets_its_font_again()
    {
        ShapingFont[] fonts = LayoutTestKit.Fonts(LayoutTestKit.Latin);
        ShapedParagraph paragraph = TextShaper.Shape("ab", fonts);

        using MemoryStream stream = new();
        using (PdfDocumentWriter document = new(stream, new PdfDocumentOptions { Compression = PdfCompression.None }))
        {
            PdfEmbeddedFont embedded = new(document, fonts[0].Font);
            PdfPageWriter page = document.BeginPage(595, 842);
            string name = page.UseFont(embedded.Reference);

            // The font the builder tracked was set inside q ... Q, so after the Q the state holds another one and the run must say which it uses.
            page.Content.SaveState().BeginText().Font(name, RunSize).EndText().RestoreState();
            Assert.Null(page.Content.CurrentFont);
            page.Content.BeginText();
            GlyphRunWriter.Write(page.Content, embedded, name, paragraph.Runs[0], "ab", 20, 780, RunSize);
            page.Content.EndText();
            page.End();
            document.Complete();
        }

        string pdf = Encoding.Latin1.GetString(stream.ToArray());
        string afterRestore = pdf[(pdf.IndexOf("\nQ\n", StringComparison.Ordinal) + 3)..];
        Assert.Matches(@"^BT\s+/\S+ 14 Tf", afterRestore);
    }
}
