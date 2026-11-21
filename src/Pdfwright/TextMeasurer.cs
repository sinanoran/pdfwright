using Pdfwright.Document;

namespace Pdfwright;

/// <summary>The room a piece of text takes.</summary>
public readonly record struct TextMeasurement
{
    /// <summary>Makes a measurement.</summary>
    /// <param name="width">The width of the widest line.</param>
    /// <param name="height">The height of all the lines.</param>
    /// <param name="lineCount">The number of lines.</param>
    public TextMeasurement(Length width, Length height, int lineCount)
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
public sealed class TextMeasurer
{
    private readonly FontCollection _fonts;
    private readonly TextStyle? _defaultStyle;

    /// <summary>Makes a measurer over a font collection.</summary>
    /// <param name="fonts">The fonts to measure with; at least one.</param>
    /// <param name="defaultStyle">The style that the style passed to <see cref="Measure"/> is put over, or <see langword="null"/> for the library's defaults (the same as a document's <see cref="PdfDocumentOptions.DefaultStyle"/>).</param>
    /// <exception cref="ArgumentException">The collection holds no font.</exception>
    public TextMeasurer(FontCollection fonts, TextStyle? defaultStyle = null)
    {
        ArgumentNullException.ThrowIfNull(fonts);
        if (fonts.Count == 0)
        {
            throw new ArgumentException("The collection holds no font.", nameof(fonts));
        }

        _fonts = fonts;
        _defaultStyle = defaultStyle;
    }

    /// <summary>Measures <paramref name="text"/> as one paragraph.</summary>
    /// <param name="text">The text; a line break inside it ends a line.</param>
    /// <param name="style">The style, over the default style of the measurer; <see langword="null"/> for the default.</param>
    /// <param name="maxWidth">The width lines may fill, or <see langword="null"/> for no limit (lines then break only at hard line breaks).</param>
    /// <param name="direction">The base direction of the paragraph.</param>
    /// <returns>The width of the widest line, the height of all lines and the number of lines.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The width is negative or not finite, or the style's size or weight is out of range.</exception>
    public TextMeasurement Measure(string text, TextStyle? style = null, Length? maxWidth = null, TextDirection direction = TextDirection.Auto)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (maxWidth is { } width && (!width.IsFinite || width < Length.Zero))
        {
            throw new ArgumentOutOfRangeException(nameof(maxWidth), width, "The width is zero or more and finite.");
        }

        FontCollection.Snapshot fonts = _fonts.Current;
        ResolvedStyle resolved = ResolvedStyle.Base(fonts, _defaultStyle, null).Apply(style);
        StyledLayout layout = BlockText.Layout([new TextRun(text)], resolved, fonts, maxWidth?.Points, TextAlignment.Start, direction, 1.0);
        return new TextMeasurement(Length.Pt(layout.Layout.Width), Length.Pt(layout.Layout.Height), layout.Layout.LineCount);
    }
}
