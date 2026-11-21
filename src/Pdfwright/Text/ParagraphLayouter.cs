using Pdfwright.Fonts;
using Pdfwright.Unicode;

namespace Pdfwright.Text;

/// <summary>
/// Lays one paragraph out in lines: the paragraph is shaped once, as a whole (so that joining and context do not depend on where lines
/// break), the break opportunities of UAX #14 that fall on a glyph cluster boundary are filled greedily up to the width, each line gets the
/// bidi order of its own characters (UAX #9 rules L1 and L2) and is placed in the width by its alignment. The width a line is measured with
/// is the one it is drawn with, so <see cref="ParagraphMeasurer"/> and drawing cannot disagree. Because the paragraph is shaped once, kerning
/// across a place where a line breaks (a pair on either side of a hyphen) is kept, not undone at the edge of the line.
/// </summary>
internal static class ParagraphLayouter
{
    /// <summary>A width within this of the limit still fits: sums of advances in different orders differ in the last bits.</summary>
    private const double Tolerance = 1e-9;

    /// <summary>U+00AD SOFT HYPHEN marks where a word may be hyphenated; with no hyphenation in 0.1 it is invisible and never a place to break.</summary>
    private const int SoftHyphen = 0x00AD;

    /// <summary>Whether the boundary before code point <paramref name="index"/> touches a soft hyphen, on either side.</summary>
    private static bool IsSoftHyphenEdge(int[] codePoints, int index) => codePoints[index] == SoftHyphen || codePoints[index - 1] == SoftHyphen;

