using Pdfwright.Fonts;
using Pdfwright.Pdf;
using Pdfwright.Tests.Pdf;
using Pdfwright.Text;
using Pdfwright.Unicode;
using UglyToad.PdfPig;

namespace Pdfwright.Tests.Text;

/// <summary>
/// The stop test of the plan: every corpus line, mixed direction and several fonts included, is shaped by the text engine (bidi, scripts,
/// font fallback, HarfBuzz), drawn with its runs at their visual places and its glyphs written in logical order, and read back with PdfPig:
/// the text that comes out must be the text that went in, byte for byte.
/// </summary>
public class MixedDirectionExtractionTests
{
    private static readonly Dictionary<string, ShapingFont> Fonts = [];

    private static ShapingFont Font(string file)
    {
        lock (Fonts)
        {
            if (!Fonts.TryGetValue(file, out ShapingFont? font))
            {
                font = new ShapingFont(TrueTypeFont.Parse(File.ReadAllBytes(TestPaths.Font(file))));
                Fonts[file] = font;
            }

            return font;
        }
    }

    public static TheoryData<string> Cases()
    {
        TheoryData<string> data = [];
        foreach (CorpusTests.Case c in CorpusTests.Load())
        {
            data.Add(c.Id);
        }

        return data;
    }

    /// <summary>Draws one paragraph on one page: runs placed by the bidi order of their levels, glyphs emitted in logical order.</summary>
    internal static byte[] Draw(string text, string[] fontFiles, out ShapedParagraph paragraph, out double[] runOrigins)
    {
        ShapingFont[] fonts = [.. fontFiles.Select(Font)];
        paragraph = TextShaper.Shape(text, fonts);
        double size = 14.0;

        byte[] levels = [.. paragraph.Runs.Select(r => r.Level)];
        int[] visual = UnicodeBidi.VisualOrder(levels, 0, levels.Length);
        double[] originX = new double[paragraph.Runs.Count];
        runOrigins = originX;
        double x = 20;
        foreach (int index in visual)
        {
            originX[index] = x;
            x += paragraph.Runs[index].Width * size / fonts[paragraph.Runs[index].FontIndex].Font.UnitsPerEm;
        }

        using MemoryStream stream = new();
        using (PdfDocumentWriter document = new(stream, new PdfDocumentOptions { Compression = PdfCompression.None }))
        {
            PdfEmbeddedFont[] embedded = [.. fonts.Select(f => new PdfEmbeddedFont(document, f.Font))];
            PdfPageWriter page = document.BeginPage(Math.Max(595, x + 20), 842);
            string[] names = [.. embedded.Select(e => page.UseFont(e.Reference))];
            page.Content.BeginText();
            for (int r = 0; r < paragraph.Runs.Count; r++)
            {
                ShapedRun run = paragraph.Runs[r];
                double scale = size / fonts[run.FontIndex].Font.UnitsPerEm;
                double[] glyphX = new double[run.Glyphs.Length];
                double pen = originX[r];
                for (int g = 0; g < run.Glyphs.Length; g++)
                {
                    glyphX[g] = pen + (run.Glyphs[g].XOffset * scale);
                    pen += run.Glyphs[g].XAdvance * scale;
                }

                // Logical order: clusters ascending; a right-to-left run is drawn the other way round.
                int[] order = [.. Enumerable.Range(0, run.Glyphs.Length)];
                if (run.IsRightToLeft)
                {
                    Array.Reverse(order);
                }

                order = [.. order.OrderBy(g => run.Glyphs[g].Cluster)];
                int[] clusters = [.. run.Glyphs.Select(g => g.Cluster).Distinct().Order()];
                page.Content.Font(names[run.FontIndex], size);
                int previousCluster = -1;
                foreach (int g in order)
                {
                    int cluster = run.Glyphs[g].Cluster;
                    string? clusterText = null;
                    if (cluster != previousCluster)
                    {
                        int at = Array.IndexOf(clusters, cluster);
                        int end = at + 1 < clusters.Length ? clusters[at + 1] : run.Utf16Start + run.Utf16Length;
                        clusterText = text[cluster..end];
                        previousCluster = cluster;
                    }

                    int code = embedded[run.FontIndex].UseGlyph(run.Glyphs[g].Glyph, clusterText);
                    page.Content.TextMatrix(1, 0, 0, 1, glyphX[g], 780 + (run.Glyphs[g].YOffset * scale)).ShowHex(PdfEmbeddedFont.Code(code));
                }
            }

            page.Content.EndText();
            page.End();
            document.Complete();
        }

        return stream.ToArray();
    }

    /// <summary>
    /// The runs stand left to right in the order the bidi algorithm gives the characters in them, worked out a second way: per character
    /// (the end-of-line rule, then the visual order of the code points), not per run as <see cref="Draw"/> does.
    /// </summary>
    private static void AssertRunsAreInBidiOrder(string text, ShapedParagraph paragraph, double[] origins)
    {
        int[] codePoints = UnicodeText.CodePoints(text, out int[] utf16Index);
        BidiResult bidi = UnicodeBidi.Resolve(codePoints);
        byte[] levels = UnicodeBidi.LineLevels(bidi, 0, codePoints.Length);
        int[] visual = UnicodeBidi.VisualOrder(levels, 0, levels.Length);
        int[] rank = new int[codePoints.Length];
        for (int position = 0; position < visual.Length; position++)
        {
            rank[visual[position]] = position;
        }

        int[] runRank = [.. paragraph.Runs.Select(run => Enumerable.Range(0, codePoints.Length)
            .Where(i => utf16Index[i] >= run.Utf16Start && utf16Index[i] < run.Utf16Start + run.Utf16Length)
            .Min(i => rank[i]))];

        int[] byPosition = [.. Enumerable.Range(0, origins.Length).OrderBy(r => origins[r])];
        int[] byRank = [.. Enumerable.Range(0, origins.Length).OrderBy(r => runRank[r])];
        Assert.Equal(byRank, byPosition);
        for (int r = 1; r < byPosition.Length; r++)
        {
            Assert.True(origins[byPosition[r]] > origins[byPosition[r - 1]], "two runs start at the same place");
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void A_corpus_line_reads_back_exactly_as_it_was_written(string id)
    {
        CorpusTests.Case c = CorpusTests.Load().Single(x => x.Id == id);

        byte[] pdf = Draw(c.Text, c.Fonts, out ShapedParagraph paragraph, out double[] origins);

        // Set PDFWRIGHT_DUMP to a folder to keep the files, to read them with other readers (pdftotext, pdf.js, a viewer).
        if (Environment.GetEnvironmentVariable("PDFWRIGHT_DUMP") is { Length: > 0 } dump)
        {
            Directory.CreateDirectory(dump);
            File.WriteAllBytes(Path.Combine(dump, id + ".pdf"), pdf);
        }

        using PdfDocument document = PdfDocument.Open(pdf);
        Assert.Empty(paragraph.Missing);
        Assert.DoesNotContain(paragraph.Runs.SelectMany(r => r.Glyphs), g => g.Glyph == 0);
        AssertRunsAreInBidiOrder(c.Text, paragraph, origins);
        Assert.Equal(c.Text, document.GetPage(1).Text);
        PdfTestKit.AssertQpdfAccepts(pdf, "mixed-" + id);
    }
}
