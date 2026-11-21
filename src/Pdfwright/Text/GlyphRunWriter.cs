using Pdfwright.Pdf;

namespace Pdfwright.Text;

/// <summary>
/// Writes a shaped run into a content stream so that readers get the right text whichever way they read it: the glyphs are written in
/// <b>logical</b> order (a right-to-left run backwards from how the shaper lists them, so that a reader that follows the content stream
/// gets the text in the order it was typed) and each at the place the shaper put it, with a text matrix of its own (so that a reader that
/// works from positions sees the glyphs where they are drawn).
/// </summary>
/// <remarks>
/// Measured on the 61 corpus lines (see the plan's section on the mixed-direction test): a <c>Tm</c> per glyph reads better in xpdf's
/// position-ordered mode than one <c>Tm</c> per run with <c>TJ</c> adjustments (36 of 61 against 29) and the same in pdf.js and PdfPig, and
/// writing the glyphs of a cluster with those that have no advance first (to put the glyph that carries the advance last) made xpdf
/// worse (25 of 61). The file is larger by about twenty bytes a glyph (not measured after compression). Measured before the continuation codes were
/// mapped to U+034F (see <see cref="PdfEmbeddedFont.ContinuationText"/>); under that mapping xpdf reads 44 of 61 once the joiner is removed.
/// </remarks>
internal static class GlyphRunWriter
{
    /// <summary>
    /// Writes <paramref name="run"/> with its origin (its left edge) at <paramref name="originX"/> on the baseline <paramref name="baselineY"/>,
    /// at <paramref name="size"/>, in the font whose resource name is <paramref name="fontName"/>. The text state must be inside <c>BT</c> / <c>ET</c>.
    /// </summary>
    internal static void Write(ContentStreamBuilder content, PdfEmbeddedFont font, string fontName, ShapedRun run, string text, double originX, double baselineY, double size)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(fontName);
        ArgumentNullException.ThrowIfNull(text);
        ShapedGlyph[] glyphs = run.Glyphs;
        if (glyphs.Length == 0)
        {
            return;
        }

        double scale = size / font.Font.UnitsPerEm;
        double[] x = new double[glyphs.Length];
        double pen = originX;
        for (int i = 0; i < glyphs.Length; i++)
        {
            x[i] = pen + (glyphs[i].XOffset * scale);
            pen += glyphs[i].XAdvance * scale;
        }

        // Logical order: clusters ascending (a right-to-left run lists them downwards), the glyphs of one cluster in the order the shaper lists them, which for a right-to-left run puts the base first (the glyph that carries the text).
        int[] order = [.. Enumerable.Range(0, glyphs.Length)];
        if (run.IsRightToLeft)
        {
            Array.Reverse(order);
        }

        order = [.. order.OrderBy(g => glyphs[g].Cluster)];

        // The text of each cluster: from its value to the next greater one in the run, or the end of the run.
        int[] values = [.. glyphs.Select(g => g.Cluster).Distinct().Order()];
        Dictionary<int, int> indexOfValue = [];
        for (int v = 0; v < values.Length; v++)
        {
            indexOfValue[values[v]] = v;
        }

        int runEnd = run.Utf16Start + run.Utf16Length;
        content.Font(fontName, size);
        int previousCluster = -1;
        foreach (int g in order)
        {
            int cluster = glyphs[g].Cluster;
            string? clusterText = null;
            if (cluster != previousCluster)
            {
                int next = indexOfValue[cluster] + 1;
                clusterText = text[cluster..(next < values.Length ? values[next] : runEnd)];
                previousCluster = cluster;
            }

            int code = font.UseGlyph(glyphs[g].Glyph, clusterText);
            content.TextMatrix(1, 0, 0, 1, x[g], baselineY + (glyphs[g].YOffset * scale)).ShowHex(PdfEmbeddedFont.Code(code));
        }
    }
}
