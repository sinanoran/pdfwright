using System.Buffers.Binary;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Pdfwright.Benchmarks.Scenarios;
using Pdfwright.Tests.Document;

namespace Pdfwright.Tests.Pdf;

/// <summary>
/// Object streams (ISO 32000-1, 7.5.7) in compressed tagged files: the structure elements, the parent tree's arrays and the <c>StructTreeRoot</c> are
/// written in compressed object streams, and the cross-reference stream gives each its stream and index. Each tagged document of the pinned files is
/// written compressed too and read back by PdfPig through <see cref="StructureTreeReader"/>: the tree, the parent tree, the content and the text of
/// each element must be those of the uncompressed file, the cross-reference stream must point at every object, and qpdf must accept the file.
/// Untagged and uncompressed files have no object streams.
/// </summary>
public partial class TaggedObjectStreamTests
{
    /// <summary>The tagged documents of <see cref="TaggedOutputEquivalenceTests"/>, by name.</summary>
    public static TheoryData<string> TaggedDocuments() =>
        [.. TaggedOutputEquivalenceTests.Documents().Select(d => d.Name).Where(n => !n.EndsWith("-untagged", StringComparison.Ordinal))];

    [Theory]
    [MemberData(nameof(TaggedDocuments))]
    public void A_compressed_tagged_file_holds_what_the_uncompressed_one_holds(string name)
    {
        Func<PdfDocument> build = TaggedOutputEquivalenceTests.Documents().Single(d => d.Name == name).Build;
        byte[] plain = DocumentTestKit.Save(build());
        byte[] packed = DocumentTestKit.Save(Compressed(build()));

        Assert.Empty(StructureTreeReader.ObjectStreams(plain));
        Assert.NotEmpty(StructureTreeReader.ObjectStreams(packed));
        StructureTreeReader expected = TaggedFlowTests.Read(plain);
        StructureTreeReader actual = TaggedFlowTests.Read(packed);
        Assert.Equal(Describe(expected), Describe(actual));
        Assert.Equal(expected.ContentText, actual.ContentText);
        Assert.Equal(TaggedFlowTests.ElementTexts(plain, expected), TaggedFlowTests.ElementTexts(packed, actual));
        AssertCrossReference(packed);
        DocumentTestKit.AssertValid(packed, $"object-streams-{name}");
    }

    [Fact]
    public void A_long_tagged_table_fills_its_object_streams_to_their_limit_and_each_object_is_in_one()
    {
        using FontCollection fonts = ScenarioDocuments.LoadFonts(TestPaths.FontsDirectory);
        PdfDocument document = ScenarioDocuments.Table(fonts, tagged: true);
        byte[] pdf = DocumentTestKit.Save(document);

        StructureTreeReader tree = TaggedFlowTests.Read(pdf);
        List<int> counts = AssertCrossReference(pdf);

        // The elements, a parent-tree array for each page and the StructTreeRoot, in streams of at most the limit, all full but the last.
        Assert.Equal(tree.StructElemObjects + document.Diagnostics.PageCount + 1, counts.Sum());
        Assert.All(counts.SkipLast(1), n => Assert.Equal(Pdfwright.Pdf.PdfFile.PackedObjectLimit, n));
        Assert.InRange(counts[^1], 1, Pdfwright.Pdf.PdfFile.PackedObjectLimit);
    }

    [Fact]
    public void Untagged_and_uncompressed_files_have_no_object_streams_and_a_plain_cross_reference_stream()
    {
        using FontCollection fonts = ScenarioDocuments.LoadFonts(TestPaths.FontsDirectory);
        byte[] untagged = DocumentTestKit.Save(ScenarioDocuments.Table(fonts, rows: 50));
        byte[] uncompressed = DocumentTestKit.Save(TaggedOutputEquivalenceTests.Documents().Single(d => d.Name == "tables").Build());

        foreach (byte[] pdf in new[] { untagged, uncompressed })
        {
            string text = Encoding.Latin1.GetString(pdf);
            Assert.DoesNotContain("/ObjStm", text, StringComparison.Ordinal);
            Assert.DoesNotContain("/DecodeParms", text, StringComparison.Ordinal);
            Assert.Empty(AssertCrossReference(pdf));
        }
    }

