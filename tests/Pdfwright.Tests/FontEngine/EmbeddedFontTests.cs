using Pdfwright.Tests.Pdf;
using System.Text;
using Pdfwright.Fonts;
using Pdfwright.Pdf;
using UglyToad.PdfPig;

namespace Pdfwright.Tests.FontEngine;

/// <summary>
/// An embedded subset font in a real document: the glyphs are drawn, the file is valid, the font is a subset, and the text
/// reads back from the PDF. This is the first half of the plan's acceptance test: the corpus lines that need no shaping
/// (Latin, Greek, Cyrillic, and Thai and the Indic scripts as plain characters) come back from the file exactly. The
/// right-to-left and mixed-direction lines wait for the text engine (slice S3), which decides their order.
/// </summary>
public class EmbeddedFontTests
{
    private static readonly string[] NeedsShapingOrBidi = ["arabic", "hebrew", "mixed", "long-unbreakable-ar", "long-unbreakable-he"];

    private static readonly Dictionary<string, TrueTypeFont> Fonts = [];

    private static TrueTypeFont Font(string file)
    {
        lock (Fonts)
        {
            if (!Fonts.TryGetValue(file, out TrueTypeFont? font))
            {
                font = TrueTypeFont.Parse(File.ReadAllBytes(TestPaths.Font(file)));
                Fonts[file] = font;
            }

            return font;
        }
    }

    public static TheoryData<string> PlainCases()
    {
        TheoryData<string> data = [];
        foreach (CorpusTests.Case c in CorpusTests.Load())
        {
            if (c.Fonts.Length == 1 && !NeedsShapingOrBidi.Any(p => c.Id.StartsWith(p, StringComparison.Ordinal)))
            {
                data.Add(c.Id);
            }
        }

        return data;
    }

    /// <summary>One line per page, each character as the font's glyph for it, drawn in a row from the left: no shaping, no kerning.</summary>
    private static byte[] Draw(string fontFile, params string[] lines)
    {
        using MemoryStream stream = new();
        using (PdfDocumentWriter document = new(stream, new PdfWriterOptions { Title = "Embedded font", Language = "en", Created = PdfTestKit.Created }))
        {
            PdfEmbeddedFont font = new(document, Font(fontFile));
            foreach (string line in lines)
            {
                PdfPageWriter page = document.BeginPage(595, 842);
                string name = page.UseFont(font.Reference);
                byte[] codes = Codes(font, line);
                page.Content.BeginText().Font(name, 14).TextMove(40, 780).ShowHex(codes).EndText();
                page.End();
            }

            document.Complete();
        }

        return stream.ToArray();
    }

    private static byte[] Codes(PdfEmbeddedFont font, string line)
    {
        List<byte> codes = [];
        foreach (Rune rune in line.EnumerateRunes())
        {
            int glyph = font.Font.GlyphForCodePoint(rune.Value);
            if (glyph == 0)
            {
                continue;
            }

            codes.AddRange(PdfEmbeddedFont.Code(font.UseGlyph(glyph, rune.ToString())));
        }

        return [.. codes];
    }

    private static string Squeeze(string text) => new(text.Where(c => !char.IsWhiteSpace(c) && c is not ('‌' or '‍')).ToArray());

    [Theory]
    [MemberData(nameof(PlainCases))]
    public void A_corpus_line_that_needs_no_shaping_reads_back_from_the_file_as_it_was_written(string id)
    {
        CorpusTests.Case c = CorpusTests.Load().Single(x => x.Id == id);

        using UglyToad.PdfPig.PdfDocument document = UglyToad.PdfPig.PdfDocument.Open(Draw(c.Fonts[0], c.Text));

        Assert.Equal(Squeeze(c.Text), Squeeze(document.GetPage(1).Text));
    }

    [Fact]
    public void The_font_is_a_subset_with_a_tag_and_the_file_is_small_and_valid()
    {
        string line = "Quarterly report: İstanbul, Größe, Ω Ж";
        byte[] pdf = Draw("NotoSans-Regular.ttf", line);

        using UglyToad.PdfPig.PdfDocument document = UglyToad.PdfPig.PdfDocument.Open(pdf);
        string? fontName = document.GetPage(1).Letters.Select(l => l.FontName).Distinct().Single();

        Assert.Matches("^[A-Z]{6}\\+NotoSans-Regular$", fontName);
        Assert.True(pdf.Length < 40_000, $"{pdf.Length} bytes for one short line: the font was not subset");
        PdfTestKit.AssertQpdfAccepts(pdf, "embedded-font");
    }

