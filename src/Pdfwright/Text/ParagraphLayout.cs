using Pdfwright.Pdf;

namespace Pdfwright.Text;

/// <summary>
/// A <see cref="ShapedRun"/> restricted to one line, with the place it is drawn. Restricting cuts by cluster (a glyph belongs to the line
/// that holds the characters it stands for) and never reshapes, so what was shaped across the whole paragraph stays as it was shaped.
/// </summary>
internal sealed class PlacedRun
{
    internal PlacedRun(ShapedRun run, double x, double width, bool isTrailingSpace)
    {
        Run = run;
        X = x;
        Width = width;
        IsTrailingSpace = isTrailingSpace;
    }

    /// <summary>
    /// The glyphs of the line that belong to one run of the paragraph and have one level on this line. The text it covers is
    /// <see cref="ShapedRun.Utf16Start"/> and <see cref="ShapedRun.Utf16Length"/>, within the line; its level is the one the line has (rule L1),
    /// which differs from the paragraph's run only for white space at the end of the line.
    /// </summary>
    internal ShapedRun Run { get; }

    /// <summary>The left edge of the run in points from the paragraph's left edge, already in its visual place and after alignment.</summary>
    internal double X { get; set; }

    /// <summary>The advance width of the run in points.</summary>
    internal double Width { get; }

    /// <summary>The run is the white space at the end of the line: drawn, but not part of the line's <see cref="LaidOutLine.Width"/>.</summary>
    internal bool IsTrailingSpace { get; }
}

/// <summary>One line of a laid out paragraph.</summary>
internal sealed class LaidOutLine
{
    /// <summary>The UTF-16 index in the paragraph's text where the line starts.</summary>
    internal required int Start { get; init; }

    /// <summary>The length of the line's text in UTF-16 units, including white space and the hard line break at its end.</summary>
    internal required int Length { get; init; }

    /// <summary>The width of the visible content in points: the white space at the end of the line (it hangs outside the box) is not counted.</summary>
    internal required double Width { get; init; }

    /// <summary>The offset in points of the content's left edge from the paragraph's left edge, after alignment.</summary>
    internal required double X { get; init; }

    /// <summary>The distance from the top of the line to its baseline, in points (positive), from the fonts used on the line.</summary>
    internal required double Ascent { get; init; }

    /// <summary>The distance from the baseline to the bottom of the line, in points (positive), from the fonts used on the line.</summary>
    internal required double Descent { get; init; }

    /// <summary>The distance from this line's baseline to the next one's: <c>(Ascent + Descent) * LineSpacing</c>.</summary>
    internal required double Pitch { get; init; }

    /// <summary>The runs of the line in logical order; each has its place in <see cref="PlacedRun.X"/>.</summary>
    internal required List<PlacedRun> Runs { get; init; }
}

/// <summary>The lines of one paragraph, and what it takes to draw them.</summary>
internal sealed class ParagraphLayout
{
    internal ParagraphLayout(string text, double size, IReadOnlyList<LaidOutLine> lines)
    {
        Text = text;
        Size = size;
        Lines = lines;
        Width = lines.Count == 0 ? 0 : lines.Max(l => l.Width);
        Height = lines.Sum(l => l.Pitch);
    }

    /// <summary>The text that was laid out; the <see cref="LaidOutLine.Start"/> of each line indexes into it.</summary>
    internal string Text { get; }

    /// <summary>The font size in points.</summary>
    internal double Size { get; }

    /// <summary>The lines in order. A paragraph always has at least one (an empty text has one empty line).</summary>
    internal IReadOnlyList<LaidOutLine> Lines { get; }

    /// <summary>The number of lines.</summary>
    internal int LineCount => Lines.Count;

    /// <summary>The width of the widest line's content, in points.</summary>
    internal double Width { get; }

    /// <summary>The sum of the lines' pitches, in points.</summary>
    internal double Height { get; }

    /// <summary>
    /// Writes every line into <paramref name="content"/>, inside a <c>BT</c> / <c>ET</c> of its own, with <see cref="GlyphRunWriter"/>, so
    /// that every reader gets the text in logical order. The top left corner of the paragraph is at (<paramref name="left"/>,
    /// <paramref name="top"/>) in PDF space (y up); the baseline of each line is its ascent below the line's top. <paramref name="embedded"/>
    /// and <paramref name="fontResourceNames"/> are indexed like the fonts that were given to <see cref="ParagraphLayouter.Layout"/>;
    /// <paramref name="text"/> must be the text that was laid out (it is what the glyph clusters are cut from).
    /// </summary>
    internal void Draw(ContentStreamBuilder content, PdfEmbeddedFont[] embedded, string[] fontResourceNames, string text, double left, double top)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(embedded);
        ArgumentNullException.ThrowIfNull(fontResourceNames);
        ArgumentNullException.ThrowIfNull(text);
        if (!string.Equals(text, Text, StringComparison.Ordinal))
        {
            throw new ArgumentException("The text is not the text that was laid out.", nameof(text));
        }

        content.BeginText();
        double lineTop = top;
        foreach (LaidOutLine line in Lines)
        {
            double baseline = lineTop - line.Ascent;
            foreach (PlacedRun placed in line.Runs)
            {
                int font = placed.Run.FontIndex;
                GlyphRunWriter.Write(content, embedded[font], fontResourceNames[font], placed.Run, text, left + placed.X, baseline, Size);
            }

            lineTop -= line.Pitch;
        }

        content.EndText();
    }
}
