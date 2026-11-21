namespace Pdfwright.Text;

/// <summary>
/// How much room text takes. It asks <see cref="ParagraphLayouter"/>, the engine that draws, so a measured size and a drawn size are one
/// computation and cannot disagree.
/// </summary>
internal static class TextMeasurer
{
    /// <summary>
    /// The width of the widest line, the height of all lines (their pitches added) and the number of lines of <paramref name="text"/> at
    /// <paramref name="size"/> points, broken to <paramref name="maxWidth"/> when it is given. <paramref name="text"/> is one paragraph, as
    /// for <see cref="ParagraphLayouter.Layout"/> (<see cref="ArgumentException"/> for a paragraph separator inside it).
    /// </summary>
    internal static (double Width, double Height, int LineCount) Measure(string text, IReadOnlyList<ShapingFont> fonts, double size, double? maxWidth = null, string? language = null)
    {
        ParagraphLayout layout = ParagraphLayouter.Layout(text, fonts, new ParagraphOptions { Size = size, MaxWidth = maxWidth, Language = language });
        return (layout.Width, layout.Height, layout.LineCount);
    }
}
