using System.Buffers.Binary;
using HarfBuzzSharp;
using Pdfwright.Fonts;
using Pdfwright.Text;

namespace Pdfwright.Tests.FontEngine;

/// <summary>
/// The hb-subset binding (slice C1): the exports are in the native library HarfBuzzSharp ships, and a subset made through them is a font with the glyphs asked for.
/// These tests never skip: a platform whose library lacks an export fails here (and the same call runs in the .NET runtime images of the CI job linux-runtime-images, through tools/Pdfwright.NativeProbe).
/// </summary>
public class HarfBuzzSubsetterTests
{
    private static readonly byte[] NotoSans = File.ReadAllBytes(TestPaths.Font("NotoSans-Regular.ttf"));

    [Fact]
    public void Every_export_the_subsetter_needs_is_in_the_native_library()
    {
        Assert.Empty(HarfBuzzSubsetNative.MissingExports());
        HarfBuzzSubsetNative.EnsureAvailable(needPin: true);
    }

    [Fact]
    public void A_subset_of_three_letters_has_four_glyphs_and_the_map_of_the_old_ones()
    {
        TrueTypeFont font = TrueTypeFont.Parse(NotoSans);
        int[] old = [.. "CAB".Select(c => font.GlyphForCodePoint(c))];

        HarfBuzzSubset subset = HarfBuzzSubsetter.Subset(NotoSans, old);

        Assert.True(subset.Font.Length < NotoSans.Length / 10);
        Assert.Equal(4, NumGlyphs(subset.Font));

        // .notdef stays 0 and the others are numbered in the order of the old ids: A, B, C are consecutive in Noto Sans, so 1, 2, 3.
        Assert.Equal(4, subset.OldToNew.Count);
        Assert.Equal(0, subset.OldToNew[0]);
        int[] ordered = [.. old.Order()];
        Assert.Equal([1, 2, 3], ordered.Select(g => subset.OldToNew[g]));
        Assert.DoesNotContain(font.GlyphForCodePoint('D'), subset.OldToNew.Keys);
    }

    [Fact]
    public void A_glyph_keeps_its_advance_and_its_outline_under_its_new_number()
    {
        TrueTypeFont font = TrueTypeFont.Parse(NotoSans);
        int[] old = [.. "Hg".Select(c => font.GlyphForCodePoint(c))];
        HarfBuzzSubset subset = HarfBuzzSubsetter.Subset(NotoSans, old);

        using Blob originalBlob = TestPaths.BlobOf(NotoSans);
        using Face originalFace = new(originalBlob, 0);
        using HarfBuzzSharp.Font original = new(originalFace);
        using Blob subsetBlob = TestPaths.BlobOf(subset.Font);
        using Face subsetFace = new(subsetBlob, 0);
        using HarfBuzzSharp.Font reduced = new(subsetFace);

        foreach (int glyph in old)
        {
            int moved = subset.OldToNew[glyph];
            Assert.Equal(original.GetHorizontalGlyphAdvance((uint)glyph), reduced.GetHorizontalGlyphAdvance((uint)moved));
            Assert.True(original.TryGetGlyphExtents((uint)glyph, out GlyphExtents before));
            Assert.True(reduced.TryGetGlyphExtents((uint)moved, out GlyphExtents after));
            Assert.Equal(before, after);
        }
    }

    [Fact]
    public void Retaining_glyph_ids_keeps_the_numbers()
    {
        TrueTypeFont font = TrueTypeFont.Parse(NotoSans);
        int a = font.GlyphForCodePoint('A');

        HarfBuzzSubset subset = HarfBuzzSubsetter.Subset(NotoSans, [a], HarfBuzzSubsetFlags.RetainGlyphIds);

        Assert.Equal(a, subset.OldToNew[a]);
        Assert.Equal(a + 1, NumGlyphs(subset.Font));
    }

