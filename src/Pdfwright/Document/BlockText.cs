using System.Text;
using Pdfwright.Text;
using Pdfwright.Unicode;

namespace Pdfwright.Document;

/// <summary>A style with every property decided: what a run is set in after the document's, the paragraph's and its own styles are put together.</summary>
internal sealed record ResolvedStyle(string Family, double Size, int Weight, bool Italic, Color Color, string? Language)
{
    /// <summary>The style of the document: the default style over the library's defaults, with the first font's family when none is named.</summary>
    internal static ResolvedStyle Base(FontCollection.Snapshot fonts, TextStyle? defaults, string? documentLanguage)
    {
        ResolvedStyle library = new(fonts.Entries[0].Family, 10, 400, false, Color.Black, documentLanguage);
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

        return new ResolvedStyle(over.Family ?? Family, size, weight, over.Italic ?? Italic, over.Color ?? Color, over.Language ?? Language);
    }
}

/// <summary>A paragraph laid out, and what is needed to say where a character of it is set.</summary>
internal sealed class StyledLayout
{
    internal required ParagraphLayout Layout { get; init; }

    internal required IReadOnlyList<ResolvedStyle> Styles { get; init; }

    internal required IReadOnlyList<StyledSpan> Spans { get; init; }

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

/// <summary>Turns the runs of a block into one laid out paragraph, the way the flow and the public text measurer both do it.</summary>
internal static class BlockText
{
    /// <summary>U+2028 LINE SEPARATOR, a hard line break inside a paragraph.</summary>
    private const char LineSeparator = (char)0x2028;

    /// <summary>
    /// Lays out <paramref name="runs"/> as one paragraph in <paramref name="width"/> points (no limit when <see langword="null"/>). Every run is set in
    /// <paramref name="baseStyle"/> with the run's own style over it; a line break of any kind inside a run is one hard line break.
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
        StringBuilder text = new();
        List<ResolvedStyle> styles = [baseStyle];
        List<StyledSpan> spans = [];
        foreach (TextRun run in runs)
        {
            string chunk = LineBreaks(run.Text);
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
                spans.Add(new StyledSpan(text.Length, chunk.Length, index));
            }

            text.Append(chunk);
        }

        RunStyle[] runStyles = [.. styles.Select(s => new RunStyle
        {
            FontOrder = fonts.Order(s.Family, s.Weight, s.Italic),
            Size = s.Size,
            Language = s.Language,
            Color = s.Color,
        })];

        ParagraphLayout layout = ParagraphLayouter.Layout(
            text.ToString(),
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

    /// <summary>The text with every paragraph separator turned into U+2028, a line break inside the paragraph (a carriage return and a line feed together are one).</summary>
    private static string LineBreaks(string text)
    {
        if (!text.Any(c => c < 0x20 || c == 0x85 || c == 0x2029))
        {
            return text;
        }

        StringBuilder result = new(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                continue; // the line feed after it makes the break
            }

            result.Append(BidiData.ClassOf(c) == BidiClass.B ? LineSeparator : c);
        }

        return result.ToString();
    }
}
