using Pdfwright.Fonts;
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
    internal double X { get; }

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

    /// <summary>
    /// The distance from the top of the line to its baseline, in points (positive), from the fonts used on the line. A line with nothing
    /// visible takes the font of the nearest line that has content (the one before it, else the one after it, else the first font).
    /// </summary>
    internal required double Ascent { get; init; }

    /// <summary>The distance from the baseline to the bottom of the line, in points (positive), chosen like <see cref="Ascent"/>.</summary>
    internal required double Descent { get; init; }

    /// <summary>
    /// The distance from the top of this line to the top of the next one: <c>(Ascent + Descent) * LineSpacing</c>. The baseline of a line is its
    /// <see cref="Ascent"/> below its top, so the next baseline is <c>Pitch - Ascent + Ascent of the next line</c> below this one; the extra
    /// room of a line spacing above 1 is under the line.
    /// </summary>
    internal required double Pitch { get; init; }

    /// <summary>The runs of the line in logical order; each has its place in <see cref="PlacedRun.X"/>.</summary>
    internal required IReadOnlyList<PlacedRun> Runs { get; init; }
}

/// <summary>The lines of one paragraph, and what it takes to draw them.</summary>
internal sealed class ParagraphLayout
{
    private readonly TrueTypeFont[] _fonts;
    private readonly IReadOnlyList<StyledSpan>? _spans;
    private readonly bool _mixedColours;

    internal ParagraphLayout(string text, IReadOnlyList<RunStyle> styles, IReadOnlyList<LaidOutLine> lines, IReadOnlyList<int> missing, IReadOnlyList<ShapingFont> fonts, byte paragraphLevel = 0, IReadOnlyList<StyledSpan>? spans = null)
    {
        ParagraphLevel = paragraphLevel;
        Text = text;
        Styles = styles;
        Lines = lines;
        Missing = missing;
        _fonts = [.. fonts.Select(f => f.Font)];
        _spans = spans;
        _mixedColours = spans is { Count: > 1 } && styles.Select(s => s.Color).Distinct().Skip(1).Any();
        Width = lines.Count == 0 ? 0 : lines.Max(l => l.Width);
        Height = lines.Sum(l => l.Pitch);
    }

    /// <summary>The text that was laid out; the <see cref="LaidOutLine.Start"/> of each line indexes into it.</summary>
    internal string Text { get; }

    /// <summary>The styles the text is set in; a run's <see cref="ShapedRun.Style"/> indexes into it. One style for a paragraph set in one.</summary>
    internal IReadOnlyList<RunStyle> Styles { get; }

    /// <summary>The font size in points of the first style (the size of a paragraph set in one).</summary>
    internal double Size => Styles[0].Size;

    /// <summary>The base level of the paragraph, as the bidirectional algorithm resolved it: 0 for left to right, 1 for right to left.</summary>
    internal byte ParagraphLevel { get; }

    /// <summary>The paragraph reads from right to left.</summary>
    internal bool IsRightToLeft => (ParagraphLevel & 1) == 1;

    /// <summary>The lines in order. A paragraph always has at least one (an empty text has one empty line).</summary>
    internal IReadOnlyList<LaidOutLine> Lines { get; }

    /// <summary>The characters no font had a glyph for, as UTF-16 indices (see <see cref="ShapedParagraph.Missing"/>): they are drawn as the .notdef glyph.</summary>
    internal IReadOnlyList<int> Missing { get; }

    /// <summary>The number of lines.</summary>
    internal int LineCount => Lines.Count;

    /// <summary>The width of the widest line's content, in points.</summary>
    internal double Width { get; }

    /// <summary>The sum of the lines' pitches, in points.</summary>
    internal double Height { get; }

    /// <summary>
    /// Writes every line into <paramref name="content"/> with <see cref="GlyphRunWriter"/>, so that every reader gets the text in logical order.
    /// The whole paragraph is one <c>BT</c> / <c>ET</c> pair, which this method writes (the text state must not already be inside one). The top
    /// left corner of the paragraph is at (<paramref name="left"/>, <paramref name="top"/>) in PDF space (y up): the baseline of a line is its
    /// <see cref="LaidOutLine.Ascent"/> below the line's top, and the top of the next line is <see cref="LaidOutLine.Pitch"/> below that top.
    /// <paramref name="embedded"/> and <paramref name="fontResourceNames"/> hold one entry for each font that was given to
    /// <see cref="ParagraphLayouter.Layout"/>, in the same order, and each embedded font must be the very font (the same <see cref="TrueTypeFont"/> object) the paragraph was laid out with at that place.
    /// </summary>
    /// <remarks><paramref name="firstLine"/> and <paramref name="lineCount"/> (all the rest when negative) draw only some of the lines, the first of them with its top at <paramref name="top"/>: the flow of a document uses it to split a paragraph between pages. A style with a colour sets the fill colour before its runs.</remarks>
    /// <exception cref="ArgumentException">The arrays do not match the fonts the paragraph was laid out with.</exception>
    internal void Draw(ContentStreamBuilder content, PdfEmbeddedFont?[] embedded, string?[] fontResourceNames, double left, double top, int firstLine = 0, int lineCount = -1)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(embedded);
        ArgumentNullException.ThrowIfNull(fontResourceNames);
        if (embedded.Length != _fonts.Length || fontResourceNames.Length != _fonts.Length)
        {
            throw new ArgumentException(
                $"The paragraph was laid out with {_fonts.Length} font(s), and {embedded.Length} embedded font(s) and {fontResourceNames.Length} resource name(s) were given: there must be one of each for every font, in the same order.");
        }