    [Fact]
    public void The_same_document_is_the_same_bytes_and_two_subsets_of_one_font_have_different_tags()
    {
        byte[] first = Draw("NotoSans-Regular.ttf", "Hello");
        byte[] second = Draw("NotoSans-Regular.ttf", "Hello");
        byte[] other = Draw("NotoSans-Regular.ttf", "Hello, world");

        Assert.Equal(first, second);
        string Tag(byte[] pdf)
        {
            using UglyToad.PdfPig.PdfDocument d = UglyToad.PdfPig.PdfDocument.Open(pdf);
            return d.GetPage(1).Letters[0].FontName![..6];
        }

        Assert.NotEqual(Tag(first), Tag(other));
    }

    [Fact]
    public void A_glyph_that_stands_for_several_characters_reads_back_as_all_of_them()
    {
        // A ligature or a stacked mark is one glyph for a cluster of characters; ToUnicode gives the whole cluster back.
        using MemoryStream stream = new();
        using (PdfDocumentWriter document = new(stream, new PdfWriterOptions()))
        {
            PdfEmbeddedFont font = new(document, Font("NotoSans-Regular.ttf"));
            PdfPageWriter page = document.BeginPage(300, 300);
            int f = font.Font.GlyphForCodePoint('f');
            int code = font.UseGlyph(f, "ffi");
            page.Content.BeginText().Font(page.UseFont(font.Reference), 20).TextMove(20, 200).ShowHex(PdfEmbeddedFont.Code(code)).EndText();
            page.End();
            document.Complete();
        }

        using UglyToad.PdfPig.PdfDocument pdf = UglyToad.PdfPig.PdfDocument.Open(stream.ToArray());

        Assert.Equal("ffi", pdf.GetPage(1).Text);
    }

    [Fact]
    public void An_actual_text_span_carries_the_logical_text_around_glyphs_drawn_in_visual_order()
    {
        // The route a right-to-left run will take: drawn in visual order, with its text in logical order beside it.
        // PdfPig does not apply ActualText (it reads the glyphs), so the content stream is read directly.
        using MemoryStream stream = new();
        using (PdfDocumentWriter document = new(stream, new PdfWriterOptions { Compression = PdfCompression.None }))
        {
            PdfEmbeddedFont font = new(document, Font("NotoSans-Regular.ttf"));
            PdfPageWriter page = document.BeginPage(300, 300);
            byte[] visual = Codes(font, "cba");
            page.Content.BeginText().Font(page.UseFont(font.Reference), 20).TextMove(20, 200)
                .BeginActualText("abc").ShowHex(visual).EndMarkedContent().EndText();
            page.End();
            document.Complete();
        }

        string text = System.Text.Encoding.Latin1.GetString(stream.ToArray());

        Assert.Contains("/Span <</ActualText <FEFF006100620063>>> BDC", text);
        Assert.Contains("> Tj\nEMC\nET", text);
        PdfTestKit.AssertQpdfAccepts(stream.ToArray(), "actual-text");
    }

    [Fact]
    public void A_font_whose_licence_forbids_embedding_is_refused()
    {
        byte[] restricted = File.ReadAllBytes(TestPaths.Font("NotoSans-Regular.ttf"));
        TrueTypeFont original = TrueTypeFont.Parse(restricted);
        // Set fsType to 2 (restricted licence) in a copy of the font.
        int os2 = FindTable(restricted, "OS/2");
        restricted[os2 + 8] = 0;
        restricted[os2 + 9] = 2;

        TrueTypeFont font = TrueTypeFont.Parse(restricted);
        using MemoryStream stream = new();
        using PdfDocumentWriter document = new(stream, new PdfWriterOptions());

        Assert.True(original.MayEmbedSubset);
        Assert.False(font.MayEmbedSubset);
        Assert.Contains("forbids it", Assert.Throws<InvalidOperationException>(() => new PdfEmbeddedFont(document, font)).Message);
    }

    [Fact]
    public void A_glyph_outside_the_font_is_refused()
    {
        using MemoryStream stream = new();
        using PdfDocumentWriter document = new(stream, new PdfWriterOptions());
        PdfEmbeddedFont font = new(document, Font("NotoSans-Regular.ttf"));

        Assert.Throws<ArgumentOutOfRangeException>(() => font.UseGlyph(font.Font.GlyphCount, "x"));
    }

    private static int FindTable(byte[] font, string tag)
    {
        int count = (font[4] << 8) | font[5];
        for (int i = 0; i < count; i++)
        {
            int record = 12 + (i * 16);
            if (Encoding.ASCII.GetString(font, record, 4) == tag)
            {
                return (font[record + 8] << 24) | (font[record + 9] << 16) | (font[record + 10] << 8) | font[record + 11];
            }
        }

        throw new InvalidOperationException(tag);
    }
}
