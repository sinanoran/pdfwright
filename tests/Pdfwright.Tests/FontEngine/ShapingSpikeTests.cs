using System.Text;
using HarfBuzzSharp;
using Pdfwright.Fonts;
using Pdfwright.Pdf;
using Pdfwright.Tests.Pdf;
using UglyToad.PdfPig;

namespace Pdfwright.Tests.FontEngine;

/// <summary>
/// The go or stop test of the plan (section 9): lines of Arabic, Hebrew, Devanagari, Bengali, Tamil and Thai are shaped by
/// HarfBuzz, drawn with the font engine of slice S2, and read back with PdfPig. This is a measurement made before the text
/// engine exists, so it is deliberately small: one run, one direction per line, one font, glyphs placed one by one in
/// logical order at the positions the shaper gave (so that an extractor which reads the content stream in order gets logical
/// order, which is what the Skia spike failed to produce). Mixed direction and bidi are slice S3.
/// </summary>
public class ShapingSpikeTests
{
    private static readonly string[] Scripts = ["arabic", "hebrew", "devanagari", "bengali", "tamil", "thai"];

    public static TheoryData<string> Cases()
    {
        TheoryData<string> data = [];
        foreach (CorpusTests.Case c in CorpusTests.Load())
        {
            if (c.Fonts.Length == 1 && Scripts.Any(s => c.Id.StartsWith(s, StringComparison.Ordinal)) && !c.Id.Contains("mixed", StringComparison.Ordinal))
            {
                data.Add(c.Id);
            }
        }

        return data;
    }

    private static string Squeeze(string text) => new(text.Normalize(NormalizationForm.FormC).Where(c => !char.IsWhiteSpace(c) && c is not ('‌' or '‍')).ToArray());

    /// <summary>Shapes and draws one line; returns the file and how many clusters needed ActualText because their glyph already stood for other text.</summary>
    private static byte[] Draw(CorpusTests.Case c)
    {
        byte[] fontBytes = File.ReadAllBytes(TestPaths.Font(c.Fonts[0]));
        TrueTypeFont parsed = TrueTypeFont.Parse(fontBytes);
        using Blob blob = TestPaths.BlobOf(fontBytes);
        using Face face = new(blob, 0);
        using HarfBuzzSharp.Font shaper = new(face);
        shaper.SetScale(parsed.UnitsPerEm, parsed.UnitsPerEm);
        using HarfBuzzSharp.Buffer buffer = new();
        buffer.AddUtf16(c.Text);
        buffer.GuessSegmentProperties();
        shaper.Shape(buffer);

        GlyphInfo[] infos = buffer.GlyphInfos;
        GlyphPosition[] positions = buffer.GlyphPositions;
        double size = 14.0;
        double scale = size / parsed.UnitsPerEm;

        // Visual pen positions of every glyph.
        double[] x = new double[infos.Length];
        double pen = 0;
        for (int i = 0; i < infos.Length; i++)
        {
            x[i] = pen + positions[i].XOffset;
            pen += positions[i].XAdvance;
        }

        // The text of each cluster: from its index to the next index in the logical string.
        int[] starts = [.. infos.Select(i => (int)i.Cluster).Distinct().Order()];
        string TextOf(int cluster)
        {
            int at = Array.IndexOf(starts, cluster);
            int end = at + 1 < starts.Length ? starts[at + 1] : c.Text.Length;
            return c.Text[cluster..end];
        }

        using MemoryStream stream = new();
        using (PdfDocumentWriter document = new(stream, new PdfDocumentOptions { Compression = PdfCompression.None }))
        {
            PdfEmbeddedFont font = new(document, parsed);
            PdfPageWriter page = document.BeginPage(595, 842);
            string name = page.UseFont(font.Reference);
            page.Content.BeginText().Font(name, size);
            foreach (int cluster in starts)
            {
                // Logical order: clusters ascending; within a cluster, the glyphs in the order the shaper drew them.
                int[] glyphs = [.. Enumerable.Range(0, infos.Length).Where(i => (int)infos[i].Cluster == cluster).OrderBy(i => x[i]).ThenBy(i => i)];
                string text = TextOf(cluster);
                bool first = true;
                foreach (int i in glyphs)
                {
                    int code = font.UseGlyph((int)infos[i].Codepoint, first ? text : null);
                    first = false;
                    double px = 40 + (x[i] * scale);
                    double py = 780 + (positions[i].YOffset * scale);
                    page.Content.TextMatrix(1, 0, 0, 1, px, py).ShowHex(PdfEmbeddedFont.Code(code));
                }
            }

            page.Content.EndText();
            page.End();
            document.Complete();
        }

        return stream.ToArray();
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void A_shaped_line_reads_back_in_logical_order(string id)
    {
        CorpusTests.Case c = CorpusTests.Load().Single(x => x.Id == id);

        byte[] pdf = Draw(c);

        using PdfDocument document = PdfDocument.Open(pdf);
        string extracted = document.GetPage(1).Text;
        Assert.Equal(Squeeze(c.Text), Squeeze(extracted));
        PdfTestKit.AssertQpdfAccepts(pdf, "shaping-" + id);
    }
}
