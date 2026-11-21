using Pdfwright.Unicode;

namespace Pdfwright.Text;

/// <summary>
/// Where the lines of a paragraph sit in the width they are given. The names are logical, never left and right, so that the same setting
/// works for a right-to-left paragraph: <see cref="Start"/> is the left edge in a left-to-right paragraph and the right edge in a right-to-left one.
/// There is no justification and no hyphenation in 0.1.
/// </summary>
internal enum TextAlignment
{
    /// <summary>The edge where the paragraph's text begins.</summary>
    Start,

    /// <summary>In the middle of the width.</summary>
    Center,

    /// <summary>The edge where the paragraph's text ends.</summary>
    End,
}

/// <summary>What <see cref="ParagraphLayouter.Layout"/> needs to know besides the text and the fonts.</summary>
internal sealed class ParagraphOptions
{
    /// <summary>The font size in points; greater than zero.</summary>
    internal required double Size { get; init; }

    /// <summary>The width lines may fill, in points, or <see langword="null"/> for no limit (the paragraph then breaks only at hard line breaks).</summary>
    internal double? MaxWidth { get; init; }

    /// <summary>
    /// Where a line sits in <see cref="MaxWidth"/>, or, when there is none, in the width of the widest line. A line wider than that
    /// (a single unbreakable unit) is aligned to <see cref="TextAlignment.Start"/> whatever this says, so that it overflows on the side
    /// where reading ends rather than on the side where it begins.
    /// </summary>
    internal TextAlignment Alignment { get; init; } = TextAlignment.Start;

    /// <summary>0 (left to right), 1 (right to left) or <see cref="UnicodeBidi.AutoLevel"/>: the paragraph's base direction.</summary>
    internal int ParagraphLevel { get; init; } = UnicodeBidi.AutoLevel;

    /// <summary>A BCP 47 language tag that applies to every run (HarfBuzz uses it to choose language-specific forms), or <see langword="null"/>.</summary>
    internal string? Language { get; init; }

    /// <summary>A multiplier of the natural line pitch (the line's ascent plus its descent); greater than zero. The extra room goes below the line.</summary>
    internal double LineSpacing { get; init; } = 1.0;
}
