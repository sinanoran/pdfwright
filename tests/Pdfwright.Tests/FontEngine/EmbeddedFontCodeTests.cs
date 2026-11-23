using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Pdfwright.Fonts;
using Pdfwright.Pdf;

namespace Pdfwright.Tests.FontEngine;

/// <summary>
/// The codes an embedded font hands out, and the maps written for them, are those of the dictionary the font kept before it kept its codes in arrays by glyph: a code is the glyph's id the first time the glyph is drawn,
/// the next code above the glyph count for each other text it is drawn for, the same code again for a glyph and text seen before; the <c>ToUnicode</c> map, the widths and the <c>CIDToGIDMap</c> list the codes in ascending order.
/// The reference below is that dictionary, kept here as the model the font is compared with, call by call and map by map.
/// </summary>
public class EmbeddedFontCodeTests
{
    private static readonly TrueTypeFont Regular = TrueTypeFont.Parse(File.ReadAllBytes(TestPaths.Font("NotoSans-Regular.ttf")));

    /// <summary>The former bookkeeping of <see cref="PdfEmbeddedFont.UseGlyph"/>, as it was.</summary>
    private sealed class Reference(int glyphCount)
    {
        private readonly Dictionary<(int Glyph, string Text), int> _codes = [];
        private readonly HashSet<int> _glyphsWithCode = [];
        private int _nextExtra = glyphCount;

        internal SortedDictionary<int, (int Glyph, string Text)> ByCode { get; } = [];

        internal int NextExtra => _nextExtra;

        internal int UseGlyph(int glyph, string? text)
        {
            text ??= string.Empty;
            if (_codes.TryGetValue((glyph, text), out int known))
            {
                return known;
            }

            bool first = _glyphsWithCode.Add(glyph);
            int code = first ? glyph : _nextExtra++;
            _codes[(glyph, text)] = code;
            ByCode[code] = (glyph, text);
            return code;
        }
    }

    private static readonly string?[] Texts = [null, string.Empty, "a", "b", "fi", "é", "‌", "ab", "A", "ال"];

    public static TheoryData<int> Seeds() => [1, 2, 3, 4, 5, 6, 7, 8];

    [Theory]
    [MemberData(nameof(Seeds))]
    public void The_codes_and_the_maps_are_those_of_the_former_dictionary(int seed)
    {
        Random random = new(seed);

        // A mix of glyphs that are drawn often for one text (most glyphs of real text), a few drawn for several texts, low and high ids, the .notdef glyph, and the glyphs on either side of the edges of
        // the pages of 256 glyphs the font keeps its codes in.
        int[] glyphs = [0, 1, 3, 36, 68, 69, 70, 255, 256, 257, 300, 511, 512, 1000, Regular.GlyphCount - 2, Regular.GlyphCount - 1];
        Reference reference = new(Regular.GlyphCount);
        List<(int Expected, int Actual)> codes = [];
        string file = Draw(font =>
        {
            int calls = 50 + random.Next(2000);
            for (int i = 0; i < calls; i++)
            {
                int glyph = random.Next(4) == 0 ? random.Next(Regular.GlyphCount) : glyphs[random.Next(glyphs.Length)];
                string? text = random.Next(3) == 0 ? Texts[random.Next(Texts.Length)] : Texts[2 + (glyph % 3)];
                if (text is not null && random.Next(10) == 0)
                {
                    text = new string(text.ToCharArray()); // the same text as another string object
                }

                codes.Add((reference.UseGlyph(glyph, text), font.UseGlyph(glyph, text)));
            }
        });

        Assert.All(codes, pair => Assert.Equal(pair.Expected, pair.Actual));
        AssertMapsAre(reference, file);
    }

    [Fact]
    public void A_font_that_draws_nothing_maps_the_notdef_glyph_to_the_joiner()
    {
        Reference reference = new(Regular.GlyphCount);
        _ = reference.UseGlyph(0, string.Empty);
        string file = Draw(_ => { });
        AssertMapsAre(reference, file);
    }

    [Fact]
    public void A_glyph_drawn_for_a_second_text_gets_the_next_code_above_the_glyph_count_and_keeps_it()
    {
        Reference reference = new(Regular.GlyphCount);
        string file = Draw(font =>
        {
            Assert.Equal(68, font.UseGlyph(68, "a"));
            Assert.Equal(Regular.GlyphCount, font.UseGlyph(68, "b"));
            Assert.Equal(69, font.UseGlyph(69, "a"));
            Assert.Equal(Regular.GlyphCount, font.UseGlyph(68, "b"));
            Assert.Equal(Regular.GlyphCount + 1, font.UseGlyph(69, null));
            Assert.Equal(68, font.UseGlyph(68, "a"));
        });

        foreach ((int glyph, string? text) in new (int, string?)[] { (68, "a"), (68, "b"), (69, "a"), (69, null) })
        {
            _ = reference.UseGlyph(glyph, text);
        }

        AssertMapsAre(reference, file);
    }

