using System.Buffers.Binary;

namespace Pdfwright.Tests.Document;

/// <summary>The font collection: what it reads from a font, what it refuses, how it picks and how it behaves under threads.</summary>
public class FontCollectionTests
{
    private static byte[] Bytes(string file) => File.ReadAllBytes(TestPaths.Font(file));

    private static void SetFsType(byte[] font, ushort value)
    {
        int count = BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(4, 2));
        for (int i = 0; i < count; i++)
        {
            int record = 12 + (i * 16);
            if (System.Text.Encoding.ASCII.GetString(font, record, 4) == "OS/2")
            {
                int offset = (int)BinaryPrimitives.ReadUInt32BigEndian(font.AsSpan(record + 8, 4));
                BinaryPrimitives.WriteUInt16BigEndian(font.AsSpan(offset + 8, 2), value);
                return;
            }
        }

        throw new InvalidOperationException("no OS/2 table");
    }

    [Fact]
    public void Family_weight_and_style_are_read_from_the_fonts()
    {
        FontCollection fonts = new FontCollection().Add(Bytes("NotoSans-Regular.ttf")).Add(Bytes("NotoSans-Bold.ttf")).Add(Bytes("NotoSans-Italic.ttf"));

        FontCollection.FontEntry[] entries = fonts.Current.Entries;

        Assert.Equal(3, fonts.Count);
        Assert.All(entries, e => Assert.Equal("Noto Sans", e.Family));
        Assert.Equal([400, 700, 400], entries.Select(e => e.Weight));
        Assert.Equal([false, false, true], entries.Select(e => e.Italic));
        Assert.Equal(["Noto Sans"], fonts.Families);
    }

    [Fact]
    public void Add_returns_the_collection_and_accepts_memory()
    {
        FontCollection fonts = new();

        Assert.Same(fonts, fonts.Add(Bytes("NotoSans-Regular.ttf")));
        Assert.Same(fonts, fonts.Add(new ReadOnlyMemory<byte>(Bytes("NotoSansArabic-Regular.ttf"))));
        Assert.Equal(2, fonts.Count);
    }

    [Fact]
    public void A_font_added_twice_is_kept_once()
    {
        FontCollection fonts = new FontCollection().Add(Bytes("NotoSans-Regular.ttf")).Add(Bytes("NotoSans-Regular.ttf"));

        Assert.Equal(1, fonts.Count);
    }

    [Fact]
    public void A_font_is_copied_so_the_array_can_change_afterwards()
    {
        byte[] bytes = Bytes("NotoSans-Regular.ttf");
        FontCollection fonts = new FontCollection().Add(bytes);
        Array.Clear(bytes);

        Assert.True(fonts.Current.Entries[0].Font.GlyphForCodePoint('A') != 0);
    }

    [Theory]
    [InlineData(0x0002)]
    [InlineData(0x0100)]
    [InlineData(0x0200)]
    public void A_font_that_may_not_be_embedded_is_refused_with_a_typed_exception(int fsType)
    {
        byte[] font = Bytes("NotoSans-Regular.ttf");
        SetFsType(font, (ushort)fsType);
        FontCollection fonts = new();

        FontNotEmbeddableException error = Assert.Throws<FontNotEmbeddableException>(() => fonts.Add(font));

        Assert.IsAssignableFrom<ArgumentException>(error);
        Assert.Equal("Noto Sans", error.Family);
        Assert.Equal(fsType, error.EmbeddingPermission);
        Assert.Equal(0, fonts.Count);
    }

    [Theory]
    [InlineData(0x0000)]
    [InlineData(0x0004)]
    [InlineData(0x0008)]
    public void A_font_that_allows_a_subset_is_accepted(int fsType)
    {
        byte[] font = Bytes("NotoSans-Regular.ttf");
        SetFsType(font, (ushort)fsType);

        Assert.Equal(1, new FontCollection().Add(font).Count);
    }

    [Fact]
    public void Data_that_is_not_a_truetype_font_is_an_argument_exception()
    {
        FontCollection fonts = new();

        Assert.Throws<ArgumentNullException>(() => fonts.Add((byte[])null!));
        Assert.Throws<ArgumentException>(() => fonts.Add(new byte[] { 1, 2, 3 }));
        Assert.Throws<ArgumentException>(() => fonts.Add(new byte[200]));
        byte[] cff = Bytes("NotoSans-Regular.ttf");
        cff[0] = (byte)'O';
        cff[1] = (byte)'T';
        cff[2] = (byte)'T';
        cff[3] = (byte)'O';
        Assert.Throws<ArgumentException>(() => fonts.Add(cff));
        Assert.Equal(0, fonts.Count);
    }

    [Fact]
    public void The_order_puts_the_family_first_then_the_style_then_the_nearest_weight()
    {
        FontCollection fonts = DocumentTestKit.Fonts;
        FontCollection.Snapshot snapshot = fonts.Current;
        string[] files = DocumentTestKit.AllFonts;
        int Index(string file) => Array.IndexOf(files, file);

        int[] regular = snapshot.Order("Noto Sans", 400, false);
        int[] bold = snapshot.Order("noto sans", 700, false);
        int[] italic = snapshot.Order("Noto Sans", 400, true);
        int[] semibold = snapshot.Order("Noto Sans", 600, false);

        Assert.Equal(files.Length, regular.Length);
        Assert.Equal(Index("NotoSans-Regular.ttf"), regular[0]);
        Assert.Equal(Index("NotoSans-Bold.ttf"), bold[0]);
        Assert.Equal(Index("NotoSans-Italic.ttf"), italic[0]);
        Assert.Equal(Index("NotoSans-Bold.ttf"), semibold[0]);
        Assert.Equal(Index("NotoSans-Regular.ttf"), snapshot.Order("Noto Sans", 500, false)[0]);

        // Every family of the collection is still in the list: a family that lacks a script falls back to the others.
        Assert.Equal(Enumerable.Range(0, files.Length).Order(), regular.Order());
    }

    [Fact]
    public void A_family_that_is_not_in_the_collection_still_gets_every_font()
    {
        int[] order = DocumentTestKit.Fonts.Current.Order("Nonexistent", 400, false);

        Assert.Equal(DocumentTestKit.AllFonts.Length, order.Length);
    }

    [Fact]
    public async Task Fonts_can_be_added_from_many_threads_at_once()
    {
        FontCollection fonts = new();
        string[] files = DocumentTestKit.AllFonts;
        byte[][] data = [.. files.Select(Bytes)];

        await Task.WhenAll(Enumerable.Range(0, 32).Select(i => Task.Run(() => fonts.Add(data[i % data.Length]))));

        Assert.Equal(files.Length, fonts.Count);
    }

    [Fact]
    public async Task A_shared_collection_serves_documents_on_several_threads()
    {
        byte[][] results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() =>
        {
            PdfDocument document = new(DocumentTestKit.Options());
            document.Add(new Heading(1, "Shared"));
            document.Add(new Paragraph(DocumentTestKit.LongText(300)));
            return DocumentTestKit.Save(document);
        })));

        Assert.All(results, r => Assert.True(results[0].AsSpan().SequenceEqual(r)));
    }

    [Fact]
    public void More_than_256_spellings_from_several_threads_give_the_orders_the_general_lookup_gives()
    {
        // A fresh snapshot has no spelling remembered; past 256 of them it stops remembering, and the threads that find the table full together may pass the limit by a few. None of that may change an order.
        FontCollection.Snapshot snapshot = new(DocumentTestKit.Fonts.Current.Entries);
        FontCollection.FontEntry[] entries = snapshot.Entries;
        List<(string Family, int Weight, bool Italic)> keys = [];
        string[] known = ["Noto Sans", "noto sans", "NOTO SANS", "Noto Sans Arabic", "noto sans hebrew", "Missing", string.Empty];
        foreach (string family in known)
        {
            foreach (int weight in new[] { 100, 400, 450, 700 })
            {
                keys.Add((family, weight, false));
                keys.Add((family, weight, true));
            }
        }

        for (int i = 0; i < 600; i++)
        {
            keys.Add(("Family " + i, 300 + (i % 5 * 100), i % 2 == 0));
        }

        // The general lookup, as it is without any table: the family first, then the style, then the nearest weight, then the order they were added in.
        int[][] expected = [.. keys.Select(k => Enumerable.Range(0, entries.Length)
            .OrderBy(i => string.Equals(entries[i].Family, k.Family, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(i => entries[i].Italic == k.Italic ? 0 : 1)
            .ThenBy(i => Math.Abs(entries[i].Weight - k.Weight))
            .ThenBy(i => i)
            .ToArray())];

        ParallelOptions options = new() { MaxDegreeOfParallelism = 8, CancellationToken = TestContext.Current.CancellationToken };
        Parallel.For(0, keys.Count * 6, options, n =>
        {
            int i = (n * 7) % keys.Count;
            Assert.Equal(expected[i], snapshot.Order(keys[i].Family, keys[i].Weight, keys[i].Italic));
        });

        for (int i = 0; i < keys.Count; i++)
        {
            Assert.Equal(expected[i], snapshot.Order(keys[i].Family, keys[i].Weight, keys[i].Italic));
        }
    }
}
