using System.Text;
using System.Text.RegularExpressions;
using Pdfwright.Fonts;
using Pdfwright.Pdf;
using Pdfwright.Text;
using Pdfwright.Unicode;

namespace Pdfwright.Tests.FontEngine;

/// <summary>The <c>ToUnicode</c> map of an embedded font: one text per code, the cluster's text on the glyph that carries it, a joiner on the rest.</summary>
public class ToUnicodeMappingTests
{
    private static string ToUnicodeOf(Action<PdfEmbeddedFont, PdfPageWriter> draw, bool tagged = false)
    {
        using MemoryStream stream = new();
        using (PdfDocumentWriter document = new(stream, new PdfWriterOptions { Compression = PdfCompression.None, Tagged = tagged }))
        {
            PdfEmbeddedFont font = new(document, TrueTypeFont.Parse(File.ReadAllBytes(TestPaths.Font("NotoSans-Regular.ttf"))));
            PdfPageWriter page = document.BeginPage(200, 200);
            draw(font, page);
            page.End();
            document.Complete();
        }

        return Encoding.Latin1.GetString(stream.ToArray());
    }

    private static string Hex(int code) => code.ToString("X4", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void A_code_that_only_continues_a_cluster_maps_to_the_grapheme_joiner_and_the_glyph_that_carries_the_text_maps_to_the_cluster()
    {
        int a = 0;
        int b = 0;
        string map = ToUnicodeOf((font, _) =>
        {
            a = font.UseGlyph(font.Font.GlyphForCodePoint('e'), "é");
            b = font.UseGlyph(font.Font.GlyphForCodePoint('x'), null);
        });

        Assert.Equal("͏", PdfEmbeddedFont.ContinuationText);
        Assert.Contains($"<{Hex(a)}> <00650301>", map, StringComparison.Ordinal);
        Assert.Contains($"<{Hex(b)}> <034F>", map, StringComparison.Ordinal);
        Assert.DoesNotContain($"<{Hex(b)}> <>", map, StringComparison.Ordinal);
    }

    [Fact]
    public void No_code_is_ever_mapped_to_the_empty_string()
    {
        string map = ToUnicodeOf((font, _) =>
        {
            font.UseGlyph(font.Font.GlyphForCodePoint('a'), string.Empty);
            font.UseGlyph(font.Font.GlyphForCodePoint('b'), null);
            font.UseGlyph(font.Font.GlyphForCodePoint('c'), "c");
        });

        Assert.DoesNotMatch(new Regex("<[0-9A-F]{4}> <>"), map);
    }

    [Fact]
    public void In_a_shaped_right_to_left_run_the_text_of_a_cluster_is_on_its_first_glyph_written_and_the_others_carry_the_joiner()
    {
        // An Arabic word in Noto Sans Arabic has letters drawn as a skeleton plus separate dots: some clusters have several glyphs.
        using ShapingFont arabic = new(TrueTypeFont.Parse(File.ReadAllBytes(TestPaths.Font("NotoSansArabic-Regular.ttf"))));
        const string Text = "مرحبا";
        ShapedParagraph paragraph = TextShaper.Shape(Text, [arabic]);
        ShapedRun run = Assert.Single(paragraph.Runs);
        int[] glyphsPerCluster = [.. run.Glyphs.GroupBy(g => g.Cluster).Select(c => c.Count())];
        Assert.Contains(glyphsPerCluster, count => count > 1); // the premise: there is a multi-glyph cluster

        string map;
        using (MemoryStream stream = new())
        {
            using (PdfDocumentWriter document = new(stream, new PdfWriterOptions { Compression = PdfCompression.None }))
            {
                PdfEmbeddedFont font = new(document, arabic.Font);
                PdfPageWriter page = document.BeginPage(300, 200);
                page.Content.BeginText();
                GlyphRunWriter.Write(page.Content, font, page.UseFont(font.Reference), run, Text, 20, 100, 14);
                page.Content.EndText();
                page.End();
                document.Complete();
            }

            map = Encoding.Latin1.GetString(stream.ToArray());
        }

        // Every code has exactly one entry; the entries that carry the Arabic letters are the ones that are not the joiner.
        List<Match> entries = [.. Regex.Matches(map, "<([0-9A-F]{4})> <([0-9A-F]+)>\n").Where(m => m.Groups[2].Value != "FFFF")]; // not the code space range
        int joiners = entries.Count(m => m.Groups[2].Value == "034F");
        int letters = entries.Count(m => m.Groups[2].Value != "034F");
        Assert.True(joiners >= 1, "a continuation glyph maps to the joiner");
        Assert.Equal(Text.Length, letters);
        Assert.Equal(entries.Count, entries.Select(m => m.Groups[1].Value).Distinct().Count());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_tagged_document_maps_no_code_to_the_byte_order_mark_or_its_noncharacter_and_an_untagged_one_is_as_it_was(bool tagged)
    {
        int bom = 0;
        int non = 0;
        string map = ToUnicodeOf(
            (font, _) =>
            {
                bom = font.UseGlyph(font.Font.GlyphForCodePoint('a'), "a\uFEFF");
                non = font.UseGlyph(font.Font.GlyphForCodePoint('b'), "\uFFFE");
            },
            tagged);

        // PDF/UA-1 7.21.7 forbids U+FEFF and U+FFFE in ToUnicode: tagged, U+FEFF is the word joiner that took its meaning and U+FFFE the replacement character.
        Assert.Contains(tagged ? $"<{Hex(bom)}> <00612060>" : $"<{Hex(bom)}> <0061FEFF>", map, StringComparison.Ordinal);
        Assert.Contains(tagged ? $"<{Hex(non)}> <FFFD>" : $"<{Hex(non)}> <FFFE>", map, StringComparison.Ordinal);
    }
}
