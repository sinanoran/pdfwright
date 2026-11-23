using System.Runtime;
using System.Runtime.CompilerServices;
using Pdfwright.Fonts;
using Pdfwright.Text;

namespace Pdfwright.Tests.FontEngine;

/// <summary>
/// HarfBuzz reads a font's bytes in place, from the array the TrueType reader reads, pinned for as long as HarfBuzz holds them, instead of from a copy in native memory. The pin must hold while the heap is
/// compacted around the array, and must be let go when the font is disposed, so that a disposed collection leaves nothing behind.
/// </summary>
public class SharedFontBytesTests
{
    private static readonly string[] Texts = ["Row 12 cell 3 text", "office ﬁne Ta", "مرحبا بالعالم", "straße", "1,234.50"];

    private static string Shape(FontCollection fonts) =>
        string.Join("|", Texts.Select(text => string.Join(";", TextShaper.Shape(text, fonts.Current.Shaping, language: "en").Runs.SelectMany(r => r.Glyphs).Select(g => $"{g.Glyph}/{g.Cluster}/{g.XAdvance}/{g.XOffset}/{g.YOffset}"))));

    private static FontCollection Load(params string[] files)
    {
        FontCollection fonts = new();
        foreach (string file in files)
        {
            fonts.Add(File.ReadAllBytes(TestPaths.Font(file)));
        }

        return fonts;
    }

    [Fact]
    public void The_bytes_a_collection_keeps_are_its_one_copy_on_the_pinned_object_heap()
    {
        // A font smaller than the large object heap's threshold (85,000 bytes): copied to the ordinary heap it would be in generation 0 right after it is added, and pinned there by the shaping engine.
        // On the pinned object heap it is in the oldest generation from the start, and it is the array the parser keeps (no second copy that leaves the pinned one as garbage).
        byte[] file = File.ReadAllBytes(TestPaths.Font("NotoSansHebrew-Regular.ttf"));
        Assert.True(file.Length < 85_000);
        using FontCollection fonts = new();
        fonts.Add(file);
        byte[] kept = fonts.Current.Entries[0].Font.RawBytes;
        Assert.NotSame(file, kept);
        Assert.Equal(file, kept);
        Assert.Equal(GC.MaxGeneration, GC.GetGeneration(kept));
    }

    [Fact]
    public void Parsing_owned_bytes_keeps_the_array_and_parsing_a_caller_s_bytes_copies_them()
    {
        byte[] file = File.ReadAllBytes(TestPaths.Font("NotoSansHebrew-Regular.ttf"));
        Assert.Same(file, TrueTypeFont.ParseOwned(file).RawBytes);
        Assert.NotSame(file, TrueTypeFont.Parse(file).RawBytes);
    }

    [Fact]
    public void The_glyphs_are_the_same_after_the_heap_is_compacted_around_the_fonts()
    {
        using FontCollection fonts = Load("NotoSans-Regular.ttf", "NotoSansArabic-Regular.ttf", "NotoSansHebrew-Regular.ttf");
        string before = Shape(fonts);
        for (int i = 0; i < 3; i++)
        {
            // Garbage around the fonts, then a full collection that compacts both heaps: an array HarfBuzz reads that was not pinned could move under it.
            _ = Enumerable.Range(0, 1000).Select(n => new byte[n * 97 % 4000]).ToList();
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            using FontCollection fresh = Load("NotoSans-Regular.ttf", "NotoSansArabic-Regular.ttf", "NotoSansHebrew-Regular.ttf");
            Assert.Equal(before, Shape(fonts));
            Assert.Equal(before, Shape(fresh));
        }
    }

    [Fact]
    public void A_disposed_collection_lets_go_of_the_bytes_of_its_fonts()
    {
        WeakReference[] bytes = LoadShapeAndDispose();
        for (int i = 0; i < 5 && bytes.Any(b => b.IsAlive); i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        Assert.All(bytes, b => Assert.False(b.IsAlive, "the bytes of a disposed font are still held (pinned)"));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] LoadShapeAndDispose()
    {
        FontCollection fonts = Load("NotoSans-Regular.ttf", "NotoSansHebrew-Regular.ttf");
        _ = Shape(fonts);
        WeakReference[] bytes = [.. fonts.Current.Entries.Select(e => new WeakReference(e.Font.RawBytes))];
        fonts.Dispose();
        return bytes;
    }
}