    [Fact]
    public void Drawing_one_glyph_of_a_large_font_allocates_a_page_of_codes_not_arrays_of_the_whole_font()
    {
        // The codes are kept in pages of 256 glyphs made on demand: the first glyph drawn, the last of the font, makes one page (two arrays of 256 references, about 4 KB), not arrays as long as the font
        // (16 bytes a glyph: about 62 KB for the 3,885 glyphs of Noto Sans, a megabyte for a CJK font, for every document).
        using MemoryStream stream = new();
        using PdfDocumentWriter document = new(stream, new PdfWriterOptions { Compression = PdfCompression.None });
        PdfEmbeddedFont font = new(document, Regular);
        _ = font.UseGlyph(0, "x"); // the first call of the method, so that what is measured below is the data alone
        long before = GC.GetAllocatedBytesForCurrentThread();
        _ = font.UseGlyph(Regular.GlyphCount - 1, "y");
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.InRange(allocated, 1, 6 * 1024);
        Assert.True(allocated < 4L * Regular.GlyphCount, $"drawing one glyph allocated {allocated} bytes, in proportion to the font's {Regular.GlyphCount} glyphs");
    }

    private static string Draw(Action<PdfEmbeddedFont> use)
    {
        using MemoryStream stream = new();
        using (PdfDocumentWriter document = new(stream, new PdfWriterOptions { Compression = PdfCompression.None }))
        {
            PdfEmbeddedFont font = new(document, Regular);
            PdfPageWriter page = document.BeginPage(200, 200);
            _ = page.UseFont(font.Reference);
            use(font);
            page.End();
            document.Complete();
        }

        return Encoding.Latin1.GetString(stream.ToArray());
    }

    /// <summary>The <c>ToUnicode</c> entries, in order and in blocks of at most 100, the codes and widths of <c>/W</c>, and the <c>CIDToGIDMap</c> when there are codes above the glyph count, against the reference.</summary>
    private static void AssertMapsAre(Reference reference, string file)
    {
        KeyValuePair<int, (int Glyph, string Text)>[] expected = [.. reference.ByCode];

        StringBuilder bfchar = new();
        for (int block = 0; block < expected.Length; block += 100)
        {
            int count = Math.Min(100, expected.Length - block);
            bfchar.Append(count.ToString(CultureInfo.InvariantCulture)).Append(" beginbfchar\n");
            for (int i = block; i < block + count; i++)
            {
                string text = expected[i].Value.Text.Length == 0 ? PdfEmbeddedFont.ContinuationText : expected[i].Value.Text;
                bfchar.Append('<').Append(expected[i].Key.ToString("X4", CultureInfo.InvariantCulture)).Append("> <").Append(Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(text))).Append(">\n");
            }

            bfchar.Append("endbfchar\n");
        }

        int start = file.IndexOf("endcodespacerange\n", StringComparison.Ordinal) + "endcodespacerange\n".Length;
        int end = file.IndexOf("endcmap", start, StringComparison.Ordinal);
        Assert.Equal(bfchar.ToString(), file[start..end]);

        // The widths: runs of consecutive codes, each written as its first code and an array of widths.
        StringBuilder widths = new("/W [");
        int k = 0;
        bool firstRun = true;
        while (k < expected.Length)
        {
            int runStart = k;
            while (k + 1 < expected.Length && expected[k + 1].Key == expected[k].Key + 1)
            {
                k++;
            }

            widths.Append(firstRun ? string.Empty : " ").Append(expected[runStart].Key.ToString(CultureInfo.InvariantCulture)).Append(" [");
            for (int i = runStart; i <= k; i++)
            {
                double advance = Math.Round(Regular.Advance(expected[i].Value.Glyph) * 1000.0 / Regular.UnitsPerEm);
                widths.Append(i == runStart ? string.Empty : " ").Append(((long)advance).ToString(CultureInfo.InvariantCulture));
            }

            widths.Append(']');
            firstRun = false;
            k++;
        }

        widths.Append(']');
        Match written = Regex.Match(file, @"/W \[.*?\]\]", RegexOptions.Singleline);
        Assert.True(written.Success, "the font has no /W array");
        Assert.Equal(widths.ToString(), written.Value);

        if (reference.NextExtra > Regular.GlyphCount)
        {
            byte[] map = new byte[reference.NextExtra * 2];
            foreach (KeyValuePair<int, (int Glyph, string Text)> pair in expected)
            {
                map[pair.Key * 2] = (byte)(pair.Value.Glyph >> 8);
                map[(pair.Key * 2) + 1] = (byte)pair.Value.Glyph;
            }

            Assert.Contains(Encoding.Latin1.GetString(map), file, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("/CIDToGIDMap /Identity", file, StringComparison.Ordinal);
        }
    }
}
