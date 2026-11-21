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

    /// <summary>The left edge, in points, of what <see cref="Draw"/> and <see cref="DrawStack"/> draw.</summary>
    internal const double Left = 20;

    /// <summary>The distance from the top of the page to the top of the first paragraph.</summary>
    internal const double TopMargin = 50;

    /// <summary>The page height <see cref="DrawStack"/> uses: A4's, or taller when the paragraphs need it.</summary>
    internal static double PageHeight(params ParagraphLayout[] layouts) => Math.Max(842, layouts.Sum(l => l.Height) + 100);

    /// <summary>Draws a laid out paragraph on one page: its top left corner is <see cref="Left"/> from the page's left edge and <see cref="TopMargin"/> from its top.</summary>
    internal static byte[] Draw(ParagraphLayout layout, ShapingFont[] fonts) => DrawStack([layout], fonts);

    /// <summary>Draws paragraphs one under the other on one page, the first at the top margin, each starting where the one before it ends.</summary>
    internal static byte[] DrawStack(ParagraphLayout[] layouts, ShapingFont[] fonts)
    {
        double pageWidth = Math.Max(595, layouts.Max(l => l.Width) + 40);
        double pageHeight = PageHeight(layouts);
        using MemoryStream stream = new();
        using (PdfDocumentWriter document = new(stream, new PdfDocumentOptions { Compression = PdfCompression.None }))
        {
            PdfEmbeddedFont[] embedded = [.. fonts.Select(f => new PdfEmbeddedFont(document, f.Font))];
            PdfPageWriter page = document.BeginPage(pageWidth, pageHeight);
            string[] names = [.. embedded.Select(e => page.UseFont(e.Reference))];
            double top = pageHeight - TopMargin;
            foreach (ParagraphLayout layout in layouts)
            {
                layout.Draw(page.Content, embedded, names, Left, top);
                top -= layout.Height;
            }

            page.End();
            document.Complete();
        }

        return stream.ToArray();
    }
}
