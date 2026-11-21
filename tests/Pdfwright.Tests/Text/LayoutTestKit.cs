using Pdfwright.Fonts;
using Pdfwright.Pdf;
using Pdfwright.Text;

namespace Pdfwright.Tests.Text;

/// <summary>Fonts and drawing for the paragraph layout tests.</summary>
internal static class LayoutTestKit
{
    internal const string Latin = "NotoSans-Regular.ttf";
    internal const string Arabic = "NotoSansArabic-Regular.ttf";
    internal const string Hebrew = "NotoSansHebrew-Regular.ttf";

    private static readonly Dictionary<string, ShapingFont> Cache = [];

    internal static ShapingFont Font(string file)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue(file, out ShapingFont? font))
            {
                font = new ShapingFont(TrueTypeFont.Parse(File.ReadAllBytes(TestPaths.Font(file))));
                Cache[file] = font;
            }

            return font;
        }
    }

    internal static ShapingFont[] Fonts(params string[] files) => [.. files.Select(Font)];

    internal static CorpusTests.Case Case(string id) => CorpusTests.Load().Single(c => c.Id == id);

    internal static ParagraphLayout Layout(string text, ShapingFont[] fonts, double? maxWidth, TextAlignment alignment = TextAlignment.Start, double size = 14, int paragraphLevel = Pdfwright.Unicode.UnicodeBidi.AutoLevel, double lineSpacing = 1.0) =>
        ParagraphLayouter.Layout(text, fonts, new ParagraphOptions { Size = size, MaxWidth = maxWidth, Alignment = alignment, ParagraphLevel = paragraphLevel, LineSpacing = lineSpacing });

    /// <summary>Draws a laid out paragraph on one page, tall enough for all its lines, with its top left corner 20 points from the page's left edge and 50 from its top.</summary>
    internal static byte[] Draw(ParagraphLayout layout, string text, ShapingFont[] fonts)
    {
        double pageWidth = Math.Max(595, layout.Width + 40);
        double pageHeight = Math.Max(842, layout.Height + 100);
        using MemoryStream stream = new();
        using (PdfDocumentWriter document = new(stream, new PdfDocumentOptions { Compression = PdfCompression.None }))
        {
            PdfEmbeddedFont[] embedded = [.. fonts.Select(f => new PdfEmbeddedFont(document, f.Font))];
            PdfPageWriter page = document.BeginPage(pageWidth, pageHeight);
            string[] names = [.. embedded.Select(e => page.UseFont(e.Reference))];
            layout.Draw(page.Content, embedded, names, text, 20, pageHeight - 50);
            page.End();
            document.Complete();
        }

        return stream.ToArray();
    }
}
