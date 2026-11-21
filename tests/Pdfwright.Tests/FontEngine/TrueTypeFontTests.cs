using System.Buffers.Binary;
using HarfBuzzSharp;
using Pdfwright.Fonts;

namespace Pdfwright.Tests.FontEngine;

/// <summary>The TrueType reader and the subsetter, against the real Noto fonts of the corpus and against HarfBuzz as the second opinion.</summary>
public class TrueTypeFontTests
{
    private static TrueTypeFont Load(string file) => TrueTypeFont.Parse(File.ReadAllBytes(TestPaths.Font(file)));

    [Fact]
    public void The_names_metrics_and_licence_of_a_font_are_read()
    {
        TrueTypeFont font = Load("NotoSans-Regular.ttf");

        Assert.Equal("Noto Sans", font.FamilyName);
        Assert.Equal("NotoSans-Regular", font.PostScriptName);
        Assert.Equal(1000, font.UnitsPerEm);
        Assert.True(font.GlyphCount > 1000);
        Assert.True(font.Ascender > 0 && font.Descender < 0);
        Assert.True(font.MayEmbedSubset);
        Assert.Equal(400, font.WeightClass);
        Assert.False(font.IsFixedPitch);
        Assert.Equal(0, font.ItalicAngle);
        Assert.NotEqual(0, Load("NotoSans-Italic.ttf").ItalicAngle);
        Assert.Equal(700, Load("NotoSans-Bold.ttf").WeightClass);
    }

    [Theory]
    [InlineData("NotoSans-Regular.ttf", 'A')]
    [InlineData("NotoSans-Regular.ttf", 'İ')]
    [InlineData("NotoSans-Regular.ttf", 'ğ')]
    [InlineData("NotoSans-Regular.ttf", 'Ω')]
    [InlineData("NotoSans-Regular.ttf", 'Ж')]
    [InlineData("NotoSansArabic-Regular.ttf", 'م')]
    [InlineData("NotoSansHebrew-Regular.ttf", 'ש')]
    [InlineData("NotoSansDevanagari-Regular.ttf", 'क')]
    [InlineData("NotoSansThai-Regular.ttf", 'ก')]
    public void A_character_has_the_glyph_HarfBuzz_gives_it(string file, char character)
    {
        TrueTypeFont font = Load(file);
        using Blob blob = Blob.FromFile(TestPaths.Font(file))!;
        using Face face = new(blob, 0);
        using Font reference = new(face);
        Assert.True(reference.TryGetNominalGlyph(character, out uint expected));

        Assert.Equal((int)expected, font.GlyphForCodePoint(character));
        Assert.NotEqual(0, font.GlyphForCodePoint(character));
    }

    [Fact]
    public void A_character_the_font_does_not_have_is_glyph_zero_and_so_is_a_code_point_outside_unicode_ranges()
    {
        TrueTypeFont font = Load("NotoSans-Regular.ttf");

        Assert.Equal(0, font.GlyphForCodePoint('漢'));
        Assert.Equal(0, font.GlyphForCodePoint(0x10FFFF));
        Assert.Equal(0, font.GlyphForCodePoint(0x1F600));
    }

    [Theory]
    [InlineData("NotoSans-Regular.ttf")]
    [InlineData("NotoSansArabic-Regular.ttf")]
    [InlineData("NotoSansTamil-Regular.ttf")]
    public void Every_advance_equals_the_one_HarfBuzz_reads(string file)
    {
        TrueTypeFont font = Load(file);
        using Blob blob = Blob.FromFile(TestPaths.Font(file))!;
        using Face face = new(blob, 0);
        using Font reference = new(face);
        reference.SetScale(font.UnitsPerEm, font.UnitsPerEm);

        for (int glyph = 0; glyph < font.GlyphCount; glyph += 7)
        {
            Assert.Equal(reference.GetHorizontalGlyphAdvance((uint)glyph), font.Advance(glyph));
        }

        Assert.Equal(reference.GetHorizontalGlyphAdvance((uint)(font.GlyphCount - 1)), font.Advance(font.GlyphCount - 1));
    }

    [Fact]
    public void A_composite_glyph_names_the_glyphs_it_is_built_from()
    {
        TrueTypeFont font = Load("NotoSans-Regular.ttf");
        int eAcute = font.GlyphForCodePoint('é');

        IReadOnlyList<int> parts = font.Components(eAcute);

        Assert.True(parts.Count >= 2, "é is a base letter and an accent in Noto Sans");
        Assert.Contains(font.GlyphForCodePoint('e'), parts);
        Assert.Empty(font.Components(font.GlyphForCodePoint('A')));
    }

    [Fact]
    public void A_font_with_cff_outlines_a_truncated_file_and_a_file_that_is_no_font_are_refused_with_a_clear_message()
    {
        byte[] cff = new byte[64];
        "OTTO"u8.CopyTo(cff);
        Assert.Contains("CFF", Assert.Throws<NotSupportedException>(() => TrueTypeFont.Parse(cff)).Message);

        byte[] whole = File.ReadAllBytes(TestPaths.Font("NotoSans-Regular.ttf"));
        Assert.Throws<InvalidDataException>(() => TrueTypeFont.Parse(whole[..400]));
        Assert.Throws<InvalidDataException>(() => TrueTypeFont.Parse(new byte[200]));
        Assert.Throws<InvalidDataException>(() => TrueTypeFont.Parse([1, 2, 3]));
    }