    /// <summary>
    /// PDFium draws a compressed tagged file (object streams, a predicted cross-reference stream) pixel for pixel as the untagged file of the same
    /// document: tagging adds marked content around the drawing and changes nothing drawn.
    /// </summary>
    [Theory]
    [InlineData("tables")]
    [InlineData("figures")]
    [InlineData("report-20")]
    public void A_compressed_tagged_file_is_drawn_as_the_untagged_one(string name)
    {
        string? unavailable = RenderGolden.RendererUnavailable();
        if (unavailable is not null)
        {
            Assert.False(RenderGolden.Required, $"PDFWRIGHT_REQUIRE_RENDER=1 and {unavailable}");
            Assert.Skip(unavailable + "; set PDFWRIGHT_REQUIRE_RENDER=1 to make this a failure.");
            return;
        }

        Func<PdfDocument> build = TaggedOutputEquivalenceTests.Documents().Single(d => d.Name == name).Build;
        byte[] tagged = DocumentTestKit.Save(Compressed(build()));
        byte[] untagged = DocumentTestKit.Save(Compressed(build()).WithTagged(false));
        Assert.NotEmpty(StructureTreeReader.ObjectStreams(tagged));
        Assert.Empty(StructureTreeReader.ObjectStreams(untagged));

        List<RenderGolden.Page> expected = RenderGolden.Render(untagged);
        List<RenderGolden.Page> actual = RenderGolden.Render(tagged);
        Assert.Equal(expected.Count, actual.Count);
        for (int page = 0; page < expected.Count; page++)
        {
            Assert.Contains(actual[page].Rgb, b => b != 255);
            Assert.True(expected[page].Rgb.AsSpan().SequenceEqual(actual[page].Rgb), $"page {page + 1} of {name} is not drawn as the untagged page");
        }
    }

    [Fact]
    public void A_stream_is_never_put_in_an_object_stream_and_an_object_is_written_once()
    {
        using MemoryStream output = new();
        using Pdfwright.Pdf.PdfFile file = new(output, Pdfwright.Pdf.PdfCompression.Flate, objectStreams: true);
        Pdfwright.Pdf.PdfReference stream = file.Allocate();
        Pdfwright.Pdf.PdfReference packed = file.Allocate();

        Assert.Throws<ArgumentException>(() => file.WritePacked(stream, new Pdfwright.Pdf.PdfStream(new Pdfwright.Pdf.PdfDictionary(), new byte[10])));
        file.WritePacked(packed, new Pdfwright.Pdf.PdfDictionary().Set("Type", "Test"));
        Assert.Throws<InvalidOperationException>(() => file.WritePacked(packed.Number, "<<>>"u8));
        Assert.Throws<InvalidOperationException>(() => file.Write(packed, new Pdfwright.Pdf.PdfDictionary()));
    }

