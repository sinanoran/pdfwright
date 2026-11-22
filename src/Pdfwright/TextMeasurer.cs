using Pdfwright.Document;

namespace Pdfwright;

/// <summary>The room a piece of text takes. A measurement is read from <see cref="TextMeasurer.Measure"/>, never made.</summary>
public readonly record struct TextMeasurement
{
    internal TextMeasurement(Length width, Length height, int lineCount)
    {
        Width = width;
        Height = height;
        LineCount = lineCount;
    }

    /// <summary>The width of the widest line's content (the white space at the end of a line hangs outside and is not counted).</summary>
    public Length Width { get; }

    /// <summary>The height of all the lines: the sum of their pitches.</summary>
    public Length Height { get; }

    /// <summary>The number of lines; an empty text is one empty line.</summary>
    public int LineCount { get; }
}

/// <summary>
/// Measures text with the very engine that draws it (shaping, font fallback, bidi and line breaking), so a measured size and a drawn size are one computation
/// and cannot disagree. Chart authors use it to place labels. An instance is safe to use from several threads.
/// </summary>
/// <remarks>
/// A measurement matches what a document draws only when it is made the way the document is: with the same fonts, the same default style, the same language
/// (<c>language</c> here is <see cref="PdfDocumentOptions.Language"/> there), the same direction (the <c>direction</c> of <see cref="Measure"/> is the paragraph's or the document's
/// <see cref="PdfDocumentOptions.Direction"/>) and the same line spacing (<see cref="Paragraph.LineSpacing"/>). Text that sets none of its own language is measured in the language given here.
/// </remarks>
public sealed class TextMeasurer
{
    private readonly FontCollection _fonts;
    private readonly TextStyle? _defaultStyle;
    private readonly string? _language;
    private readonly double _lineSpacing;

    /// <summary>Makes a measurer over a font collection.</summary>
    /// <param name="fonts">The fonts to measure with; at least one.</param>
    /// <param name="defaultStyle">The style that the style passed to <see cref="Measure"/> is put over, or <see langword="null"/> for the library's defaults (the same as a document's <see cref="PdfDocumentOptions.DefaultStyle"/>).</param>
    /// <param name="language">The BCP 47 language of text whose style sets none (a document's <see cref="PdfDocumentOptions.Language"/>), or <see langword="null"/> for none.</param>
    /// <param name="lineSpacing">The multiplier of the natural line height, as <see cref="Paragraph.LineSpacing"/>; 1 by default.</param>
    /// <exception cref="ArgumentException">The collection holds no font.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The line spacing is not a finite number greater than zero.</exception>
    public TextMeasurer(FontCollection fonts, TextStyle? defaultStyle = null, string? language = null, double lineSpacing = 1.0)
    {
        ArgumentNullException.ThrowIfNull(fonts);
        if (fonts.Count == 0)
        {
            throw new ArgumentException("The collection holds no font.", nameof(fonts));
        }

        _fonts = fonts;
        _defaultStyle = defaultStyle;
        _language = language;
        _lineSpacing = Check.LineSpacing(lineSpacing, nameof(lineSpacing));
    }

    /// <summary>Measures <paramref name="text"/> as one paragraph.</summary>
    /// <param name="text">The text; a line break of any kind inside it (line feed, carriage return, U+0085, vertical tab, form feed, U+2028, U+2029) is a hard line break and ends a line, other control characters are removed, and a surrogate without its pair is measured as U+FFFD.</param>
    /// <param name="style">The style, over the default style of the measurer; <see langword="null"/> for the default.</param>
    /// <param name="maxWidth">The width lines may fill, or <see langword="null"/> for no limit (lines then break only at hard line breaks).</param>
    /// <param name="direction">The base direction of the paragraph.</param>
    /// <returns>The width of the widest line, the height of all lines and the number of lines.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The width is negative or not finite, the direction is not a <see cref="TextDirection"/>, or the style's size or weight is out of range.</exception>
    /// <exception cref="ObjectDisposedException">The font collection has been disposed.</exception>
    public TextMeasurement Measure(string text, TextStyle? style = null, Length? maxWidth = null, TextDirection direction = TextDirection.Auto)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (maxWidth is { } width && (!width.IsFinite || width < Length.Zero))
        {
            throw new ArgumentOutOfRangeException(nameof(maxWidth), width, "The width is zero or more and finite.");
        }

        _ = Check.Defined(direction, nameof(direction));
        FontCollection.Snapshot fonts = _fonts.Current;
        ResolvedStyle resolved = ResolvedStyle.Base(fonts, _defaultStyle, _language).Apply(style);
        StyledLayout layout = BlockText.Layout([new TextRun(text)], resolved, fonts, maxWidth?.Points, TextAlignment.Start, direction, _lineSpacing);
        return new TextMeasurement(Length.Pt(layout.Layout.Width), Length.Pt(layout.Layout.Height), layout.Layout.LineCount);
    }
}
