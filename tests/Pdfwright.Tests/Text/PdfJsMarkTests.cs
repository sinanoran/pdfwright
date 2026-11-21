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

    [GeneratedRegex(@"(?<tf>/\S+ (?<size>-?[\d.]+) Tf)|(?<tm>(?<a>-?[\d.]+) -?[\d.]+ -?[\d.]+ -?[\d.]+ -?[\d.]+ -?[\d.]+ Tm)|<(?<shown>[0-9A-F]{4})> Tj")]
    private static partial Regex ContentOperator();

    /// <summary>The text of every code of the (one) embedded font, from its <c>ToUnicode</c> map.</summary>
    private static Dictionary<string, string> ToUnicode(string pdf)
    {
        Dictionary<string, string> map = [];
        foreach (Match m in ToUnicodeEntry().Matches(pdf))
        {
            map[m.Groups["code"].Value] = Encoding.BigEndianUnicode.GetString(Convert.FromHexString(m.Groups["text"].Value));
        }

        return map;
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
        foreach (CorpusTests.Case c in CorpusTests.Load().Where(x => x.Fonts.Length == 1))
        {
            string pdf = Draw(c);
            Dictionary<string, string> text = ToUnicode(pdf);
            double? size = null;
            double? previousShown = null;
            double a = 1;
            foreach (Match m in ContentOperator().Matches(pdf))
            {
                if (m.Groups["tf"].Success)
                {
                    size = double.Parse(m.Groups["size"].Value, CultureInfo.InvariantCulture);
                }
                else if (m.Groups["tm"].Success)
                {
                    a = double.Parse(m.Groups["a"].Value, CultureInfo.InvariantCulture);
                }
                else
                {
                    Assert.NotNull(size);
                    string shown = text.TryGetValue(m.Groups["shown"].Value, out string? t) ? t : PdfEmbeddedFont.ContinuationText;
                    Assert.Equal(RunSize, size.Value * a, 3);
                    if (HoldsMarkOrEndsWithFormat(shown))
                    {
                        special++;
                        if (previousShown is { } before)
                        {
                            Assert.True(before != size, $"{c.Id}: glyph with the text U+{Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(shown))} follows a glyph shown under the same font size");
                        }
                    }

                    previousShown = size;
                }
            }
        }

        Assert.True(special > 100, $"only {special} glyphs with a mark or a format character: the corpus no longer exercises the switch");
    }

    [Fact]
    public static void The_zero_width_non_joiner_of_a_persian_line_maps_to_a_text_that_ends_with_the_joiner()
    {
        CorpusTests.Case c = CorpusTests.Load().Single(x => x.Id == "arabic-persian");
        Dictionary<string, string> text = ToUnicode(Draw(c));
        string[] withZwnj = [.. text.Values.Where(t => t.Contains((char)0x200C, StringComparison.Ordinal))];
        Assert.NotEmpty(withZwnj);
        Assert.All(withZwnj, t => Assert.EndsWith(new string([(char)0x200C, (char)0x034F]), t, StringComparison.Ordinal));
    }
}
