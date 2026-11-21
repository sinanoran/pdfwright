using System.Globalization;
using System.Text;
using Pdfwright.Pdf;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Annotations;

namespace Pdfwright.Tests.Pdf;

/// <summary>
/// A document the writer makes is a file other software opens: PdfPig reads it back (pages, sizes, metadata, text,
/// links), qpdf checks its structure, and it is byte-stable.
/// </summary>
public class PdfDocumentTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_reader_finds_the_pages_their_sizes_and_the_metadata(bool flate)
    {
        PdfCompression compression = flate ? PdfCompression.Flate : PdfCompression.None;
        using PdfDocument document = PdfDocument.Open(PdfTestKit.ThreePages(compression));

        Assert.Equal(3, document.NumberOfPages);
        Assert.Equal((595d, 842d), (document.GetPage(1).Width, document.GetPage(1).Height));
        Assert.Equal((842d, 595d), (document.GetPage(2).Width, document.GetPage(2).Height));
        Assert.Equal((300d, 300d), (document.GetPage(3).Width, document.GetPage(3).Height));
        Assert.Equal("Rapor İstanbul 2026", document.Information.Title);
        Assert.Equal("Writer test", document.Information.Subject);
        Assert.Equal("Pdfwright tests", document.Information.Author);
        Assert.Equal("Pdfwright", document.Information.Producer);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_text_of_every_page_reads_back(bool flate)
    {
        PdfCompression compression = flate ? PdfCompression.Flate : PdfCompression.None;
        using PdfDocument document = PdfDocument.Open(PdfTestKit.ThreePages(compression));

        Assert.Contains("Hello PDF, first page", document.GetPage(1).Text);
        Assert.Contains("Second page, landscape (with brackets and a backslash \\)", document.GetPage(2).Text);
        Assert.Contains("Line 0 of a repetitive page", document.GetPage(3).Text);
        Assert.Contains("Line 39 of a repetitive page", document.GetPage(3).Text);
    }

    [Fact]
    public void A_link_to_an_address_reads_back_with_its_address_and_the_link_to_a_page_is_there()
    {
        using PdfDocument document = PdfDocument.Open(PdfTestKit.ThreePages(PdfCompression.None));

        List<Annotation> links = document.GetPage(1).GetAnnotations().Where(a => a.Type == AnnotationType.Link).ToList();

        Assert.Equal(2, links.Count);
        Assert.Contains(links, a => a.Action is UglyToad.PdfPig.Actions.UriAction { Uri: "https://example.com/report?a=1&b=2" });
    }

    [Fact]
    public void Compression_makes_a_repetitive_page_smaller_and_leaves_the_text_alone()
    {
        byte[] plain = PdfTestKit.ThreePages(PdfCompression.None);
        byte[] compressed = PdfTestKit.ThreePages(PdfCompression.Flate);

        Assert.True(compressed.Length < plain.Length, $"{compressed.Length} bytes compressed, {plain.Length} plain");
        Assert.Contains("/FlateDecode", Encoding.Latin1.GetString(compressed));
        Assert.DoesNotContain("/FlateDecode", Encoding.Latin1.GetString(plain));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_same_input_is_the_same_file_twice(bool flate)
    {
        PdfCompression compression = flate ? PdfCompression.Flate : PdfCompression.None;
        Assert.Equal(PdfTestKit.ThreePages(compression), PdfTestKit.ThreePages(compression));
    }

    [Fact]
    public void The_file_is_the_same_under_a_culture_that_writes_a_comma_for_a_decimal_point()
    {
        byte[] invariant = PdfTestKit.ThreePages(PdfCompression.None);
        CultureInfo before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");

            Assert.Equal(invariant, PdfTestKit.ThreePages(PdfCompression.None));
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    [Fact]
    public void The_output_need_not_be_seekable_and_the_bytes_are_the_same()
    {
        byte[] expected = PdfTestKit.ThreePages(PdfCompression.None);
        using MemoryStream sink = new();

        PdfTestKit.ThreePages(PdfCompression.None, new WriteOnlyStream(sink));

        Assert.Equal(expected, sink.ToArray());
    }

    [Fact]
    public void A_document_without_a_creation_date_has_no_date_at_all()
    {
        using MemoryStream stream = new();
        using (PdfDocumentWriter document = new(stream, new PdfDocumentOptions { Compression = PdfCompression.None }))
        {
            document.BeginPage(100, 100).End();
            document.Complete();
        }

        string text = Encoding.Latin1.GetString(stream.ToArray());

        Assert.DoesNotContain("CreationDate", text);
        Assert.DoesNotContain("CreateDate", text);
    }

    [Fact]
    public void The_file_ends_with_the_cross_reference_the_trailer_points_at()
    {
        byte[] pdf = PdfTestKit.ThreePages(PdfCompression.None);
        string text = Encoding.Latin1.GetString(pdf);

        Assert.StartsWith("%PDF-1.7\n%", text);
        Assert.EndsWith("%%EOF\n", text);
        int startxref = text.LastIndexOf("startxref\n", StringComparison.Ordinal);
        long offset = long.Parse(text[(startxref + 10)..text.IndexOf('\n', startxref + 10)], CultureInfo.InvariantCulture);
        Assert.Matches(@"^\d+ 0 obj\n<<\n/Type /XRef", text[(int)offset..]);
    }

    [Fact]
    public void An_object_that_was_allocated_and_never_written_is_reported_when_the_file_is_finished()
    {
        using MemoryStream stream = new();
        using PdfDocumentWriter document = new(stream, new PdfDocumentOptions());
        document.AllocateObject();
        document.BeginPage(100, 100).End();

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(document.Complete);

        Assert.Contains("allocated and never written", error.Message);
    }

    [Fact]
    public void A_page_that_is_not_ended_a_second_page_started_early_and_an_empty_document_are_refused()
    {
        using MemoryStream stream = new();
        using PdfDocumentWriter document = new(stream, new PdfDocumentOptions());

        Assert.Throws<InvalidOperationException>(document.Complete);
        PdfPageWriter page = document.BeginPage(100, 100);
        Assert.Throws<InvalidOperationException>(() => document.BeginPage(100, 100));
        Assert.Throws<InvalidOperationException>(document.Complete);
        page.End();
        Assert.Throws<ArgumentOutOfRangeException>(() => document.BeginPage(0, 100));
    }

    [Fact]
    public void A_link_to_a_page_that_does_not_exist_is_reported_when_the_document_is_complete()
    {
        using MemoryStream stream = new();
        using PdfDocumentWriter document = new(stream, new PdfDocumentOptions());
        PdfPageWriter page = document.BeginPage(100, 100);
        page.AddPageLink(new PdfRectangle(0, 0, 10, 10), targetPage: 5, x: 0, y: 0);
        page.End();

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(document.Complete);

        Assert.Contains("points at page 6", error.Message);
    }

    [Fact]
    public void An_opacity_outside_zero_to_one_and_a_non_ascii_address_are_refused()
    {
        using MemoryStream stream = new();
        using PdfDocumentWriter document = new(stream, new PdfDocumentOptions());
        PdfPageWriter page = document.BeginPage(100, 100);

        Assert.Throws<ArgumentOutOfRangeException>(() => page.UseOpacity(1.5));
        Assert.Throws<ArgumentException>(() => page.AddUriLink(new PdfRectangle(0, 0, 1, 1), "https://example.com/İ"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Qpdf_accepts_the_file(bool flate)
    {
        PdfCompression compression = flate ? PdfCompression.Flate : PdfCompression.None;
        PdfTestKit.AssertQpdfAccepts(PdfTestKit.ThreePages(compression), $"three-pages-{compression}");
    }

    [Fact]
    public void The_uncompressed_file_is_the_golden_file()
    {
        // Uncompressed output depends on nothing but the input, on every runtime: its bytes are compared with the file
        // in Golden/. To change it on purpose, run with PDFWRIGHT_UPDATE_GOLDENS=1 and commit the new file by itself.
        byte[] actual = PdfTestKit.ThreePages(PdfCompression.None);

        Golden.Check("three-pages.pdf", actual);
    }

    /// <summary>A stream that only writes: nothing may seek or read it, as a response body or a pipe cannot.</summary>
    private sealed class WriteOnlyStream(Stream inner) : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

        public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

/// <summary>Golden files: byte-for-byte comparison with a committed file, regenerated only on purpose.</summary>
internal static class Golden
{
    internal static void Check(string name, byte[] actual)
    {
        string source = Path.Combine(RepositoryRoot(), "tests", "Pdfwright.Tests", "Golden", name);
        if (Environment.GetEnvironmentVariable("PDFWRIGHT_UPDATE_GOLDENS") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            File.WriteAllBytes(source, actual);
            return;
        }

        Assert.True(File.Exists(source), $"{name} is not in Golden/; create it with PDFWRIGHT_UPDATE_GOLDENS=1");
        byte[] expected = File.ReadAllBytes(source);
        Assert.True(
            expected.AsSpan().SequenceEqual(actual),
            $"{name} differs from the golden file ({expected.Length} bytes expected, {actual.Length} written). "
            + "If the change is intended, run with PDFWRIGHT_UPDATE_GOLDENS=1 and commit the file by itself.");
    }

    private static string RepositoryRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "Pdfwright.slnx")))
        {
            directory = Path.GetDirectoryName(directory);
        }

        return directory ?? throw new InvalidOperationException("Pdfwright.slnx was not found above the test assembly");
    }
}
