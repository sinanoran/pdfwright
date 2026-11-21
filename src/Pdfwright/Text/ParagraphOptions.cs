using Pdfwright.Unicode;

namespace Pdfwright.Text;

/// <summary>
/// How one stretch of a paragraph is set: which fonts may draw it (in order of preference), at what size, in which language and colour.
/// A paragraph with one style is the common case; the document layer gives a paragraph one style for every distinct style of its runs.
/// </summary>
internal sealed class RunStyle
{
    /// <summary>Indices into the font list given to <see cref="ParagraphLayouter.Layout"/>, in order of preference: the first font that covers a character draws it.</summary>
    internal required int[] FontOrder { get; init; }

    /// <summary>The font size in points; greater than zero.</summary>
    internal required double Size { get; init; }

    /// <summary>A BCP 47 language tag, or <see langword="null"/>.</summary>
    internal string? Language { get; init; }

    /// <summary>The fill colour of the glyphs, or <see langword="null"/> to draw in whatever colour the content stream has.</summary>
    internal Color? Color { get; init; }
}

/// <summary>A stretch of the paragraph's text (UTF-16 indices) that is set in the style with the given index.</summary>
internal readonly record struct StyledSpan(int Start, int Length, int Style);

/// <summary>What <see cref="ParagraphLayouter.Layout"/> needs to know besides the text and the fonts.</summary>
internal sealed class ParagraphOptions
{
    /// <summary>The font size in points; greater than zero. With <see cref="Styles"/> it is not used for drawing (each style has its own); it must still be positive.</summary>
    internal required double Size { get; init; }

    /// <summary>The width lines may fill, in points, or <see langword="null"/> for no limit (the paragraph then breaks only at hard line breaks).</summary>
    internal double? MaxWidth { get; init; }

    /// <summary>
    /// Where a line sits in <see cref="MaxWidth"/>, or, when there is none, in the width of the widest line. A line wider than that
    /// (a single glyph cluster, which may span several grapheme clusters) is aligned to <see cref="TextAlignment.Start"/> whatever this says, so that it overflows on the side
    /// where reading ends rather than on the side where it begins.
    /// </summary>
    internal TextAlignment Alignment { get; init; } = TextAlignment.Start;

    /// <summary>0 (left to right), 1 (right to left) or <see cref="UnicodeBidi.AutoLevel"/>: the paragraph's base direction.</summary>
    internal int ParagraphLevel { get; init; } = UnicodeBidi.AutoLevel;

    /// <summary>A BCP 47 language tag that applies to every run (HarfBuzz uses it to choose language-specific forms), or <see langword="null"/>. Not used when <see cref="Styles"/> is given.</summary>
    internal string? Language { get; init; }

    /// <summary>A multiplier of the natural line pitch (the line's ascent plus its descent); greater than zero. The extra room goes below the line.</summary>
    internal double LineSpacing { get; init; } = 1.0;

    /// <summary>
    /// The styles of a paragraph set in more than one: with <see cref="Spans"/> they say, for every stretch of the text, the fonts, size, language and colour.
    /// When it is <see langword="null"/>, the paragraph has one style made of <see cref="Size"/>, <see cref="Language"/> and all the fonts in the order given.
    /// </summary>
    internal IReadOnlyList<RunStyle>? Styles { get; init; }

    /// <summary>The stretches that tile the text, in order, each with the index of its style in <see cref="Styles"/>. Required when <see cref="Styles"/> is given.</summary>
    internal IReadOnlyList<StyledSpan>? Spans { get; init; }
}