    /// <summary>
    /// Lays out <paramref name="text"/>, which is one paragraph (a paragraph separator may only be its last character: split text with
    /// <see cref="UnicodeBidi.Paragraphs"/> and lay the paragraphs out one after another). Hard line breaks inside the paragraph (U+2028,
    /// U+000B, U+000C) end a line; a line break at the very end of the text does not add an empty line, and an empty text is one empty line.
    /// A line with nothing visible (between two hard breaks) is as high as the nearest line that has content (the one before it, else the one after it, else the first font).
    /// A tab (U+0009) is laid out as one space (tab stops are for the block layer); the character itself stays in the text the glyphs are cut from.
    /// A soft hyphen (U+00AD) is never a place to break: nothing would draw the hyphen, and there is no hyphenation in 0.1, so it stays invisible inside its word.
    /// A unit wider than the width (a long word, Thai text without spaces) is cut between grapheme clusters, as much as fits on each line and
    /// never less than one glyph cluster, with no hyphen (CSS <c>overflow-wrap: anywhere</c>); one glyph cluster wider than the width stands alone and overflows.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The size, the width or the line spacing is not usable.</exception>
    internal static ParagraphLayout Layout(string text, IReadOnlyList<ShapingFont> fonts, ParagraphOptions options)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(fonts);
        ArgumentNullException.ThrowIfNull(options);
        if (!(options.Size > 0) || double.IsInfinity(options.Size))
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.Size, "The size must be a positive number.");
        }

        if (options.MaxWidth is { } maxWidth && (!(maxWidth >= 0) || double.IsInfinity(maxWidth)))
        {
            throw new ArgumentOutOfRangeException(nameof(options), maxWidth, "The width must be zero or a positive number, or null.");
        }

        if (!(options.LineSpacing > 0) || double.IsInfinity(options.LineSpacing))
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.LineSpacing, "The line spacing must be a positive number.");
        }

        // A tab has no glyph in a font: it is shaped, broken and measured as the space it is laid out as (the same length, so every index holds), while the text the glyphs are cut from for readers stays the tab, and the bidi algorithm reads the tab itself (a segment separator, rule L1).
        (IReadOnlyList<RunStyle> styles, ShapeStyling? styling) = Styling(text, fonts, options);
        ShapedParagraph paragraph = TextShaper.Shape(text.Replace('\t', ' '), fonts, options.ParagraphLevel, options.Language, text, styling);
        State state = new(text, paragraph, fonts, options, styles);
        List<Raw> raws = [];
        int start = 0;
        int candidate = 0;
        do
        {
            while (candidate < state.CandidateIndex.Count && state.CandidateIndex[candidate] <= start)
            {
                candidate++;
            }

            int end = state.Count == 0 ? 0 : state.FindLineEnd(start, candidate);

            // A line that does not advance would loop for ever: stop with an error (this check stays in Release builds).
            if (state.Count > 0 && (end <= start || end > state.Count))
            {
                throw new InvalidOperationException($"Line breaking did not advance: a line that starts at {start} was given the end {end} in a text of {state.Count} characters.");
            }

            raws.Add(state.BuildLine(start, end));
            start = end;
        }
        while (start < state.Count);

        // A line with nothing visible (an empty line between two hard breaks, a line of white space) is as high as the font of the nearest
        // line that has content (the one before it, else the one after it, else the first font), so that the empty lines of a paragraph in
        // one script do not take the height of another script's font.
        for (int i = 0; i < raws.Count; i++)
        {
            if (raws[i].FirstFont < 0)
            {
                Raw nearest = NearestContent(raws, i);
                (double ascent, double descent) = nearest.FirstFont >= 0 ? state.Metrics(nearest.FirstFont, nearest.FirstStyle) : state.Metrics(styles[0].FontOrder[0], 0);
                raws[i] = raws[i] with { Ascent = ascent, Descent = descent };
            }
        }

        // The box lines sit in: the width given, or the widest line's content, so that a paragraph without a width still has an edge to align to.
        double box = options.MaxWidth ?? raws.Max(r => r.Width);
        bool rightToLeft = (paragraph.ParagraphLevel & 1) == 1;
        List<LaidOutLine> lines = new(raws.Count);
        foreach (Raw raw in raws)
        {
            double offset = Offset(options.Alignment, rightToLeft, box, raw.Width);
            List<PlacedRun> runs = new(raw.Runs.Count);
            foreach (Placement placement in raw.Runs)
            {
                runs.Add(new PlacedRun(placement.Run, offset + (placement.X - raw.ContentLeft), placement.Width, placement.IsTrailingSpace));
            }

            lines.Add(new LaidOutLine
            {
                Start = raw.Start,
                Length = raw.Length,
                Width = raw.Width,
                X = offset,
                Ascent = raw.Ascent,
                Descent = raw.Descent,
                Pitch = (raw.Ascent + raw.Descent) * options.LineSpacing,
                Runs = runs.AsReadOnly(),
            });
        }

        return new ParagraphLayout(text, styles, lines.AsReadOnly(), paragraph.Missing.AsReadOnly(), fonts, paragraph.ParagraphLevel, options.Spans);
    }

    /// <summary>The styles of the paragraph and what the shaper needs to set it in them: with no styles in the options, one style of the options' size and language over every font.</summary>
    private static (IReadOnlyList<RunStyle> Styles, ShapeStyling? Styling) Styling(string text, IReadOnlyList<ShapingFont> fonts, ParagraphOptions options)
    {
        if (options.Styles is not { } styles)
        {
            return ([new RunStyle { FontOrder = [.. Enumerable.Range(0, fonts.Count)], Size = options.Size, Language = options.Language }], null);
        }

        if (styles.Count == 0)
        {
            throw new ArgumentException("Styles must not be empty.", nameof(options));
        }

        IReadOnlyList<StyledSpan> spans = options.Spans ?? [];
        if (spans.Count == 0 && text.Length > 0)
        {
            throw new ArgumentException("Styles need spans that tile the text.", nameof(options));
        }

        foreach (RunStyle style in styles)
        {
            if (!(style.Size > 0) || double.IsInfinity(style.Size))
            {
                throw new ArgumentOutOfRangeException(nameof(options), style.Size, "The size of a style must be a positive number.");
            }

            if (style.FontOrder.Length == 0 || style.FontOrder.Any(i => (uint)i >= (uint)fonts.Count))
            {
                throw new ArgumentException("The font order of a style is not empty and names fonts of the list.", nameof(options));
            }
        }

        int at = 0;
        foreach (StyledSpan span in spans)
        {
            if (span.Start != at || span.Length < 0 || (uint)span.Style >= (uint)styles.Count)
            {
                throw new ArgumentException("The spans must tile the text in order and name styles of the list.", nameof(options));
            }

            at += span.Length;
        }

        if (at != text.Length)
        {
            throw new ArgumentException($"The spans cover {at} characters and the text has {text.Length}.", nameof(options));
        }

        return (styles, new ShapeStyling { Spans = spans, FontOrders = [.. styles.Select(s => s.FontOrder)], Languages = [.. styles.Select(s => s.Language)], ShapeKeys = ShapeKeysOf(styles) });
    }

    /// <summary>The shaping group of each style: what shaping depends on is the font order, the size (the run's scale) and the language, and not the colour.</summary>
    private static int[] ShapeKeysOf(IReadOnlyList<RunStyle> styles)
    {
        Dictionary<(string Fonts, double Size, string? Language), int> groups = [];
        int[] keys = new int[styles.Count];
        for (int i = 0; i < styles.Count; i++)
        {
            (string, double, string?) key = (string.Join(',', styles[i].FontOrder), styles[i].Size, styles[i].Language);
            if (!groups.TryGetValue(key, out int group))
            {
                group = groups.Count;
                groups.Add(key, group);
            }

            keys[i] = group;
        }

        return keys;
    }

    /// <summary>The closest line to <paramref name="index"/> that has content (the one before, else the one after), or the line itself when there is none: the line whose font a line with nothing visible takes.</summary>
    private static Raw NearestContent(List<Raw> raws, int index)
    {
        for (int i = index - 1; i >= 0; i--)
        {
            if (raws[i].FirstFont >= 0)
            {
                return raws[i];
            }
        }

        for (int i = index + 1; i < raws.Count; i++)
        {
            if (raws[i].FirstFont >= 0)
            {
                return raws[i];
            }
        }

        return raws[index];
    }

    /// <summary>The left edge of a line's content in a box: logical alignment, with a line that is too wide aligned to the start.</summary>
    private static double Offset(TextAlignment alignment, bool rightToLeft, double box, double width)
    {
        double room = box - width;
        double atStart = rightToLeft ? room : 0;
        if (room < 0)
        {
            return atStart;
        }

        return alignment switch
        {
            TextAlignment.Center => room / 2,
            TextAlignment.End => rightToLeft ? 0 : room,
            _ => atStart,
        };
    }

    /// <summary>The order the clusters of a run's glyphs are listed in, which decides how the glyphs of a stretch of text are found.</summary>
    private enum GlyphOrder
    {
        Ascending,
        Descending,
        Unordered,
    }

    /// <summary>A line before alignment: its runs are at x positions measured from the left edge of the line's own sequence of runs.</summary>
    /// <remarks><paramref name="FirstFont"/> is the font of the first run of the content in logical order, or -1 for a line with nothing visible; <paramref name="FirstStyle"/> is that run's style.</remarks>
    private sealed record Raw(int Start, int Length, double Width, double ContentLeft, double Ascent, double Descent, int FirstFont, int FirstStyle, List<Placement> Runs);

    /// <summary>A run on a line while the line is being built: <see cref="X"/> is first its place in the row of runs and then, after alignment, its place from the paragraph's left edge.</summary>
    private sealed class Placement(ShapedRun run, double width, bool isTrailingSpace)
    {
        internal ShapedRun Run { get; } = run;

        internal double Width { get; } = width;

        internal bool IsTrailingSpace { get; } = isTrailingSpace;

        internal double X { get; set; }
    }

    /// <summary>What the line breaker and the line builder share for one paragraph.</summary>
    private sealed class State
    {
        private readonly ShapedParagraph _paragraph;
        private readonly IReadOnlyList<ShapingFont> _fonts;
        private readonly ParagraphOptions _options;
        private readonly IReadOnlyList<RunStyle> _styles;
        private readonly double[] _prefix;
        private readonly bool[] _boundary;
        private readonly int[] _codePointOf;
        private readonly List<bool> _mandatory = [];
        private readonly GlyphOrder[] _glyphOrder;
        private readonly bool[] _anywhere;

        internal State(string text, ShapedParagraph paragraph, IReadOnlyList<ShapingFont> fonts, ParagraphOptions options, IReadOnlyList<RunStyle> styles)
        {
            _paragraph = paragraph;
            _fonts = fonts;
            _options = options;
            _styles = styles;
            Count = text.Length;
            int[] utf16 = paragraph.Utf16Index;

            // The width of everything left of each UTF-16 index: a glyph's advance sits at its cluster, so the width of a text range is
            // the difference of two prefix sums, and a range that does not start and end on cluster boundaries is never asked for.
            double[] advanceAt = new double[Count + 1];
            _boundary = new bool[Count + 1];
            _boundary[0] = true;
            _boundary[Count] = true;
            _glyphOrder = new GlyphOrder[paragraph.Runs.Count];
            for (int r = 0; r < paragraph.Runs.Count; r++)
            {
                ShapedRun run = paragraph.Runs[r];
                _boundary[run.Utf16Start] = true;
                double scale = Scale(run);
                bool ascending = true;
                bool descending = true;
                for (int g = 0; g < run.Glyphs.Length; g++)
                {
                    ShapedGlyph glyph = run.Glyphs[g];
                    advanceAt[glyph.Cluster] += glyph.XAdvance * scale;
                    _boundary[glyph.Cluster] = true;
                    if (g > 0)
                    {
                        ascending &= run.Glyphs[g - 1].Cluster <= glyph.Cluster;
                        descending &= run.Glyphs[g - 1].Cluster >= glyph.Cluster;
                    }
                }

                _glyphOrder[r] = ascending ? GlyphOrder.Ascending : descending ? GlyphOrder.Descending : GlyphOrder.Unordered;
            }

            _prefix = new double[Count + 1];
            for (int i = 0; i < Count; i++)
            {
                _prefix[i + 1] = _prefix[i] + advanceAt[i];
            }

            // Only a position that starts a code point can be a boundary (a cluster value is one by construction; this is a guard).
            _codePointOf = new int[Count + 1];
            Array.Fill(_codePointOf, -1);
            for (int i = 0; i < paragraph.CodePoints.Length; i++)
            {
                _codePointOf[utf16[i]] = i;
            }

            _codePointOf[Count] = paragraph.CodePoints.Length;
            for (int u = 0; u <= Count; u++)
            {
                _boundary[u] &= _codePointOf[u] >= 0;
            }

            // Where a unit wider than the width may be cut: between grapheme clusters (UAX #29) that are also glyph clusters.
            bool[] graphemeBoundary = UnicodeGraphemeBreaker.Boundaries(paragraph.CodePoints);
            _anywhere = new bool[Count + 1];
            for (int i = 1; i < paragraph.CodePoints.Length; i++)
            {
                _anywhere[utf16[i]] = graphemeBoundary[i] && _boundary[utf16[i]] && !IsSoftHyphenEdge(paragraph.CodePoints, i);
            }

            // The places where a line may end: a break opportunity of UAX #14 that is also a grapheme and a glyph cluster boundary, so that no cluster is split
            // (a right-to-left run has a cluster for a mark of its own, so the glyph clusters alone no longer keep a mark with its base).
            LineBreakKind[] breaks = UnicodeLineBreaker.Breaks(paragraph.CodePoints);
            for (int i = 1; i < breaks.Length; i++)
            {
                if (breaks[i] != LineBreakKind.None && graphemeBoundary[i] && _boundary[utf16[i]] && (i == paragraph.CodePoints.Length || paragraph.CodePoints[i - 1] != SoftHyphen))
                {
                    CandidateIndex.Add(utf16[i]);
                    _mandatory.Add(breaks[i] == LineBreakKind.Mandatory);
                }
            }
        }

        /// <summary>The length of the text in UTF-16 units.</summary>
        internal int Count { get; }

        /// <summary>The UTF-16 index of each place a line may end, ascending.</summary>
        internal List<int> CandidateIndex { get; } = [];

        /// <summary>
        /// Where the line that starts at <paramref name="start"/> ends: at the last break opportunity before the content outgrows the width
        /// (the white space in front of the opportunity hangs and is not counted), at a hard line break, or, when even the first unit is too
        /// wide, inside that unit (see <see cref="BreakUnit"/>). <paramref name="firstCandidate"/> is the first candidate after <paramref name="start"/>.
        /// </summary>
        internal int FindLineEnd(int start, int firstCandidate)
        {
            int lastFit = -1;
            for (int j = firstCandidate; j < CandidateIndex.Count; j++)
            {
                int end = CandidateIndex[j];
                bool fits = _options.MaxWidth is not { } limit || _prefix[ContentEnd(start, end)] - _prefix[start] <= limit + Tolerance;
                if (!fits)
                {
                    return lastFit >= 0 ? lastFit : BreakUnit(start, end);
                }

                lastFit = end;
                if (_mandatory[j])
                {
                    return end;
                }
            }

            // No candidate is left: measure the rest like any other unit, so that a missing candidate never switches breaking off.
            bool restFits = _options.MaxWidth is not { } restLimit || _prefix[ContentEnd(start, Count)] - _prefix[start] <= restLimit + Tolerance;
            if (restFits)
            {
                return Count;
            }

            return lastFit >= 0 ? lastFit : BreakUnit(start, Count);
        }

        /// <summary>
        /// Where a line ends when the unit [<paramref name="start"/>, <paramref name="unitEnd"/>) alone is wider than the width (a long word, a
        /// number, Thai text without spaces): as much of it as fits, cut between grapheme clusters that are also glyph clusters (the behaviour of
        /// CSS <c>overflow-wrap: anywhere</c>; no hyphen is added), and at least one glyph cluster so that the line always advances. A unit that is
        /// one glyph cluster wide stays whole, with the white space after it, and overflows.
        /// </summary>
        private int BreakUnit(int start, int unitEnd)
        {
            int[] utf16 = _paragraph.Utf16Index;
            double limit = _options.MaxWidth ?? double.PositiveInfinity;
            int contentEndCp = _codePointOf[ContentEnd(start, unitEnd)];
            int first = -1;
            int best = -1;
            for (int i = _codePointOf[start] + 1; i < contentEndCp; i++)
            {
                int position = utf16[i];
                if (!_anywhere[position])
                {
                    continue;
                }

                first = first < 0 ? position : first;
                if (_prefix[position] - _prefix[start] > limit + Tolerance)
                {
                    break;
                }

                best = position;
            }

            return best >= 0 ? best : first >= 0 ? first : unitEnd;
        }

        /// <summary>
        /// Where the white space at the end of [<paramref name="start"/>, <paramref name="end"/>) begins (<paramref name="end"/> when there is
        /// none): the characters rule L1 returns to the paragraph level, but never from the middle of a cluster.
        /// </summary>
        internal int ContentEnd(int start, int end)
        {
            int[] utf16 = _paragraph.Utf16Index;
            int first = _codePointOf[start];
            int last = _codePointOf[end];
            int i = last;
            while (i > first && IsTrailingSpace(_paragraph.Bidi.Classes[i - 1]))
            {
                i--;
            }

            while (i < last && !_boundary[utf16[i]])
            {
                i++;
            }

            return utf16[i];
        }

        /// <summary>Lays out the line [<paramref name="start"/>, <paramref name="end"/>): its runs with their places in a row, before alignment.</summary>
        internal Raw BuildLine(int start, int end)
        {
            int[] utf16 = _paragraph.Utf16Index;
            int[] codePoints = _paragraph.CodePoints;
            int firstCp = _codePointOf[start];
            int endCp = _codePointOf[end];
            int contentEnd = ContentEnd(start, end);

            // The characters that end the line (U+000A, U+2028 ...) are not drawn: a font has no picture for them.
            int drawEndCp = endCp;
            while (drawEndCp > firstCp && IsLineTerminator(codePoints[drawEndCp - 1]))
            {
                drawEndCp--;
            }

            int drawEnd = utf16[drawEndCp];
            byte[] levels = UnicodeBidi.LineLevels(_paragraph.Bidi, firstCp, endCp);

            List<Placement> pieces = [];
            for (int runIndex = FirstRunEndingAfter(start); runIndex < _paragraph.Runs.Count; runIndex++)
            {
                ShapedRun run = _paragraph.Runs[runIndex];
                if (run.Utf16Start >= drawEnd)
                {
                    break;
                }

                int runEnd = run.Utf16Start + run.Utf16Length;
                int from = Math.Max(run.Utf16Start, start);
                int to = Math.Min(runEnd, drawEnd);
                if (from >= to)
                {
                    continue;
                }

                // A run is cut where the line's levels differ from the paragraph's (the white space at the end of the line) and at the white space itself.
                int cp = _codePointOf[from];
                int toCp = _codePointOf[to];
                while (cp < toCp)
                {
                    int groupStart = cp;
                    byte level = levels[cp - firstCp];
                    bool trailing = utf16[cp] >= contentEnd;
                    cp++;
                    while (cp < toCp && levels[cp - firstCp] == level && (utf16[cp] >= contentEnd) == trailing)
                    {
                        cp++;
                    }

                    Placement? piece = Piece(runIndex, run, runEnd, utf16[groupStart], utf16[cp], level, trailing);
                    if (piece is not null)
                    {
                        pieces.Add(piece);
                    }
                }
            }

            // Left to right: rule L2 over the pieces, which are stretches of one level, so reversing whole pieces is reversing their characters.
            byte[] pieceLevels = [.. pieces.Select(p => p.Run.Level)];
            int[] visual = UnicodeBidi.VisualOrder(pieceLevels, 0, pieceLevels.Length);
            double x = 0;
            foreach (int index in visual)
            {
                pieces[index].X = x;
                x += pieces[index].Width;
            }

            double width = 0;
            double trailingWidth = 0;
            double contentLeft = double.PositiveInfinity;
            double ascent = 0;
            double descent = 0;
            int firstFont = -1;
            int firstStyle = 0;
            foreach (Placement piece in pieces)
            {
                if (piece.IsTrailingSpace)
                {
                    trailingWidth += piece.Width;
                    continue;
                }

                width += piece.Width;
                contentLeft = Math.Min(contentLeft, piece.X);
                if (firstFont < 0)
                {
                    firstFont = piece.Run.FontIndex;
                    firstStyle = piece.Run.Style;
                }

                (double pieceAscent, double pieceDescent) = Metrics(piece.Run.FontIndex, piece.Run.Style);
                ascent = Math.Max(ascent, pieceAscent);
                descent = Math.Max(descent, pieceDescent);
            }

            if (double.IsPositiveInfinity(contentLeft))
            {
                // Nothing visible: the white space hangs on the side where the line would have ended (to the left in a right-to-left paragraph).
                // Its height is the paragraph's to give once every line is built (see ParagraphLayouter.Layout).
                contentLeft = (_paragraph.ParagraphLevel & 1) == 1 ? trailingWidth : 0;
            }

            return new Raw(start, end - start, width, contentLeft, ascent, descent, firstFont, firstStyle, pieces);
        }

        /// <summary>One stretch of <paramref name="run"/> on a line: the glyphs whose clusters lie in [from, to), in the order of the level they now have.</summary>
        private Placement? Piece(int runIndex, ShapedRun run, int runEnd, int from, int to, byte level, bool trailing)
        {
            ShapedGlyph[] glyphs;
            if (from == run.Utf16Start && to == runEnd && level == run.Level)
            {
                glyphs = run.Glyphs;
            }
            else
            {
                glyphs = GlyphsOfClusters(runIndex, run, from, to);

                // The glyphs are listed in the order of the run's direction; a piece of the other direction (white space that went back to the paragraph level) is listed in its own.
                if (((level ^ run.Level) & 1) == 1)
                {
                    Array.Reverse(glyphs);
                }
            }

            if (glyphs.Length == 0)
            {
                return null;
            }

            double scale = Scale(run);
            double advance = 0;
            foreach (ShapedGlyph glyph in glyphs)
            {
                advance += glyph.XAdvance * scale;
            }

            ShapedRun piece = glyphs == run.Glyphs
                ? run
                : new ShapedRun { Utf16Start = from, Utf16Length = to - from, Level = level, Script = run.Script, FontIndex = run.FontIndex, Style = run.Style, Glyphs = glyphs };
            return new Placement(piece, advance, trailing);
        }

        /// <summary>The index of the first run that ends after <paramref name="position"/> (runs are in logical order and tile the text): where a line that starts there begins to look.</summary>
        private int FirstRunEndingAfter(int position)
        {
            List<ShapedRun> runs = _paragraph.Runs;
            int low = 0;
            int high = runs.Count;
            while (low < high)
            {
                int middle = low + ((high - low) / 2);
                if (runs[middle].Utf16Start + runs[middle].Utf16Length > position)
                {
                    high = middle;
                }
                else
                {
                    low = middle + 1;
                }
            }

            return low;
        }

        /// <summary>
        /// The glyphs of <paramref name="run"/> whose cluster lies in [from, to), in the run's order. HarfBuzz lists clusters in one direction
        /// (up in a left-to-right run, down in a right-to-left one), so they are found by binary search, not by scanning the run for every line.
        /// </summary>
        private ShapedGlyph[] GlyphsOfClusters(int runIndex, ShapedRun run, int from, int to)
        {
            ShapedGlyph[] glyphs = run.Glyphs;
            switch (_glyphOrder[runIndex])
            {
                case GlyphOrder.Ascending:
                    return glyphs[FirstIndex(glyphs, g => g.Cluster >= from)..FirstIndex(glyphs, g => g.Cluster >= to)];
                case GlyphOrder.Descending:
                    return glyphs[FirstIndex(glyphs, g => g.Cluster < to)..FirstIndex(glyphs, g => g.Cluster < from)];
                default:
                    return [.. glyphs.Where(g => g.Cluster >= from && g.Cluster < to)];
            }
        }

        /// <summary>The first index of <paramref name="glyphs"/> where <paramref name="test"/> holds, given that it is false up to there and true after (or <c>glyphs.Length</c>).</summary>
        private static int FirstIndex(ShapedGlyph[] glyphs, Func<ShapedGlyph, bool> test)
        {
            int low = 0;
            int high = glyphs.Length;
            while (low < high)
            {
                int middle = low + ((high - low) / 2);
                if (test(glyphs[middle]))
                {
                    high = middle;
                }
                else
                {
                    low = middle + 1;
                }
            }

            return low;
        }

        /// <summary>The factor from font units to points of a run: its style's size over its font's units per em.</summary>
        private double Scale(ShapedRun run) => _styles[run.Style].Size / _fonts[run.FontIndex].Font.UnitsPerEm;

        /// <summary>The ascent and the descent of a font at the size of a style, in points, both positive.</summary>
        internal (double Ascent, double Descent) Metrics(int fontIndex, int style)
        {
            TrueTypeFont font = _fonts[fontIndex].Font;
            double scale = _styles[style].Size / font.UnitsPerEm;
            return (Math.Abs(font.Ascender) * scale, Math.Abs(font.Descender) * scale);
        }
    }

    /// <summary>The characters rule L1 treats as white space at the end of a line: white space, separators, isolate controls and what rule X9 removes.</summary>
    private static bool IsTrailingSpace(BidiClass type) =>
        type is BidiClass.WS or BidiClass.S or BidiClass.B or BidiClass.LRI or BidiClass.RLI or BidiClass.FSI or BidiClass.PDI
            || UnicodeBidi.IsRemovedByX9(type);

    /// <summary>The characters that end a line and have nothing to draw: line feed, vertical tab, form feed, carriage return, next line, line and paragraph separator.</summary>
    private static bool IsLineTerminator(int codePoint) => codePoint is >= 0x0A and <= 0x0D or 0x85 or 0x2028 or 0x2029;
}