    /// <summary>A copy of a document written compressed (its options are its own, so they are changed through their init-only setter).</summary>
    internal static PdfDocument Compressed(PdfDocument document)
    {
        PdfDocument copy = document.WithTagged(true);
        PdfDocumentOptions options = (PdfDocumentOptions)typeof(PdfDocument).GetField("_options", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(copy)!;
        typeof(PdfDocumentOptions).GetProperty(nameof(PdfDocumentOptions.Compress))!.SetValue(options, true);
        return copy;
    }

    /// <summary>The tree as lines that do not depend on object numbers (which object streams change): each element by its place in the tree's order.</summary>
    private static List<string> Describe(StructureTreeReader tree)
    {
        List<StructureTreeReader.Element> elements = [.. tree.Root.Descendants()];
        Dictionary<long, int> index = [];
        for (int i = 0; i < elements.Count; i++)
        {
            index.Add(elements[i].Object, i);
        }

        List<string> lines = [];
        foreach (StructureTreeReader.Element e in elements)
        {
            string parent = e.ParentObject is { } p && index.TryGetValue(p, out int at) ? at.ToString(CultureInfo.InvariantCulture) : "root";
            string attributes = e.Attributes is null ? string.Empty : string.Join(" ", e.Attributes.Data.OrderBy(a => a.Key, StringComparer.Ordinal).Select(a => $"/{a.Key} {a.Value}"));
            string kids = string.Join(", ", e.Kids.Select(k => k switch
            {
                StructureTreeReader.ContentKid c => $"mcid {c.Page}/{c.Mcid}{(c.Reference ? " by reference" : string.Empty)}",
                StructureTreeReader.ChildKid c => $"element {index[c.Element.Object]}",
                _ => "?",
            }));
            lines.Add($"{index[e.Object]} {e.Type} under {parent} page {e.Page} alt [{e.Alt}] lang [{e.Language}] attributes [{attributes}] kids [{kids}]");
        }

        lines.Add($"struct parents [{string.Join(", ", tree.StructParents)}] tabs [{string.Join(", ", tree.Tabs)}] next key {tree.ParentTreeNextKey} marked {tree.Marked}");
        foreach ((int key, List<long> owners) in tree.ParentTree)
        {
            lines.Add($"parent tree {key}: {string.Join(" ", owners.Select(o => index[o]))}");
        }

        for (int page = 0; page < tree.ContentMcids.Count; page++)
        {
            lines.Add($"page {page}: {string.Join(" ", tree.ContentMcids[page])}");
        }

        return lines;
    }

    /// <summary>
    /// Reads the cross-reference stream the trailer points at, from the file's bytes: <c>/W [1 4 2]</c>, inflated, and the PNG "Up" predictor undone when
    /// <c>/DecodeParms</c> says so. Asserts that an object written in the file (type 1) starts at its offset, and that an object in an object stream
    /// (type 2) is the one at its index in that stream, which is itself in the file. Returns the number of objects of each object stream, in the order of the file.
    /// </summary>
    private static List<int> AssertCrossReference(byte[] pdf)
    {
        string text = Encoding.Latin1.GetString(pdf);
        int startxref = text.LastIndexOf("startxref\n", StringComparison.Ordinal);
        int offset = int.Parse(text[(startxref + 10)..text.IndexOf('\n', startxref + 10)], CultureInfo.InvariantCulture);
        Match xref = XrefPattern().Match(text, offset);
        Assert.True(xref.Success && xref.Index == offset, "the trailer points at the cross-reference stream");
        string dictionary = xref.Groups[1].Value;
        Assert.Contains("/W [1 4 2]", dictionary, StringComparison.Ordinal);
        int size = int.Parse(Regex.Match(dictionary, @"/Size (\d+)").Groups[1].Value, CultureInfo.InvariantCulture);
        int length = int.Parse(Regex.Match(dictionary, @"/Length (\d+)").Groups[1].Value, CultureInfo.InvariantCulture);
        byte[] data = pdf.AsSpan(xref.Index + xref.Length, length).ToArray();
        if (dictionary.Contains("/FlateDecode", StringComparison.Ordinal))
        {
            data = StructureTreeReader.Inflate(data);
        }

        const int Width = 7;
        if (dictionary.Contains("/DecodeParms", StringComparison.Ordinal))
        {
            Assert.Contains("/DecodeParms <<\n/Predictor 12\n/Columns 7\n>>", dictionary, StringComparison.Ordinal);
            Assert.Equal(size * (Width + 1), data.Length);
            byte[] rows = new byte[size * Width];
            for (int row = 0; row < size; row++)
            {
                Assert.Equal(2, data[row * (Width + 1)]);
                for (int i = 0; i < Width; i++)
                {
                    byte above = row == 0 ? (byte)0 : rows[((row - 1) * Width) + i];
                    rows[(row * Width) + i] = (byte)(data[(row * (Width + 1)) + 1 + i] + above);
                }
            }

            data = rows;
        }

        Assert.Equal(size * Width, data.Length);
        Dictionary<int, List<int>> streams = [];
        List<int> order = [];
        for (int number = 1; number < size; number++)
        {
            ReadOnlySpan<byte> entry = data.AsSpan(number * Width, Width);
            uint field = BinaryPrimitives.ReadUInt32BigEndian(entry[1..5]);
            ushort last = BinaryPrimitives.ReadUInt16BigEndian(entry[5..7]);
            switch (entry[0])
            {
                case 1:
                    Assert.Equal(0, last);
                    Assert.StartsWith($"{number} 0 obj\n", text[(int)field..], StringComparison.Ordinal);
                    break;
                case 2:
                    if (!streams.TryGetValue((int)field, out List<int>? numbers))
                    {
                        numbers = ObjectStreamNumbers(pdf, text, data, (int)field);
                        streams.Add((int)field, numbers);
                        order.Add((int)field);
                    }

                    Assert.True(last < numbers.Count, $"object {number} is at index {last} of object stream {field}, which holds {numbers.Count}");
                    Assert.Equal(number, numbers[last]);
                    break;
                default:
                    Assert.Fail($"object {number} has an entry of type {entry[0]}");
                    break;
            }
        }

        // Every object an object stream holds has its entry there.
        int entries = 0;
        for (int number = 1; number < size; number++)
        {
            entries += data[number * Width] == 2 ? 1 : 0;
        }

        Assert.Equal(streams.Values.Sum(n => n.Count), entries);
        return [.. order.OrderBy(n => n).Select(n => streams[n].Count)];
    }

    /// <summary>The object numbers an object stream holds, in order, read from its first <c>/N</c> pairs; the stream itself is an object in the file.</summary>
    private static List<int> ObjectStreamNumbers(byte[] pdf, string text, byte[] xref, int stream)
    {
        const int Width = 7;
        ReadOnlySpan<byte> entry = xref.AsSpan(stream * Width, Width);
        Assert.Equal(1, entry[0]);
        int offset = (int)BinaryPrimitives.ReadUInt32BigEndian(entry[1..5]);
        Match match = ObjectStreamPattern().Match(text, offset);
        Assert.True(match.Success && match.Index == offset, $"object {stream} is an object stream");
        Assert.Equal(stream, int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture));
        int count = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        int first = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
        byte[] data = StructureTreeReader.Inflate(pdf.AsSpan(match.Index + match.Length, int.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture)));
        Assert.InRange(count, 1, Pdfwright.Pdf.PdfFile.PackedObjectLimit);
        int[] pairs = [.. Encoding.Latin1.GetString(data, 0, first).Split([' ', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(s => int.Parse(s, CultureInfo.InvariantCulture))];
        Assert.Equal(count * 2, pairs.Length);
        List<int> numbers = [];
        for (int i = 0; i < count; i++)
        {
            numbers.Add(pairs[2 * i]);
            Assert.True(i == 0 ? pairs[1] == 0 : pairs[(2 * i) + 1] > pairs[(2 * i) - 1], "the offsets start at 0 and increase");
            Assert.True(first + pairs[(2 * i) + 1] < data.Length);
        }

        // No stream in an object stream.
        Assert.DoesNotContain("endstream", Encoding.Latin1.GetString(data, first, data.Length - first), StringComparison.Ordinal);
        return numbers;
    }

    [GeneratedRegex(@"\d+ 0 obj\n(<<\n/Type /XRef\n(?:[^>]|>(?!>\nstream))*>>)\nstream\n")]
    private static partial Regex XrefPattern();

    [GeneratedRegex(@"(\d+) 0 obj\n<<\n/Type /ObjStm\n/N (\d+)\n/First (\d+)\n/Filter /FlateDecode\n/Length (\d+)\n>>\nstream\n")]
    private static partial Regex ObjectStreamPattern();
}