    [Fact]
    public void A_glyph_outside_the_font_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => HarfBuzzSubsetter.Subset(NotoSans, [TrueTypeFont.Parse(NotoSans).GlyphCount]));
        Assert.Throws<ArgumentOutOfRangeException>(() => HarfBuzzSubsetter.Subset(NotoSans, [-1]));
    }

    [Fact]
    public void A_subset_can_be_made_from_the_face_of_a_shaping_font_and_repeated()
    {
        using ShapingFont shaping = new(TrueTypeFont.Parse(NotoSans));
        HarfBuzzFont handle = Assert.IsType<HarfBuzzFont>(shaping.Handle);

        HarfBuzzSubset first = HarfBuzzSubsetter.Subset(handle, [shaping.Font.GlyphForCodePoint('A')]);
        HarfBuzzSubset second = HarfBuzzSubsetter.Subset(handle, [shaping.Font.GlyphForCodePoint('A')]);

        Assert.Equal(first.Font, second.Font);
        Assert.Equal(2, first.OldToNew.Count);
    }

    [Fact]
    public void Bytes_that_are_not_a_font_are_refused_with_the_documented_exception()
    {
        Assert.Throws<InvalidDataException>(() => HarfBuzzSubsetter.Subset(new byte[100], [0]));
        Assert.Throws<InvalidDataException>(() => HarfBuzzSubsetter.Subset(new byte[100], []));
        Assert.Throws<InvalidDataException>(() => HarfBuzzSubsetter.Subset([], []));
    }

    [Fact]
    public void A_missing_export_is_reported_with_the_platform_and_not_as_a_crash()
    {
        PlatformNotSupportedException e = HarfBuzzSubsetNative.Unavailable("it does not export hb_map_next", new EntryPointNotFoundException());

        Assert.Contains(HarfBuzzSubsetNative.Library, e.Message);
        Assert.Contains(System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(), e.Message);
        Assert.Contains("hb_map_next", e.Message);
        Assert.IsType<EntryPointNotFoundException>(e.InnerException);
    }

    [Fact]
    public void A_disposed_font_throws_ObjectDisposedException_and_does_not_reach_freed_memory()
    {
        ShapingFont shaping = new(TrueTypeFont.Parse(NotoSans));
        HarfBuzzFont handle = Assert.IsType<HarfBuzzFont>(shaping.Handle);
        int a = shaping.Font.GlyphForCodePoint('A');
        shaping.Dispose();

        Assert.Throws<ObjectDisposedException>(() => HarfBuzzSubsetter.Subset(handle, [a]));
        Assert.Throws<ObjectDisposedException>(() => HarfBuzzSubsetter.Subset(handle, []));
        Assert.Throws<ObjectDisposedException>(() => shaping.Shaper.Shape(handle, "A", 0, 1, false, "Latn", "en"));
        Assert.Throws<ObjectDisposedException>(() => handle.Face);
        Assert.Throws<ObjectDisposedException>(() => handle.Font);
    }

    [Fact]
    public void A_font_can_be_disposed_twice()
    {
        ShapingFont shaping = new(TrueTypeFont.Parse(NotoSans));
        shaping.Dispose();
        shaping.Dispose();
        HarfBuzzFont handle = new(NotoSans, 1000);
        handle.Dispose();
        handle.Dispose();
    }

    [Fact]
    public void Pinning_the_axes_of_a_static_font_succeeds_because_there_is_nothing_to_pin()
    {
        TrueTypeFont font = TrueTypeFont.Parse(NotoSans);
        int a = font.GlyphForCodePoint('A');

        HarfBuzzSubset pinned = HarfBuzzSubsetter.Subset(NotoSans, [a], pinAxesToDefault: true);
        HarfBuzzSubset plain = HarfBuzzSubsetter.Subset(NotoSans, [a]);

        Assert.Equal(2, pinned.OldToNew.Count);
        Assert.Equal(plain.Font, pinned.Font);
    }

    [Fact]
    public void The_map_holds_what_the_closure_added_to_the_glyphs_asked_for()
    {
        TrueTypeFont font = TrueTypeFont.Parse(NotoSans);
        int f = font.GlyphForCodePoint('f');
        int i = font.GlyphForCodePoint('i');

        HarfBuzzSubset subset = HarfBuzzSubsetter.Subset(NotoSans, [f, i]);

        Assert.Contains(f, subset.OldToNew.Keys);
        Assert.Contains(i, subset.OldToNew.Keys);
        Assert.True(subset.OldToNew.Count > 3, "hb-subset runs the GSUB closure, so ligature glyphs of f and i are kept too");
        Assert.Equal(subset.OldToNew.Count, NumGlyphs(subset.Font));
    }

    private static int NumGlyphs(byte[] font)
    {
        int tables = BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(4));
        for (int i = 0; i < tables; i++)
        {
            ReadOnlySpan<byte> record = font.AsSpan(12 + (i * 16), 16);
            if (record[..4].SequenceEqual("maxp"u8))
            {
                return BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan((int)BinaryPrimitives.ReadUInt32BigEndian(record[8..]) + 4));
            }
        }

        throw new InvalidDataException("no maxp");
    }
}
