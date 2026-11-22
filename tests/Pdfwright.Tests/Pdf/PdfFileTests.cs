using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using Pdfwright.Pdf;

namespace Pdfwright.Tests.Pdf;

/// <summary>The file structure under the document: objects, streams, the cross-reference, the id, and what is refused.</summary>
public class PdfFileTests
{
    private static string Latin(byte[] bytes) => Encoding.Latin1.GetString(bytes);

    private static byte[] Build(Action<PdfFile> content, PdfCompression compression = PdfCompression.None)
    {
        using MemoryStream stream = new();
        using (PdfFile file = new(stream, compression))
        {
            content(file);
        }

        return stream.ToArray();
    }

    private static PdfReference Skeleton(PdfFile file)
    {
        PdfReference pages = file.Allocate();
        file.Write(pages, new PdfDictionary().Set("Type", "Pages").Set("Count", 0).Set("Kids", new PdfArray()));
        return file.Add(new PdfDictionary().Set("Type", "Catalog").Set("Pages", pages));
    }

    [Fact]
    public void Every_cross_reference_entry_points_at_its_own_object_and_the_free_entry_is_the_standard_one()
    {
        byte[] pdf = PdfTestKit.ThreePages(PdfCompression.None);
        string text = Latin(pdf);

        // The xref stream is the last object; decode it (uncompressed here) and look at every entry.
        long xrefOffset = long.Parse(Regex.Match(text, @"startxref\n(\d+)\n%%EOF\n$").Groups[1].Value, CultureInfo.InvariantCulture);
        Match header = Regex.Match(text[(int)xrefOffset..], @"^(\d+) 0 obj\n<<[\s\S]*?>>\nstream\n");
        string dictionary = header.Value;
        int size = int.Parse(Regex.Match(dictionary, @"/Size (\d+)").Groups[1].Value, CultureInfo.InvariantCulture);
        int[] widths = Regex.Match(dictionary, @"/W \[(\d+) (\d+) (\d+)\]").Groups.Cast<Group>().Skip(1).Select(g => int.Parse(g.Value, CultureInfo.InvariantCulture)).ToArray();
        int length = int.Parse(Regex.Match(dictionary, @"/Length (\d+)").Groups[1].Value, CultureInfo.InvariantCulture);
        Assert.Equal([1, 4, 2], widths);
        Assert.Equal(length, size * 7);
        int data = (int)xrefOffset + header.Length;

        Assert.Equal([0, 0, 0, 0, 0, 0xFF, 0xFF], pdf[data..(data + 7)].Select(b => (int)b).ToArray());
        for (int number = 1; number < size; number++)
        {
            int entry = data + (number * 7);
            Assert.Equal(1, pdf[entry]);
            long offset = BinaryPrimitives.ReadUInt32BigEndian(pdf.AsSpan(entry + 1, 4));
            Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(pdf.AsSpan(entry + 5, 2)));
            Assert.StartsWith($"{number} 0 obj\n", text[(int)offset..]);
        }