    [Fact]
    public void A_subset_keeps_the_used_glyphs_and_their_components_with_the_ids_they_had()
    {
        TrueTypeFont font = Load("NotoSans-Regular.ttf");
        int a = font.GlyphForCodePoint('A');
        int eAcute = font.GlyphForCodePoint('é');

        byte[] subset = TrueTypeSubsetter.Subset(font, [a, eAcute]);

        using Blob blob = TestPaths.BlobOf(subset);
        using Face face = new(blob, 0);
        using Blob referenceBlob = TestPaths.BlobOf(font.Bytes.ToArray());
        using Face referenceFace = new(referenceBlob, 0);
        using Font original = new(referenceFace);
        using Font cut = new(face);
        Assert.Equal(Math.Max(a, Math.Max(eAcute, font.Components(eAcute).Max())) + 1, (int)face.GlyphCount);
        foreach (int glyph in new[] { a, eAcute }.Concat(font.Components(eAcute)))
        {
            Assert.True(original.TryGetGlyphExtents((uint)glyph, out GlyphExtents expected));
            Assert.True(cut.TryGetGlyphExtents((uint)glyph, out GlyphExtents actual), $"glyph {glyph} has no outline in the subset");
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void A_glyph_that_was_not_used_has_no_outline_in_the_subset()
    {
        TrueTypeFont font = Load("NotoSans-Regular.ttf");
        int a = font.GlyphForCodePoint('A');
        int b = font.GlyphForCodePoint('B');
        int z = font.GlyphForCodePoint('Z');

        byte[] subset = TrueTypeSubsetter.Subset(font, [a, z]);

        using Blob blob = TestPaths.BlobOf(subset);
        using Face face = new(blob, 0);
        using Font cut = new(face);
        Assert.True(b > a && b < z, "the test needs B between A and Z");
        Assert.True(cut.TryGetGlyphExtents((uint)a, out GlyphExtents kept) && kept.Width != 0);
        Assert.True(!cut.TryGetGlyphExtents((uint)b, out GlyphExtents dropped) || (dropped.Width == 0 && dropped.Height == 0));
    }

    [Fact]
    public void The_subset_is_a_valid_sfnt_with_correct_checksums_sorted_tables_and_the_head_adjustment()
    {
        TrueTypeFont font = Load("NotoSans-Regular.ttf");

        byte[] subset = TrueTypeSubsetter.Subset(font, [font.GlyphForCodePoint('A'), font.GlyphForCodePoint('Ş')]);

        Assert.True(ChecksumOf(subset) == 0xB1B0AFBAu, "the checksum of the whole file must be 0xB1B0AFBA");
        int tables = BinaryPrimitives.ReadUInt16BigEndian(subset.AsSpan(4, 2));
        string previous = string.Empty;
        for (int i = 0; i < tables; i++)
        {
            int record = 12 + (i * 16);
            string tag = System.Text.Encoding.ASCII.GetString(subset, record, 4);
            Assert.True(string.CompareOrdinal(previous, tag) < 0, $"{tag} must come after {previous}");
            previous = tag;
            int offset = (int)BinaryPrimitives.ReadUInt32BigEndian(subset.AsSpan(record + 8, 4));
            int length = (int)BinaryPrimitives.ReadUInt32BigEndian(subset.AsSpan(record + 12, 4));
            Assert.Equal(0, offset % 4);
            byte[] table = subset.AsSpan(offset, length).ToArray();
            if (tag == "head")
            {
                BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(8, 4), 0);
            }

            Assert.Equal(ChecksumOf(table), BinaryPrimitives.ReadUInt32BigEndian(subset.AsSpan(record + 4, 4)));
        }
    }

    [Fact]
    public void A_latin_subset_is_small_and_the_same_subset_is_the_same_bytes()
    {
        TrueTypeFont font = Load("NotoSans-Regular.ttf");
        int[] glyphs = "Quarterly report: İstanbul, Größe".Select(c => font.GlyphForCodePoint(c)).ToArray();

        byte[] subset = TrueTypeSubsetter.Subset(font, glyphs);

        Assert.True(subset.Length < 25_000, $"a Latin subset is {subset.Length} bytes; the target is under 25 KB before compression");
        Assert.Equal(subset, TrueTypeSubsetter.Subset(font, [.. Enumerable.Reverse(glyphs)]));
    }

    [Fact]
    public void A_glyph_id_outside_the_font_is_refused()
    {
        TrueTypeFont font = Load("NotoSans-Regular.ttf");

        Assert.Throws<ArgumentOutOfRangeException>(() => TrueTypeSubsetter.Subset(font, [font.GlyphCount]));
        Assert.Throws<ArgumentOutOfRangeException>(() => font.Advance(-1));
    }

    private static uint ChecksumOf(byte[] data)
    {
        uint sum = 0;
        Span<byte> word = stackalloc byte[4];
        for (int i = 0; i < data.Length; i += 4)
        {
            word.Clear();
            data.AsSpan(i, Math.Min(4, data.Length - i)).CopyTo(word);
            sum = unchecked(sum + BinaryPrimitives.ReadUInt32BigEndian(word));
        }

        return sum;
    }
}
