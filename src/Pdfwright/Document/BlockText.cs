using System.Text;
using Pdfwright.Pdf;
using Pdfwright.Text;
using Pdfwright.Unicode;

namespace Pdfwright.Document;

/// <summary>A style with every property decided: what a run is set in after the document's, the paragraph's and its own styles are put together.</summary>
internal sealed record ResolvedStyle(string Family, double Size, int Weight, bool Italic, Color Color, string? Language)
{
    /// <summary>The style of the document: the default style over the library's defaults, with the first font's family when none is named.</summary>
    internal static ResolvedStyle Base(FontCollection.Snapshot fonts, TextStyle? defaults, string? documentLanguage)
    {
        ResolvedStyle library = new(fonts.Entries[0].Family, 10, 400, false, Color.Black, Label(documentLanguage));
        return library.Apply(defaults);
    }

    /// <summary>This style with the properties that <paramref name="over"/> sets replaced.</summary>
    internal ResolvedStyle Apply(TextStyle? over)
    {
        if (over is null)
        {
            return this;
        }

        double size = over.Size?.Points ?? Size;
        if (!(size > 0) || !double.IsFinite(size))
        {
            throw new ArgumentOutOfRangeException(nameof(over), size, "A font size is a positive, finite length.");
        }

        int weight = over.Weight ?? Weight;
        if (weight is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(over), weight, "A font weight is from 1 to 1000.");
        }

        return new ResolvedStyle(Label(over.Family) ?? Family, size, weight, over.Italic ?? Italic, over.Color ?? Color, Label(over.Language) ?? Language);
    }

    /// <summary>
    /// A family or a language a caller gave, as it is used and reported: read by the rule for metadata (controls removed, a line break or tab a space, a lone surrogate U+FFFD, white space at the ends trimmed), so that no control reaches the
    /// language of the shaper (BCP 47), the language of a missing glyph or the families reported as missing. A string that is empty only after it is read counts as not given (<see langword="null"/>). The caller's own record is not changed.
    /// </summary>
    private static string? Label(string? text) => text is null || text.Length == 0 ? text : (TextNormalizer.Metadata(text) is { Length: > 0 } cleaned ? cleaned : null);

}

/// <summary>A paragraph laid out, and what is needed to say where a character of it is set.</summary>
internal sealed class StyledLayout
{
    internal required ParagraphLayout Layout { get; init; }

    internal required IReadOnlyList<ResolvedStyle> Styles { get; init; }

    internal required IReadOnlyList<StyledSpan> Spans { get; init; }

    /// <summary>Where the paragraph stands in a list (the room its marker takes, and the marker), or <see langword="null"/> for a paragraph that is not in one.</summary>
    internal ListPlacement? List { get; init; }

    /// <summary>
    /// The structure element of the paragraph in a tagged document (a <c>P</c>, an <c>H1</c>), still light: it gets its object when its first line is drawn,
    /// and every piece of the paragraph drawn on a page is marked content of it. <see langword="null"/> for text with no element of its own.
    /// </summary>
    internal PdfStructureElement? Tag { get; set; }

    /// <summary>The language of the character at a UTF-16 index of the paragraph's text.</summary>
    internal string? LanguageAt(int index)
    {
        foreach (StyledSpan span in Spans)
        {
            if (index >= span.Start && index < span.Start + span.Length)
            {
                return Styles[span.Style].Language;
            }
        }

        return Styles[0].Language;
    }
}

/// <summary>A paragraph of a list item: how far its text is from the start edge, and the marker set before its first line.</summary>
internal sealed class ListPlacement
{
    /// <summary>The width of the space the paragraph is placed in (the paragraph is laid out in that less <see cref="Inset"/>).</summary>
    internal required double ContainerWidth { get; init; }

    /// <summary>The distance in points from the start edge to the start of the text.</summary>
    internal required double Inset { get; init; }

    /// <summary>The distance in points from the start edge to the start of the marker.</summary>
    internal required double MarkerInset { get; init; }

    /// <summary>The start edge is the right one: the paragraph reads from right to left.</summary>
    internal required bool RightToLeft { get; init; }

    /// <summary>The marker laid out without a width limit, or <see langword="null"/> for a paragraph that follows the first of its item.</summary>
    internal StyledLayout? Marker { get; init; }
}

/// <summary>Turns the runs of a block into one laid out paragraph, the way the flow and the public text measurer both do it.</summary>
internal static class BlockText
{
    /// <summary>
    /// Lays out <paramref name="runs"/> as one paragraph in <paramref name="width"/> points (no limit when <see langword="null"/>). Every run is set in
    /// <paramref name="baseStyle"/> with the run's own style over it; text is normalised first (<see cref="TextNormalizer"/>): a line break of any kind inside a run is one hard line break, other controls are dropped, a broken surrogate is U+FFFD.
    /// </summary>
    internal static StyledLayout Layout(
        IReadOnlyList<TextRun> runs,
        ResolvedStyle baseStyle,
        FontCollection.Snapshot fonts,
        double? width,
        TextAlignment alignment,
        TextDirection direction,
        double lineSpacing)
    {
        // The text is joined from the runs' chunks; a paragraph of one chunk (a table cell) is that string, with no builder.
        string? only = null;
        StringBuilder? text = null;
        int length = 0;
        List<ResolvedStyle> styles = [baseStyle];
        List<StyledSpan> spans = [];
        foreach (TextRun run in runs)
        {
            string chunk = TextNormalizer.Normalize(run.Text);
            if (chunk.Length == 0)
            {
                continue;
            }

            ResolvedStyle style = baseStyle.Apply(run.Style);
            int index = styles.IndexOf(style);
            if (index < 0)
            {
                index = styles.Count;
                styles.Add(style);
            }

            if (spans.Count > 0 && spans[^1].Style == index)
            {
                StyledSpan last = spans[^1];
                spans[^1] = last with { Length = last.Length + chunk.Length };
            }
            else
            {
                spans.Add(new StyledSpan(length, chunk.Length, index));
            }

            if (only is null && text is null)
            {
                only = chunk;
            }
            else
            {
                text ??= new StringBuilder(only);
                only = null;
                text.Append(chunk);
            }

            length += chunk.Length;
        }

        // A loop, not a Select: no iterator, no delegate and no closure for every paragraph; the styles come from the snapshot's table, so a cell in a style that another cell used builds nothing.
        RunStyle[] runStyles = new RunStyle[styles.Count];
        for (int i = 0; i < runStyles.Length; i++)
        {
            runStyles[i] = fonts.RunStyleOf(styles[i]);
        }

        ParagraphLayout layout = ParagraphLayouter.Layout(
            only ?? text?.ToString() ?? string.Empty,
            fonts.Shaping,
            new ParagraphOptions
            {
                Size = baseStyle.Size,
                MaxWidth = width,
                Alignment = alignment,
                LineSpacing = lineSpacing,
                ParagraphLevel = direction switch
                {
                    TextDirection.LeftToRight => 0,
                    TextDirection.RightToLeft => 1,
                    _ => UnicodeBidi.AutoLevel,
                },
                Styles = runStyles,
                Spans = spans,
            });
        return new StyledLayout { Layout = layout, Styles = styles, Spans = spans };
    }
}