        Assert.StartsWith($"{size - 1} 0 obj\n", text[(int)xrefOffset..]);
    }

    [Fact]
    public void The_document_id_is_the_hash_of_every_byte_before_the_cross_reference()
    {
        byte[] pdf = PdfTestKit.ThreePages(PdfCompression.None);
        string text = Latin(pdf);
        long xrefOffset = long.Parse(Regex.Match(text, @"startxref\n(\d+)\n%%EOF\n$").Groups[1].Value, CultureInfo.InvariantCulture);
        Match id = Regex.Match(text, @"/ID \[<([0-9A-F]{32})> <([0-9A-F]{32})>\]");

        string expected = Convert.ToHexString(SHA256.HashData(pdf.AsSpan(0, (int)xrefOffset)))[..32];

        Assert.True(id.Success);
        Assert.Equal(expected, id.Groups[1].Value);
        Assert.Equal(expected, id.Groups[2].Value);
    }

    [Fact]
    public void A_stream_that_already_has_a_filter_keeps_it_and_is_never_compressed_again()
    {
        byte[] data = new byte[2000];
        byte[] pdf = Build(
            file =>
            {
                PdfReference root = Skeleton(file);
                file.Add(new PdfStream(new PdfDictionary().Set("Filter", "DCTDecode").Set("Type", "XObject"), data));
                file.Finish(root, null);
            },
            PdfCompression.Flate);

        string text = Latin(pdf);

        Assert.Contains("/Filter /DCTDecode", text);
        Assert.DoesNotContain("FlateDecode\n/Length 2000", text);
        Assert.Contains("/Length 2000", text);
    }

    [Fact]
    public void A_small_stream_and_one_that_does_not_shrink_stay_as_they_are_and_a_repetitive_one_is_deflated()
    {
        byte[] random = RandomNumberGenerator.GetBytes(1000);
        byte[] repetitive = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("0 0 m 10 10 l S\n", 200)));
        byte[] pdf = Build(
            file =>
            {
                PdfReference root = Skeleton(file);
                file.Add(new PdfStream(new PdfDictionary().Set("Name", "small"), new byte[40]));
                file.Add(new PdfStream(new PdfDictionary().Set("Name", "random"), random));
                file.Add(new PdfStream(new PdfDictionary().Set("Name", "repetitive"), repetitive));
                file.Finish(root, null);
            },
            PdfCompression.Flate);

        string text = Latin(pdf);

        Assert.Matches(@"/Name /small\n/Length 40\n>>", text);
        Assert.Matches(@"/Name /random\n/Length 1000\n>>", text);
        Assert.Matches(@"/Name /repetitive\n/Filter /FlateDecode\n/Length \d+\n>>", text);
    }

    [Fact]
    public void A_metadata_stream_is_never_compressed()
    {
        byte[] pdf = PdfTestKit.ThreePages(PdfCompression.Flate);

        Assert.Contains("<x:xmpmeta", Latin(pdf));
    }

    [Fact]
    public void An_object_number_that_was_never_allocated_object_zero_and_a_second_write_are_refused()
    {
        using MemoryStream stream = new();
        using PdfFile file = new(stream, PdfCompression.None);
        PdfReference one = file.Allocate();

        Assert.Throws<ArgumentOutOfRangeException>(() => file.Write(new PdfReference(0), PdfNull.Instance));
        Assert.Throws<ArgumentOutOfRangeException>(() => file.Write(new PdfReference(99), PdfNull.Instance));
        file.Write(one, PdfNull.Instance);
        Assert.Contains("twice", Assert.Throws<InvalidOperationException>(() => file.Write(one, PdfNull.Instance)).Message);
    }

    [Fact]
    public void An_object_that_fails_halfway_stops_the_file_instead_of_leaving_it_to_be_finished_wrongly()
    {
        using MemoryStream stream = new();
        using PdfFile file = new(stream, PdfCompression.None);
        PdfReference root = Skeleton(file);
        PdfReference bad = file.Allocate();
        PdfDictionary nested = new PdfDictionary().Set("Inner", new PdfStream(new PdfDictionary(), new byte[1]));

        Assert.Throws<InvalidOperationException>(() => file.Write(bad, nested));

        Assert.Contains("failed halfway", Assert.Throws<InvalidOperationException>(() => file.Finish(root, null)).Message);
        Assert.Contains("failed halfway", Assert.Throws<InvalidOperationException>(() => file.Add(PdfNull.Instance)).Message);
    }

    [Fact]
    public void A_file_cannot_be_written_to_after_it_is_finished()
    {
        using MemoryStream stream = new();
        using PdfFile file = new(stream, PdfCompression.None);
        PdfReference root = Skeleton(file);
        file.Finish(root, null);

        Assert.Throws<InvalidOperationException>(() => file.Add(PdfNull.Instance));
        Assert.Throws<InvalidOperationException>(() => file.Finish(root, null));
    }

    [Fact]
    public void The_graphics_states_carry_the_opacity_for_fill_and_stroke_and_the_links_are_printable()
    {
        string text = Latin(PdfTestKit.ThreePages(PdfCompression.None));

        Assert.Matches(@"/Type /ExtGState\n/CA 0\.5\n/ca 0\.5", text);
        Assert.Matches(@"/Subtype /Link\n/F 4\n", text);
    }

    [Fact]
    public void The_link_to_a_page_points_at_the_third_page_at_the_position_it_was_given()
    {
        string text = Latin(PdfTestKit.ThreePages(PdfCompression.None));
        string pages = Regex.Match(text, @"/Type /Pages\n/Count 3\n/Kids \[(\d+) 0 R (\d+) 0 R (\d+) 0 R\]").Groups[3].Value;

        Assert.Matches($@"/Dest \[{pages} 0 R /XYZ 0 700 null\]", text);
    }

    [Fact]
    public void The_xmp_packet_is_well_formed_and_says_what_the_info_dictionary_says()
    {
        using MemoryStream stream = new();
        using (PdfDocumentWriter document = new(
            stream,
            new PdfWriterOptions
            {
                Compression = PdfCompression.None,
                Title = "A <title> & \"quotes\"",
                Keywords = "pdf, report",
                Author = "Ann",
                Language = "en-GB",
                Creator = "Pdfwright Tests",
                Created = PdfTestKit.Created,
            }))
        {
            document.BeginPage(100, 100).End();
            document.Complete();
        }

        string text = Latin(stream.ToArray());
        string packet = Encoding.UTF8.GetString(stream.ToArray());
        int start = packet.IndexOf("<x:xmpmeta", StringComparison.Ordinal);
        int end = packet.IndexOf("</x:xmpmeta>", StringComparison.Ordinal) + "</x:xmpmeta>".Length;
        XmlDocument xmp = new();
        xmp.LoadXml(packet[start..end]);

        Assert.Contains("A &lt;title&gt; &amp; \"quotes\"", xmp.InnerXml);
        Assert.Contains("<pdf:Keywords>pdf, report</pdf:Keywords>", xmp.InnerXml);
        Assert.Contains("<xmp:CreatorTool>Pdfwright Tests</xmp:CreatorTool>", xmp.InnerXml);
        Assert.Contains("<xmp:CreateDate>2026-11-21T09:30:00Z</xmp:CreateDate>", xmp.InnerXml);
        Assert.Contains("/Keywords (pdf, report)", text);
        Assert.Contains("/Creator (Pdfwright Tests)", text);
    }

    [Fact]
    public void Characters_that_xml_forbids_are_dropped_and_a_lone_surrogate_is_replaced_in_the_metadata_so_the_packet_stays_well_formed()
    {
        using MemoryStream stream = new();
        using (PdfDocumentWriter document = new(
            stream,
            new PdfWriterOptions { Compression = PdfCompression.None, Title = "a\u0001b\u0000c￾d\uD800e ok" }))
        {
            document.BeginPage(100, 100).End();
            document.Complete();
        }

        string packet = Encoding.UTF8.GetString(stream.ToArray());
        XmlDocument xmp = new();
        xmp.LoadXml(packet[packet.IndexOf("<x:xmpmeta", StringComparison.Ordinal)..(packet.IndexOf("</x:xmpmeta>", StringComparison.Ordinal) + 12)]);

        Assert.Contains("abcd" + (char)0xFFFD + "e ok", xmp.InnerXml);
        using UglyToad.PdfPig.PdfDocument read = UglyToad.PdfPig.PdfDocument.Open(stream.ToArray());
        Assert.Equal("abcd" + (char)0xFFFD + "e ok", read.Information.Title);
    }

    [Fact]
    public void A_creator_is_left_out_unless_one_is_given_and_the_producer_is_always_there()
    {
        using MemoryStream stream = new();
        using (PdfDocumentWriter document = new(stream, new PdfWriterOptions { Compression = PdfCompression.None }))
        {
            document.BeginPage(100, 100).End();
            document.Complete();
        }

        string text = Latin(stream.ToArray());

        Assert.DoesNotContain("/Creator", text);
        Assert.Contains("/Producer (Pdfwright)", text);
    }

    [Theory]
    [InlineData(2.9, 100)]
    [InlineData(100, 14401)]
    [InlineData(double.NaN, 100)]
    [InlineData(100, double.PositiveInfinity)]
    [InlineData(-1, 100)]
    public void A_page_size_outside_the_limits_of_the_format_is_refused_when_the_page_begins(double width, double height)
    {
        using MemoryStream stream = new();
        using PdfDocumentWriter document = new(stream, new PdfWriterOptions());

        Assert.Throws<ArgumentOutOfRangeException>(() => document.BeginPage(width, height));
        document.BeginPage(3, 14400).End();
    }

    [Fact]
    public void A_link_with_a_rectangle_or_a_target_that_is_not_finite_is_refused_where_it_is_added()
    {
        using MemoryStream stream = new();
        using PdfDocumentWriter document = new(stream, new PdfWriterOptions());
        PdfPageWriter page = document.BeginPage(100, 100);

        Assert.Throws<ArgumentOutOfRangeException>(() => page.AddUriLink(new PdfRectangle(0, 0, double.NaN, 1), "https://example.com/"));
        Assert.Throws<ArgumentOutOfRangeException>(() => page.AddPageLink(new PdfRectangle(0, 0, 1, 1), 0, double.PositiveInfinity, 0));
    }

    [Fact]
    public void A_bad_link_leaves_the_file_unwritten_because_every_link_is_checked_first()
    {
        using MemoryStream stream = new();
        using PdfDocumentWriter document = new(stream, new PdfWriterOptions());
        PdfPageWriter page = document.BeginPage(100, 100);
        page.AddPageLink(new PdfRectangle(0, 0, 1, 1), 0, 0, 0);
        page.AddPageLink(new PdfRectangle(0, 0, 1, 1), 7, 0, 0);
        page.End();
        long before = stream.Length;

        Assert.Throws<InvalidOperationException>(document.Complete);

        Assert.Equal(before, stream.Length);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a b")]
    [InlineData("F1 /Evil")]
    [InlineData("İ")]
    public void A_resource_name_that_is_not_plain_is_refused_in_a_content_stream(string name)
    {
        ContentStreamBuilder content = new();

        Assert.Throws<ArgumentException>(() => content.Font(name, 12));
        Assert.Throws<ArgumentException>(() => content.ExtGState(name));
        Assert.Throws<ArgumentException>(() => content.DrawXObject(name));
        Assert.Throws<ArgumentException>(() => content.BeginMarkedContent(name, 0));
    }

    [Fact]
    public void The_content_stream_writes_the_operators_with_their_operands_in_the_order_the_format_has_them()
    {
        ContentStreamBuilder content = new();
        content.SaveState().Transform(1, 0, 0, 1, 10.5, 20).Rectangle(1, 2, 3, 4).Clip().Dash([3, 2], 0)
            .BeginMarkedContent("P", 3).EndMarkedContent().BeginActualText("a(b)İ").EndMarkedContent().RestoreState();

        string text = Encoding.ASCII.GetString(content.ToMemory().Span);

        Assert.Equal(
            "q\n1 0 0 1 10.5 20 cm\n1 2 3 4 re\nW\nn\n[3 2] 0 d\n/P <</MCID 3>> BDC\nEMC\n/Span <</ActualText <FEFF00610028006200290130>>> BDC\nEMC\nQ\n",
            text);
    }
}
