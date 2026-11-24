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

    // The lists of one item made of this style, for a paragraph set in it alone (a table cell): made on the first ask and shared by every paragraph in the style, on any thread (two threads that ask first
    // may each make one, which does no harm: they hold the same). Nothing changes them: they are read-only lists to everything that is given them.
    private RunStyle[]? _alone;
    private int[][]? _fontOrders;
    private string?[]? _languages;

    /// <summary>A list of styles that holds this style alone.</summary>
    internal RunStyle[] Alone => _alone ??= [this];

    /// <summary>The font orders of a paragraph set in this style alone (see <see cref="ShapeStyling.FontOrders"/>).</summary>
    internal IReadOnlyList<int[]> FontOrders => _fontOrders ??= [FontOrder];

    /// <summary>The languages of a paragraph set in this style alone (see <see cref="ShapeStyling.Languages"/>).</summary>
    internal IReadOnlyList<string?> Languages => _languages ??= [Language];
}

/// <summary>A stretch of the paragraph's text (UTF-16 indices) that is set in the style with the given index.</summary>
internal readonly record struct StyledSpan(int Start, int Length, int Style)
{
    /// <summary>The longest text whose list of one span is kept (see <see cref="Whole"/>).</summary>
    private const int KeptWholeLength = 256;

    private static readonly StyledSpan[]?[] KeptWhole = new StyledSpan[]?[KeptWholeLength + 1];

    /// <summary>
    /// The list of one span, of style 0, over a text of <paramref name="length"/> characters: a paragraph set in one style. The lists of texts up to 256 characters are made once and shared, read-only, by every
    /// paragraph of that length, so that the cells of a table do not each make one; two threads that ask first for a length may each make one, which does no harm.
    /// </summary>
    internal static StyledSpan[] Whole(int length)
    {
        if (length > KeptWholeLength)
        {
            return [new StyledSpan(0, length, 0)];
        }

        return KeptWhole[length] ??= [new StyledSpan(0, length, 0)];
    }
}

/// <summary>What <see cref="ParagraphLayouter.Layout"/> needs to know besides the text and the fonts.</summary>
internal sealed class ParagraphOptions
{
    /// <summary>The font size in points; greater than zero. With <see cref="Styles"/> it is not used for drawing (each style has its own); it must still be positive.</summary>
    internal required double Size { get; set; }

    /// <summary>The width lines may fill, in points, or <see langword="null"/> for no limit (the paragraph then breaks only at hard line breaks).</summary>
    internal double? MaxWidth { get; set; }

    /// <summary>
    /// Where a line sits in <see cref="MaxWidth"/>, or, when there is none, in the width of the widest line. A line wider than that
    /// (a single glyph cluster, which may span several grapheme clusters) is aligned to <see cref="TextAlignment.Start"/> whatever this says, so that it overflows on the side
    /// where reading ends rather than on the side where it begins.
    /// </summary>
    internal TextAlignment Alignment { get; set; } = TextAlignment.Start;

    /// <summary>0 (left to right), 1 (right to left) or <see cref="UnicodeBidi.AutoLevel"/>: the paragraph's base direction.</summary>
    internal int ParagraphLevel { get; set; } = UnicodeBidi.AutoLevel;

    /// <summary>A BCP 47 language tag that applies to every run (HarfBuzz uses it to choose language-specific forms), or <see langword="null"/>. Not used when <see cref="Styles"/> is given.</summary>
    internal string? Language { get; set; }

    /// <summary>A multiplier of the natural line pitch (the line's ascent plus its descent); greater than zero. The extra room goes below the line.</summary>
    internal double LineSpacing { get; set; } = 1.0;

    /// <summary>
    /// The styles of a paragraph set in more than one: with <see cref="Spans"/> they say, for every stretch of the text, the fonts, size, language and colour.
    /// When it is <see langword="null"/>, the paragraph has one style made of <see cref="Size"/>, <see cref="Language"/> and all the fonts in the order given.
    /// </summary>
    internal IReadOnlyList<RunStyle>? Styles { get; set; }

    /// <summary>The stretches that tile the text, in order, each with the index of its style in <see cref="Styles"/>. Required when <see cref="Styles"/> is given.</summary>
    internal IReadOnlyList<StyledSpan>? Spans { get; set; }
}
