namespace Pdfwright.Text;

/// <summary>
/// How much room text takes. It asks <see cref="ParagraphLayouter"/>, the engine that draws, with the same <see cref="ParagraphOptions"/> that
/// draw, so a measured size and a drawn size are one computation and cannot disagree.
/// </summary>
internal static class TextMeasurer
{
    /// <summary>
    /// The width of the widest line, the height of all lines (their pitches added) and the number of lines of <paramref name="text"/> laid
    /// out with <paramref name="options"/>. <paramref name="text"/> is one paragraph, as for <see cref="ParagraphLayouter.Layout"/>
    /// (<see cref="ArgumentException"/> for a paragraph separator inside it).
    /// </summary>
    internal static (double Width, double Height, int LineCount) Measure(string text, IReadOnlyList<ShapingFont> fonts, ParagraphOptions options)
    {
        ParagraphLayout layout = ParagraphLayouter.Layout(text, fonts, options);
        return (layout.Width, layout.Height, layout.LineCount);
    }
}
