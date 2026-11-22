using System.Buffers;
using System.Globalization;
using System.Text;
using Pdfwright.Pdf;

namespace Pdfwright.Text;

/// <summary>
/// Writes a shaped run into a content stream so that readers get the right text whichever way they read it: the glyphs are written in
/// <b>logical</b> order (a right-to-left run backwards from how the shaper lists them, so that a reader that follows the content stream
/// gets the text in the order it was typed) and each at the place the shaper put it, with a text matrix of its own (so that a reader that
/// works from positions sees the glyphs where they are drawn).
/// </summary>
/// <remarks>
/// <para>
/// Measured on the 61 corpus lines (see the plan's section on the mixed-direction test): a <c>Tm</c> per glyph reads better in xpdf's
/// position-ordered mode than one <c>Tm</c> per run with <c>TJ</c> adjustments (36 of 61 against 29) and the same in pdf.js and PdfPig, and
/// writing the glyphs of a cluster with those that have no advance first (to put the glyph that carries the advance last) made xpdf
/// worse (25 of 61). The file is larger by about twenty bytes a glyph (not measured after compression). Measured before the continuation codes were
/// mapped to U+034F (see <see cref="PdfEmbeddedFont.ContinuationText"/>); under that mapping xpdf reads 44 of 61 once the joiner is removed.
/// </para>
/// <para>
/// Firefox's pdf.js needs three more things of the file, measured on the same lines (61 of 61 with them, 56 without) and read in its source (6.3.335): it treats a glyph whose
/// text holds a non-spacing mark anywhere as a zero-width diacritic, which skips its position check and adds it to the open text item; it then reverses a right-to-left item
/// character by character, expecting visual order; and it drops a glyph whose text ends with a format character. So (1) a right-to-left run is shaped at HarfBuzz cluster level 1
/// (<c>MonotoneCharacters</c>), which gives each mark a cluster and a text of its own (left-to-right runs stay at the default level, where level 1 cost xpdf five Indic lines);
/// (2) a glyph whose text holds a mark, ends with a format character, or is a continuation glyph (<see cref="PdfEmbeddedFont.ContinuationText"/>) is shown with <c>Tf</c> at size 1 and a
/// text matrix scaled by the real size (the same drawing), two in a row alternating between sizes 1 and 2, so that each starts an item of its own, and the next ordinary glyph restores
/// the real size (<c>Tf</c> is written only when the size changes); (3) a cluster text that ends with a format character (a zero width non-joiner) gets <see cref="PdfEmbeddedFont.ContinuationText"/>
/// appended so that pdf.js keeps the glyph. Measured with all three: PdfPig 61 of 61 (byte-exact, joiner removed), pdf.js 61 of 61 (canonical equality; see the known limitations), xpdf 45 of 61.
/// </para>
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
        int count = glyphs.Length;
        double[] x = ArrayPool<double>.Shared.Rent(count);
        long[] keys = ArrayPool<long>.Shared.Rent(count);
        try
        {
            double pen = originX;
            for (int i = 0; i < count; i++)
            {
                x[i] = pen + (glyphs[i].XOffset * scale);
                pen += glyphs[i].XAdvance * scale;
            }

            // Logical order: clusters ascending (a right-to-left run lists them downwards), the glyphs of one cluster in the order the shaper lists them, which for a right-to-left run puts the base first (the glyph that carries the text).
            // A stable sort by cluster of the run's glyphs, taken backwards when it is right to left: the key is the cluster over the position in that order, so a plain sort of the keys is stable.
            bool rightToLeft = run.IsRightToLeft;
            for (int g = 0; g < count; g++)
            {
                int position = rightToLeft ? count - 1 - g : g;
                keys[g] = ((long)glyphs[g].Cluster << 32) | (uint)position;
            }

            Array.Sort(keys, 0, count);

            // The text of each cluster: from its value to the next greater one in the run, or the end of the run.
            int runEnd = run.Utf16Start + run.Utf16Length;
            int next = 0;
            int previousCluster = -1;
            for (int i = 0; i < count; i++)
            {
                int position = (int)(keys[i] & 0xFFFFFFFF);
                int g = rightToLeft ? count - 1 - position : position;
                int cluster = glyphs[g].Cluster;
                string? clusterText = null;
                if (cluster != previousCluster)
                {
                    if (next <= i)
                    {
                        next = i + 1;
                    }

                    while (next < count && (int)(keys[next] >> 32) == cluster)
                    {
                        next++;
                    }

                    clusterText = ClusterText(text, cluster, next < count ? (int)(keys[next] >> 32) : runEnd);
                    if (EndsWithFormat(clusterText))
                    {
                        // pdf.js drops a glyph whose text ends with a format character (a zero width non-joiner): end the text with the joiner too.
                        clusterText += PdfEmbeddedFont.ContinuationText;
                    }

                    previousCluster = cluster;
                }

                int code = font.UseGlyph(glyphs[g].Glyph, clusterText);
                double y = baselineY + (glyphs[g].YOffset * scale);
                if (NeedsItemOfItsOwn(clusterText ?? PdfEmbeddedFont.ContinuationText))
                {
                    // Size 1 and a scaled matrix draw the same as the real size; a second such glyph in a row alternates with size 2 so that it differs from the one before.
                    double unit = content.CurrentFont is { } current && current.Name == fontName && current.Size == 1 ? 2 : 1;
                    SetFont(content, fontName, unit);
                    _ = content.TextMatrix(size / unit, 0, 0, size / unit, x[g], y).ShowCode(code);
                    continue;
                }

                SetFont(content, fontName, size);
                _ = content.TextPlace(x[g], y).ShowCode(code);
            }
        }
        finally
        {
            ArrayPool<double>.Shared.Return(x);
            ArrayPool<long>.Shared.Return(keys);
        }
    }

    private static readonly string[] SingleChars = new string[256];

    /// <summary>The slice of the text from <paramref name="start"/> to <paramref name="end"/>; a single Latin-1 character is a string kept and shared, so that the common one-character cluster allocates nothing.</summary>
    private static string ClusterText(string text, int start, int end)
    {
        if (end - start == 1 && end <= text.Length && text[start] < 256)
        {
            char c = text[start];
            return SingleChars[c] ??= c.ToString();
        }

        return text[start..end];
    }

    private static void SetFont(ContentStreamBuilder content, string fontName, double size)
    {
        if (content.CurrentFont is not { } current || current.Name != fontName || current.Size != size)
        {
            content.Font(fontName, size);
        }
    }

    private static bool EndsWithFormat(string text)
    {
        Rune last = default;
        foreach (Rune rune in text.EnumerateRunes())
        {
            last = rune;
        }

        return Rune.GetUnicodeCategory(last) == UnicodeCategory.Format;
    }

    /// <summary>
    /// Whether pdf.js would merge the glyph into the item before it or drop it: a text with a non-spacing mark anywhere counts for it as a zero-width diacritic
    /// (no position check, and the item it joins is reversed as a whole when it is right to left), and a text that ends with a format character is dropped.
    /// </summary>
    private static bool NeedsItemOfItsOwn(string text)
    {
        foreach (Rune rune in text.EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) == UnicodeCategory.NonSpacingMark)
            {
                return true;
            }
        }

        return EndsWithFormat(text);
    }
}
