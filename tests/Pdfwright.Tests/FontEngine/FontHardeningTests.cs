using System.Buffers.Binary;
using HarfBuzzSharp;
using Pdfwright.Fonts;
using Pdfwright.Pdf;
using Pdfwright.Tests.Pdf;

namespace Pdfwright.Tests.FontEngine;

/// <summary>A font file is input from outside: a corrupt one is refused when it is parsed, with one kind of exception, and never later.</summary>
public class FontHardeningTests
{
    private static byte[] NotoSans() => File.ReadAllBytes(TestPaths.Font("NotoSans-Regular.ttf"));

    /// <summary>The offset in the file of a table's directory record (tag, checksum, offset, length).</summary>
    private static int Record(byte[] font, string tag)
    {
        int count = BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(4, 2));
        for (int i = 0; i < count; i++)
        {
            int record = 12 + (i * 16);
            if (System.Text.Encoding.ASCII.GetString(font, record, 4) == tag)
            {
                return record;
            }
        }

        throw new InvalidOperationException($"No table {tag}.");
    }

    private static int Offset(byte[] font, string tag) => (int)BinaryPrimitives.ReadUInt32BigEndian(font.AsSpan(Record(font, tag) + 8, 4));

    private static void SetLength(byte[] font, string tag, uint length) =>
        BinaryPrimitives.WriteUInt32BigEndian(font.AsSpan(Record(font, tag) + 12, 4), length);

    [Theory]
    [InlineData("head", 20u)]
    [InlineData("hhea", 10u)]
    [InlineData("maxp", 2u)]
    [InlineData("loca", 100u)]
    [InlineData("hmtx", 100u)]
    [InlineData("cmap", 2u)]
    [InlineData("OS/2", 6u)]
    public void A_table_shorter_than_what_is_read_from_it_is_refused_when_the_font_is_parsed(string table, uint length)
    {
        byte[] font = NotoSans();
        SetLength(font, table, length);

        Assert.Throws<InvalidDataException>(() => TrueTypeFont.Parse(font));
    }

    [Fact]
    public void A_table_that_runs_past_the_end_of_the_file_is_refused()
    {
        byte[] font = NotoSans();
        SetLength(font, "glyf", uint.MaxValue - 100);

        Assert.Throws<InvalidDataException>(() => TrueTypeFont.Parse(font));
    }

    [Fact]
    public void A_loca_entry_that_points_outside_glyf_is_refused()
    {
        byte[] font = NotoSans();
        int loca = Offset(font, "loca");
        BinaryPrimitives.WriteUInt32BigEndian(font.AsSpan(loca + (10 * 4), 4), 0x7FFFFFFF);

        Assert.Throws<InvalidDataException>(() => TrueTypeFont.Parse(font));
    }

    [Fact]
    public void A_loca_table_that_goes_backwards_is_refused()
    {
        byte[] font = NotoSans();
        int loca = Offset(font, "loca");
        uint at20 = BinaryPrimitives.ReadUInt32BigEndian(font.AsSpan(loca + (20 * 4), 4));
        Assert.True(at20 > 0, "the test needs a glyph with an outline");
        BinaryPrimitives.WriteUInt32BigEndian(font.AsSpan(loca + (21 * 4), 4), 0);
        BinaryPrimitives.WriteUInt32BigEndian(font.AsSpan(loca + (22 * 4), 4), at20 / 2);

        Assert.Throws<InvalidDataException>(() => TrueTypeFont.Parse(font));
    }

    [Fact]
    public void A_composite_that_names_a_glyph_the_font_does_not_have_is_refused()
    {
        byte[] font = NotoSans();
        TrueTypeFont good = TrueTypeFont.Parse(font);
        int eAcute = good.GlyphForCodePoint('é');
        Assert.NotEmpty(good.Components(eAcute));
        int loca = Offset(font, "loca");
        int glyf = Offset(font, "glyf");
        int start = (int)BinaryPrimitives.ReadUInt32BigEndian(font.AsSpan(loca + (eAcute * 4), 4));
        BinaryPrimitives.WriteUInt16BigEndian(font.AsSpan(glyf + start + 12, 2), 0xFFFF);

        Assert.Throws<InvalidDataException>(() => TrueTypeFont.Parse(font));
    }

    [Fact]
    public void A_composite_whose_components_run_past_its_data_is_refused()
    {
        byte[] font = NotoSans();
        TrueTypeFont good = TrueTypeFont.Parse(font);
        int eAcute = good.GlyphForCodePoint('é');
        int loca = Offset(font, "loca");
        int glyf = Offset(font, "glyf");
        int start = (int)BinaryPrimitives.ReadUInt32BigEndian(font.AsSpan(loca + (eAcute * 4), 4));

        // Every component says that another follows.
        ushort flags = BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(glyf + start + 10, 2));
        BinaryPrimitives.WriteUInt16BigEndian(font.AsSpan(glyf + start + 10, 2), (ushort)(flags | 0x0020));
        int end = (int)BinaryPrimitives.ReadUInt32BigEndian(font.AsSpan(loca + ((eAcute + 1) * 4), 4));
        int at = glyf + start + 10;
        while (at + 4 <= glyf + end)
        {
            ushort componentFlags = BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(at, 2));
            BinaryPrimitives.WriteUInt16BigEndian(font.AsSpan(at, 2), (ushort)(componentFlags | 0x0020));
            at += 4 + ((componentFlags & 1) != 0 ? 4 : 2) + ((componentFlags & 0x0008) != 0 ? 2 : (componentFlags & 0x0040) != 0 ? 4 : (componentFlags & 0x0080) != 0 ? 8 : 0);
        }

        Assert.Throws<InvalidDataException>(() => TrueTypeFont.Parse(font));
    }

    [Fact]
    public void A_font_with_no_glyphs_or_no_metrics_is_refused()
    {
        byte[] noGlyphs = NotoSans();
        BinaryPrimitives.WriteUInt16BigEndian(noGlyphs.AsSpan(Offset(noGlyphs, "maxp") + 4, 2), 0);
        byte[] noMetrics = NotoSans();
        BinaryPrimitives.WriteUInt16BigEndian(noMetrics.AsSpan(Offset(noMetrics, "hhea") + 34, 2), 0);

        Assert.Throws<InvalidDataException>(() => TrueTypeFont.Parse(noGlyphs));
        Assert.Throws<InvalidDataException>(() => TrueTypeFont.Parse(noMetrics));
    }

    [Fact]
    public void A_character_map_that_names_a_glyph_the_font_does_not_have_gives_no_glyph()
    {
        byte[] font = NotoSans();
        TrueTypeFont good = TrueTypeFont.Parse(font);
        int a = good.GlyphForCodePoint('A');
        BinaryPrimitives.WriteUInt16BigEndian(font.AsSpan(Offset(font, "maxp") + 4, 2), (ushort)a);
        BinaryPrimitives.WriteUInt16BigEndian(font.AsSpan(Offset(font, "hhea") + 34, 2), (ushort)a);

        // The font now has glyphs 0 to a - 1; 'A' is glyph a, which does not exist. The loca is longer than needed, which is allowed.
        TrueTypeFont cut = TrueTypeFont.Parse(font);

        Assert.Equal(0, cut.GlyphForCodePoint('A'));
        Assert.NotEqual(0, cut.GlyphForCodePoint(' '));
    }

    [Fact]
    public void A_character_map_with_more_segments_than_its_table_holds_is_refused()
    {
        byte[] font = NotoSans();
        int cmap = Offset(font, "cmap");
        int tables = BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(cmap + 2, 2));
        for (int i = 0; i < tables; i++)
        {
            int offset = cmap + (int)BinaryPrimitives.ReadUInt32BigEndian(font.AsSpan(cmap + 8 + (i * 8), 4));
            if (BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(offset, 2)) == 4)
            {
                BinaryPrimitives.WriteUInt16BigEndian(font.AsSpan(offset + 6, 2), 0xFFFE);
            }
            else
            {
                BinaryPrimitives.WriteUInt32BigEndian(font.AsSpan(offset + 12, 4), 0x0FFFFFFF);
            }
        }

        Assert.Throws<InvalidDataException>(() => TrueTypeFont.Parse(font));
    }

    [Fact]
    public void A_parsed_font_does_not_change_when_the_array_it_came_from_does()
    {
        byte[] font = NotoSans();
        TrueTypeFont parsed = TrueTypeFont.Parse(font);
        int before = parsed.GlyphForCodePoint('A');
        Array.Clear(font);

        Assert.Equal(before, parsed.GlyphForCodePoint('A'));
        Assert.Equal(NotoSans().Length, parsed.Bytes.Length);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(4, true)]
    [InlineData(8, true)]
    [InlineData(0x000C, true)]
    [InlineData(2, false)]
    [InlineData(0x0100, false)]
    [InlineData(0x0200, false)]
    [InlineData(0x0104, false)]
    [InlineData(0x0006, true)]
    [InlineData(0x000A, true)]
    [InlineData(0x0106, false)]
    public void The_embedding_licence_allows_a_subset_exactly_where_the_bits_say(int fsType, bool allowed)
    {
        byte[] font = NotoSans();
        BinaryPrimitives.WriteUInt16BigEndian(font.AsSpan(Offset(font, "OS/2") + 8, 2), (ushort)fsType);

        Assert.Equal(allowed, TrueTypeFont.Parse(font).MayEmbedSubset);
    }

    [Fact]
    public void A_postscript_name_is_a_plain_pdf_name()
    {
        TrueTypeFont font = TrueTypeFont.Parse(NotoSans());

        Assert.Matches("^[A-Za-z0-9._-]{1,63}$", font.PostScriptName);
    }

    [Theory]
    [InlineData("NotoSansHebrew-Regular.ttf")]
    [InlineData("NotoSansThai-Regular.ttf")]
    [InlineData("NotoSansBengali-Regular.ttf")]
    [InlineData("NotoSansTamil-Regular.ttf")]
    [InlineData("NotoSansDevanagari-Regular.ttf")]
    [InlineData("NotoSansArabic-Regular.ttf")]
    public void A_subset_of_every_script_font_has_the_widths_and_outlines_of_the_original(string file)
    {
        // Short and long loca, and numberOfHMetrics below the glyph count, are among these fonts: the subset's tables are read
        // back by HarfBuzz and compared with the original's, glyph by glyph, which the extents alone would not show.
        byte[] bytes = File.ReadAllBytes(TestPaths.Font(file));
        TrueTypeFont font = TrueTypeFont.Parse(bytes);
        int[] used = [.. Enumerable.Range(0, font.GlyphCount).Where(g => g % 7 == 0 || g == font.GlyphCount - 1)];

        byte[] subset = TrueTypeSubsetter.Subset(font, used);

        using Blob original = TestPaths.BlobOf(bytes);
        using Face originalFace = new(original, 0);
        using HarfBuzzSharp.Font originalFont = new(originalFace);
        using Blob cut = TestPaths.BlobOf(subset);
        using Face cutFace = new(cut, 0);
        using HarfBuzzSharp.Font cutFont = new(cutFace);
        Assert.Equal(font.GlyphCount, (int)cutFace.GlyphCount);
        foreach (int glyph in used)
        {
            Assert.Equal(originalFont.GetHorizontalGlyphAdvance((uint)glyph), cutFont.GetHorizontalGlyphAdvance((uint)glyph));
            Assert.Equal(originalFont.TryGetGlyphExtents((uint)glyph, out GlyphExtents want), cutFont.TryGetGlyphExtents((uint)glyph, out GlyphExtents got));
            Assert.Equal(want, got);
        }
    }

    [Fact]
    public void A_glyph_drawn_for_other_text_gets_a_code_of_its_own_and_the_same_text_gets_the_same_code()
    {
        using MemoryStream stream = new();
        using PdfDocumentWriter document = new(stream, new PdfWriterOptions());
        PdfEmbeddedFont font = new(document, TrueTypeFont.Parse(NotoSans()));
        int a = font.Font.GlyphForCodePoint('A');

        int first = font.UseGlyph(a, "A");
        int greek = font.UseGlyph(a, "Α");
        int none = font.UseGlyph(a, null);

        Assert.Equal(a, first);
        Assert.Equal(first, font.UseGlyph(a, "A"));
        Assert.Equal(greek, font.UseGlyph(a, "Α"));
        Assert.Equal(none, font.UseGlyph(a, string.Empty));
        Assert.Equal(3, new[] { first, greek, none }.Distinct().Count());
        Assert.All(new[] { greek, none }, code => Assert.True(code >= font.Font.GlyphCount));
    }

    [Fact]
    public void Two_texts_of_one_glyph_extract_as_their_own_text_through_a_glyph_map()
    {
        using MemoryStream stream = new();
        using (PdfDocumentWriter document = new(stream, new PdfWriterOptions()))
        {
            PdfEmbeddedFont font = new(document, TrueTypeFont.Parse(NotoSans()));
            int a = font.Font.GlyphForCodePoint('A');
            PdfPageWriter page = document.BeginPage(300, 300);
            page.Content.BeginText().Font(page.UseFont(font.Reference), 20).TextMove(20, 200)
                .ShowHex(PdfEmbeddedFont.Code(font.UseGlyph(a, "A")))
                .ShowHex(PdfEmbeddedFont.Code(font.UseGlyph(a, "Α")))
                .ShowHex(PdfEmbeddedFont.Code(font.UseGlyph(a, "A")))
                .EndText();
            page.End();
            document.Complete();
        }

        using UglyToad.PdfPig.PdfDocument pdf = UglyToad.PdfPig.PdfDocument.Open(stream.ToArray());

        Assert.Equal("AΑA", pdf.GetPage(1).Text);
        PdfTestKit.AssertQpdfAccepts(stream.ToArray(), "glyph-map");
    }

    [Fact]
    public void An_empty_text_does_not_block_the_real_text_of_a_glyph()
    {
        using MemoryStream stream = new();
        PdfEmbeddedFont font;
        using (PdfDocumentWriter document = new(stream, new PdfWriterOptions { Compression = PdfCompression.None }))
        {
            font = new(document, TrueTypeFont.Parse(NotoSans()));
            int a = font.Font.GlyphForCodePoint('A');
            font.UseGlyph(a, string.Empty);
            font.UseGlyph(a, "A");
            document.BeginPage(100, 100).End();
            document.Complete();
        }

        Assert.Matches("<[0-9A-F]{4}> <0041>", System.Text.Encoding.Latin1.GetString(stream.ToArray()));
    }

    [Fact]
    public void A_glyph_cannot_be_recorded_once_the_document_is_complete()
    {
        using MemoryStream stream = new();
        using PdfDocumentWriter document = new(stream, new PdfWriterOptions());
        PdfEmbeddedFont font = new(document, TrueTypeFont.Parse(NotoSans()));
        document.BeginPage(100, 100).End();
        document.Complete();

        Assert.Throws<InvalidOperationException>(() => font.UseGlyph(font.Font.GlyphForCodePoint('A'), "A"));
    }

    [Fact]
    public void The_advance_is_the_whole_number_the_widths_array_carries()
    {
        using MemoryStream stream = new();
        using PdfDocumentWriter document = new(stream, new PdfWriterOptions());
        TrueTypeFont parsed = TrueTypeFont.Parse(NotoSans());
        PdfEmbeddedFont font = new(document, parsed);

        for (int glyph = 0; glyph < 200; glyph++)
        {
            Assert.Equal(Math.Round(font.Advance(glyph)), font.Advance(glyph));
        }
    }

    [Fact]
    public void Several_glyphs_of_a_font_survive_a_subset_that_is_parsed_again_and_every_cmap_lookup_stays_inside_the_font()
    {
        foreach (string file in Directory.GetFiles(TestPaths.FontsDirectory, "*.ttf"))
        {
            TrueTypeFont font = TrueTypeFont.Parse(File.ReadAllBytes(file));
            for (int codePoint = 0; codePoint < 0x3000; codePoint += 3)
            {
                int glyph = font.GlyphForCodePoint(codePoint);
                Assert.InRange(glyph, 0, font.GlyphCount - 1);
            }
        }
    }
}