        // A font that no run uses may be null (it need not be embedded); one that a run uses must be there and be the very font.
        int count = lineCount < 0 ? Lines.Count - firstLine : lineCount;
        if (firstLine < 0 || firstLine + count > Lines.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(firstLine), firstLine, $"Lines {firstLine} to {firstLine + count} are outside the {Lines.Count} lines of the paragraph.");
        }

        HashSet<int> used = [.. Lines.Skip(firstLine).Take(count).SelectMany(l => l.Runs).Select(r => r.Run.FontIndex)];
        for (int i = 0; i < _fonts.Length; i++)
        {
            if (!used.Contains(i) && embedded[i] is null && fontResourceNames[i] is null)
            {
                continue;
            }

            if (embedded[i] is null || fontResourceNames[i] is null)
            {
                throw new ArgumentException($"Font {i} has no embedded font or no resource name.");
            }

            if (!ReferenceEquals(embedded[i]!.Font, _fonts[i]))
            {
                throw new ArgumentException($"Embedded font {i} is not the font the paragraph was laid out with at that place ({_fonts[i].PostScriptName}, found {embedded[i]!.Font.PostScriptName}): the glyph numbers and widths would be those of another font.");
            }
        }

        content.BeginText();
        double lineTop = top;
        Color? current = null;
        foreach (LaidOutLine line in Lines.Skip(firstLine).Take(count))
        {
            double baseline = lineTop - line.Ascent;
            foreach (PlacedRun placed in line.Runs)
            {
                int font = placed.Run.FontIndex;
                RunStyle style = Styles[placed.Run.Style];
                if (!_mixedColours)
                {
                    SetColour(content, style.Color, ref current);
                    GlyphRunWriter.Write(content, embedded[font]!, fontResourceNames[font]!, placed.Run, Text, left + placed.X, baseline, style.Size);
                    continue;
                }

                // A run may hold characters of several colours (a colour does not change how a run is shaped): it is written in parts, one for each stretch of its glyphs of one colour.
                double scale = style.Size / _fonts[font].UnitsPerEm;
                foreach ((ShapedRun part, double offset, Color? colour) in ColourParts(placed.Run, scale))
                {
                    SetColour(content, colour, ref current);
                    GlyphRunWriter.Write(content, embedded[font]!, fontResourceNames[font]!, part, Text, left + placed.X + offset, baseline, style.Size);
                }
            }

            lineTop -= line.Pitch;
        }

        content.EndText();
    }

    private static void SetColour(ContentStreamBuilder content, Color? colour, ref Color? current)
    {
        if (colour is { } color && current != color)
        {
            _ = color.IsGray ? content.FillGray(color.R) : content.FillRgb(color.R, color.G, color.B);
            current = color;
        }
    }

    /// <summary>The colour of the glyph standing for the characters that start at a UTF-16 index: that of the style of the span that holds the index.</summary>
    private Color? ColourAt(int index)
    {
        IReadOnlyList<StyledSpan> spans = _spans!;
        int low = 0;
        int high = spans.Count - 1;
        while (low < high)
        {
            int middle = low + ((high - low + 1) / 2);
            if (spans[middle].Start <= index)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }

        return Styles[spans[low].Style].Color;
    }

    /// <summary>
    /// A run cut into the stretches of its glyphs that are of one colour, each with its distance from the left edge of the run and its colour, in the order they are to be written:
    /// logical order, which for a right-to-left run is the reverse of the order the glyphs are listed in. A run of one colour is returned whole.
    /// </summary>
    private List<(ShapedRun Part, double Offset, Color? Colour)> ColourParts(ShapedRun run, double scale)
    {
        ShapedGlyph[] glyphs = run.Glyphs;
        List<(ShapedRun, double, Color?)> parts = [];
        if (glyphs.Length == 0)
        {
            return parts;
        }

        int[] clusters = [.. glyphs.Select(g => g.Cluster).Distinct().Order()];
        int runEnd = run.Utf16Start + run.Utf16Length;
        double pen = 0;
        int first = 0;
        double firstPen = 0;
        Color? colour = ColourAt(glyphs[0].Cluster);
        for (int g = 1; g <= glyphs.Length; g++)
        {
            Color? next = g < glyphs.Length ? ColourAt(glyphs[g].Cluster) : colour;
            pen += glyphs[g - 1].XAdvance * scale;
            if (g < glyphs.Length && next == colour)
            {
                continue;
            }

            ShapedGlyph[] slice = g - first == glyphs.Length ? glyphs : glyphs[first..g];
            int lowest = slice.Min(s => s.Cluster);
            int highest = slice.Max(s => s.Cluster);
            int after = Array.BinarySearch(clusters, highest) + 1;
            int end = after < clusters.Length ? clusters[after] : runEnd;
            ShapedRun part = slice == glyphs
                ? run
                : new ShapedRun { Utf16Start = lowest, Utf16Length = end - lowest, Level = run.Level, Script = run.Script, FontIndex = run.FontIndex, Style = run.Style, Glyphs = slice };
            parts.Add((part, firstPen, colour));
            first = g;
            firstPen = pen;
            colour = next;
        }

        if (run.IsRightToLeft)
        {
            parts.Reverse();
        }

        return parts;
    }
}
