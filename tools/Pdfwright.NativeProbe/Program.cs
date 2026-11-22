using System.Buffers.Binary;
using Pdfwright.Fonts;
using Pdfwright.Text;

// Calls hb-subset through the native library Pdfwright ships and checks the result: Pdfwright.NativeProbe
// Exit code 0: every export is there and a subset of three glyphs came out right. 1: a check failed. 2: the native library could not do it.
try
{
    IReadOnlyList<string> missing = HarfBuzzSubsetNative.MissingExports();
    Console.WriteLine($"Platform: {HarfBuzzSubsetNative.Platform()}");
    Console.WriteLine($"Library: {HarfBuzzSubsetNative.Library}; exports checked: {HarfBuzzSubsetNative.RequiredExports.Length + 1}; missing: {(missing.Count == 0 ? "none" : string.Join(", ", missing))}");
    if (missing.Count > 0)
    {
        return 2;
    }

    byte[] bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "NotoSans-Regular.ttf"));
    TrueTypeFont font = TrueTypeFont.Parse(bytes);
    int[] glyphs = [.. "ABC".Select(c => font.GlyphForCodePoint(c))];
    HarfBuzzSubset subset = HarfBuzzSubsetter.Subset(bytes, glyphs);

    // maxp.numGlyphs of the result: .notdef and the three letters.
    int tables = BinaryPrimitives.ReadUInt16BigEndian(subset.Font.AsSpan(4));
    int numGlyphs = -1;
    for (int i = 0; i < tables; i++)
    {
        ReadOnlySpan<byte> record = subset.Font.AsSpan(12 + (i * 16), 16);
        if (record[..4].SequenceEqual("maxp"u8))
        {
            numGlyphs = BinaryPrimitives.ReadUInt16BigEndian(subset.Font.AsSpan((int)BinaryPrimitives.ReadUInt32BigEndian(record[8..]) + 4));
        }
    }

    Console.WriteLine($"Subset: {subset.Font.Length} bytes of {bytes.Length}, {numGlyphs} glyphs, map {string.Join(", ", subset.OldToNew.Select(p => $"{p.Key}->{p.Value}"))}");
    bool right = numGlyphs == 4 && subset.OldToNew.Count == 4 && subset.OldToNew[0] == 0 && glyphs.Select(g => subset.OldToNew[g]).Order().SequenceEqual([1, 2, 3]);
    Console.WriteLine(right ? "hb-subset works." : "hb-subset gave an unexpected result.");
    return right ? 0 : 1;
}
catch (PlatformNotSupportedException e)
{
    Console.Error.WriteLine(e.Message);
    return 2;
}
