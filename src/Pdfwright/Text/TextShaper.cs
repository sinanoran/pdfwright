using Pdfwright.Fonts;
using Pdfwright.Unicode;

namespace Pdfwright.Text;

/// <summary>A font ready for shaping: the TrueType reader's view of it and the shaper's, over one copy of the font's bytes. The shaper's handle is opaque here (<see cref="IShaper"/>).</summary>
internal sealed class ShapingFont : IDisposable
{
    internal ShapingFont(TrueTypeFont font)
        : this(font, HarfBuzzShaper.Instance)
    {
    }

    internal ShapingFont(TrueTypeFont font, IShaper shaper)
    {
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(shaper);
        Font = font;
        Shaper = shaper;
        Handle = shaper.CreateFont(font.RawBytes, font.UnitsPerEm);
    }

    internal TrueTypeFont Font { get; }

    internal IShaper Shaper { get; }

    internal ShaperFont Handle { get; }

    /// <summary>
    /// The most runs the cache of shaped text holds; when it is full it is emptied (the text of a document that repeats is mostly its first few thousand different runs), so the memory stays bounded.
    /// Worst case, about 11 MB for each font (2,048 runs of 256 characters: the cached string, about 0.5 KB, and its glyphs, about 5 KB). The cache lives as long as the
    /// <c>FontCollection</c> that owns the font and keeps the strings it holds alive. The length cap of 256 characters stays: table cells and labels are short, and a lower cap would save memory only on long texts.
    /// </summary>
    private const int CacheCapacity = 2048;

    /// <summary>The longest run that is cached: a long paragraph is rarely repeated and costs more to hold.</summary>
    private const int CacheTextLength = 256;

    private readonly Dictionary<(string Text, bool RightToLeft, string Script, string? Language), ShapedGlyph[]> _cache = [];

    /// <summary>The glyphs of a run shaped earlier (a whole text, shaped with no context), or <see langword="null"/>. A font is shared by every document and thread of its collection, so the cache is locked; the arrays are never changed after they are made.</summary>
    internal ShapedGlyph[]? GetCached(string text, bool rightToLeft, string script, string? language)
    {
        if (text.Length > CacheTextLength)
        {
            return null;
        }

        lock (_cache)
        {
            return _cache.TryGetValue((text, rightToLeft, script, language), out ShapedGlyph[]? glyphs) ? glyphs : null;
        }
    }

    internal void Cache(string text, bool rightToLeft, string script, string? language, ShapedGlyph[] glyphs)
    {
        if (text.Length > CacheTextLength)
        {
            return;
        }

        lock (_cache)
        {
            if (_cache.Count >= CacheCapacity)
            {
                _cache.Clear();
            }

            _cache[(text, rightToLeft, script, language)] = glyphs;
        }
    }

    /// <summary>A hook for tests: when set, it replaces the verdict of the shaper on whether the font may be shaped word by word (to shape a font that passes as if it failed). It is for tests only and is read on every call.</summary>
    internal bool? WordShapingOverride { get; set; }

    /// <summary>Whether a run of simple left-to-right text in this font may be shaped word by word: the shaper's check of the font (made once, on the first ask), unless a test has overridden it.</summary>
    internal bool CanShapeWordByWord => WordShapingOverride ?? Handle.CanShapeWordByWord;

    /// <summary>The most entries the word cache holds; when it is full it is emptied, as the cache of whole texts is, so the memory stays bounded: 4,096 words of at most 64 characters are about 1.5 MB for each font.</summary>
    internal const int WordCacheCapacity = 4096;

    /// <summary>The longest word (or stretch of spaces) that is cached; a longer one is shaped every time, which is rare in running text, and costs more to hold than it saves.</summary>
    internal const int WordCacheLength = 64;

    /// <summary>A word as it was shaped on its own: its glyphs (clusters from 0) and whether every one is a base glyph, the condition for using it in word-by-word shaping.</summary>
    internal sealed record ShapedWord(ShapedGlyph[] Glyphs, bool AllBase);

    // The words of one script and language, so that a lookup by the characters of a word needs no key that holds them with the script and the language: on .NET 9 and later the inner dictionary is read with a span of the text.
    private readonly Dictionary<(string Script, string? Language), Dictionary<string, ShapedWord>> _words = [];
    private int _wordCount;

    /// <summary>How many words the word cache holds now. For tests.</summary>
    internal int WordCacheCount
    {
        get
        {
            lock (_words)
            {
                return _wordCount;
            }
        }
    }

    /// <summary>The word shaped earlier for <c>text[start .. start + length]</c> (left to right, with no context), or <see langword="null"/>. Locked like <see cref="GetCached"/>; the arrays are never changed after they are made.</summary>
    internal ShapedWord? GetWord(string text, int start, int length, string script, string? language)
    {
        if (length > WordCacheLength)
        {
            return null;
        }

        lock (_words)
        {
            if (!_words.TryGetValue((script, language), out Dictionary<string, ShapedWord>? words))
            {
                return null;
            }

#if NET9_0_OR_GREATER
            return words.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(text.AsSpan(start, length), out ShapedWord? word) ? word : null;
#else
            return words.TryGetValue(text.Substring(start, length), out ShapedWord? word) ? word : null;
#endif
        }
    }

    /// <summary>Keeps a word for <see cref="GetWord"/>; one longer than <see cref="WordCacheLength"/> is not kept.</summary>
    internal void CacheWord(string word, string script, string? language, ShapedWord shaped)
    {
        if (word.Length > WordCacheLength)
        {
            return;
        }

        lock (_words)
        {
            if (_wordCount >= WordCacheCapacity)
            {
                _words.Clear();
                _wordCount = 0;
            }

            if (!_words.TryGetValue((script, language), out Dictionary<string, ShapedWord>? words))
            {
                words = [];
                _words[(script, language)] = words;
            }

            if (words.TryAdd(word, shaped))
            {
                _wordCount++;
            }
        }
    }

    public void Dispose() => Handle.Dispose();
}

/// <summary>One glyph as the shaper placed it, in font units.</summary>
internal readonly record struct ShapedGlyph(int Glyph, int Cluster, int XAdvance, int XOffset, int YOffset);

/// <summary>
/// A stretch of text that is shaped as one: one font, one script, one direction (one bidi level), one font order, size and language. <see cref="Glyphs"/> are in the order they
/// are drawn, left to right, which for a right-to-left run is the reverse of the logical order; each glyph's <see cref="ShapedGlyph.Cluster"/>
/// is the UTF-16 index in the paragraph of the characters it stands for.
/// </summary>
internal sealed class ShapedRun
{
    internal required int Utf16Start { get; init; }

    internal required int Utf16Length { get; init; }

    internal required byte Level { get; init; }

    internal required int Script { get; init; }

    internal required int FontIndex { get; init; }

    /// <summary>The index of the style of the run's first character (see <see cref="ShapeStyling"/>); 0 for a paragraph with one style. Later characters may be in other styles of the same shaping group (they differ in colour only), which a drawing looks up by cluster.</summary>
    internal int Style { get; init; }

    internal required ShapedGlyph[] Glyphs { get; init; }

    internal bool IsRightToLeft => (Level & 1) == 1;

    /// <summary>The advance width of the whole run in font units.</summary>
    internal int Width => Glyphs.Sum(g => g.XAdvance);
}

/// <summary>The runs of one paragraph in logical order, with the paragraph's level.</summary>
internal sealed class ShapedParagraph
{
    internal required byte ParagraphLevel { get; init; }

    internal required List<ShapedRun> Runs { get; init; }

    /// <summary>
    /// The characters no font had a glyph for (as UTF-16 indices): they are drawn as the .notdef glyph of the font chosen for them. Characters that
    /// need no glyph (joiners, direction marks, variation selectors, control characters) are never listed.
    /// </summary>
    internal required List<int> Missing { get; init; }

    /// <summary>The code points of the text, as the algorithms of <see cref="UnicodeText"/> take them.</summary>
    internal required int[] CodePoints { get; init; }

    /// <summary>For each code point the UTF-16 index where it starts, and <c>text.Length</c> after the last (see <see cref="UnicodeText.CodePoints"/>).</summary>
    internal required int[] Utf16Index { get; init; }

    /// <summary>
    /// The bidi result the runs' levels came from, kept so that a line breaker can apply rule L1 to each line with <see cref="UnicodeBidi.LineLevels"/>:
    /// a run's own level has L1 applied to the paragraph as one line, which is wrong for the white space at the end of every line but the last.
    /// </summary>
    internal required BidiResult Bidi { get; init; }
}

/// <summary>
/// Turns a paragraph of text into shaped runs: bidi levels (UAX #9), script runs (UAX #24), a font for every character from a fallback list,
/// and HarfBuzz shaping of each run with the text around it as context. Drawing and line breaking are done by the layers above.
/// </summary>
internal static class TextShaper
{
    /// <summary>
    /// Shapes <paramref name="text"/> as one paragraph (it must not hold a paragraph separator before its last character: split with
    /// <see cref="UnicodeBidi.Paragraphs"/> first, or <see cref="ArgumentException"/> is thrown; tabs and other control characters are for the
    /// caller to turn into spaces). <paramref name="fonts"/> is the fallback list in order of preference. No language is set unless
    /// <paramref name="language"/> is given (then it applies to every run); the beginning and end of text flags of HarfBuzz are not set, so
    /// the text is taken as it is written and a combining mark at its start gets no dotted circle. <paramref name="bidiText"/>, when given, is a text of the
    /// same code points in number that the bidi algorithm reads instead of <paramref name="text"/> (a tab shaped as a space still resolves as a tab: rule L1 needs the segment separator).
    /// <paramref name="styling"/>, when given, sets stretches of the text in different styles: each has its own order of fonts, size and language, and a run never spans two styles that are shaped differently (a change of colour alone does not end a run).
    /// </summary>
    internal static ShapedParagraph Shape(string text, IReadOnlyList<ShapingFont> fonts, int paragraphLevel = UnicodeBidi.AutoLevel, string? language = null, string? bidiText = null, ShapeStyling? styling = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(fonts);
        if (fonts.Count == 0)
        {
            throw new ArgumentException("At least one font is needed.", nameof(fonts));
        }

        int[] codePoints = UnicodeText.CodePoints(text, out int[] utf16Index);
        bool simple = FastPaths && codePoints.Length > 0 && IsSimpleLeftToRight(text, bidiText, paragraphLevel);
        BidiResult bidi = simple ? SimpleBidi(text, bidiText) : UnicodeBidi.Resolve(bidiText is null ? codePoints : BidiCodePoints(bidiText, codePoints.Length), paragraphLevel);
        if (codePoints.Length == 0)
        {
            return new ShapedParagraph { ParagraphLevel = bidi.ParagraphLevel, Runs = [], Missing = [], CodePoints = codePoints, Utf16Index = utf16Index, Bidi = bidi };
        }

        // The text of the simple path is one script: Latin when it has a letter, else Common (the itemizer's answer for it, which the tests hold the path to).
        int[] scriptOf = new int[codePoints.Length];
        if (simple)
        {
            int only = ScriptData.Common;
            foreach (char c in text)
            {
                if (c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z'))
                {
                    only = ScriptData.IdOf("Latn");
                    break;
                }
            }

            Array.Fill(scriptOf, only);
        }
        else
        {
            foreach ((int start, int length, int script) in ScriptItemizer.Itemize(codePoints))
            {
                Array.Fill(scriptOf, script, start, length);
            }
        }

        List<int> missing = [];
        int[] styleOf = StyleOfCodePoints(codePoints.Length, utf16Index, text.Length, styling);
        int[] fontOf = ChooseFonts(codePoints, utf16Index, fonts, styling, styleOf, missing, simple);

        // A run ends where the level, the script, the font or what shaping depends on in the style (the font order, the size, the language) changes: a change of colour alone does not end it, so that a word that is partly coloured is still shaped as one.
        List<ShapedRun> runs = [];
        int runStart = 0;
        for (int i = 1; i <= codePoints.Length; i++)
        {
            if (i < codePoints.Length && bidi.Levels[i] == bidi.Levels[runStart] && scriptOf[i] == scriptOf[runStart] && fontOf[i] == fontOf[runStart] && KeyOf(styling, styleOf[i]) == KeyOf(styling, styleOf[runStart]))
            {
                continue;
            }

            string? runLanguage = styling is null ? language : styling.Languages[styleOf[runStart]];
            runs.Add(ShapeRun(text, utf16Index[runStart], utf16Index[i] - utf16Index[runStart], bidi.Levels[runStart], scriptOf[runStart], fontOf[runStart], fonts[fontOf[runStart]], runLanguage, styleOf[runStart], simple));
            runStart = i;
        }

        return new ShapedParagraph { ParagraphLevel = bidi.ParagraphLevel, Runs = runs, Missing = missing, CodePoints = codePoints, Utf16Index = utf16Index, Bidi = bidi };
    }

    /// <summary>
    /// Characters that need no glyph of their own: HarfBuzz hides the default-ignorable ones (joiners, direction marks, variation selectors)
    /// whatever the font's character map says, and control characters (tab, line feed) are for the layer above to turn into space or breaks.
    /// They take the font of their neighbours and are never reported as missing.
    /// </summary>
    internal static bool NeedsNoGlyph(int codePoint) =>
        codePoint is < 0x20 or (>= 0x7F and <= 0x9F)
            or 0x00AD or 0x034F or 0x061C or 0x115F or 0x1160 or (>= 0x17B4 and <= 0x17B5) or (>= 0x180B and <= 0x180F)
            or (>= 0x200B and <= 0x200F) or (>= 0x202A and <= 0x202E) or (>= 0x2060 and <= 0x206F) or 0x3164
            or (>= 0xFE00 and <= 0xFE0F) or 0xFEFF or 0xFFA0 or (>= 0xFFF0 and <= 0xFFF8) or (>= 0xE0000 and <= 0xE0FFF);

    private static int[] BidiCodePoints(string bidiText, int count)
    {
        int[] codePoints = UnicodeText.CodePoints(bidiText, out _);
        if (codePoints.Length != count)
        {
            throw new ArgumentException("The text for the bidi algorithm must have as many code points as the text that is shaped.", nameof(bidiText));
        }

        return codePoints;
    }

    /// <summary>The shaping group of a style: styles in one group are shaped alike, so a run may span them.</summary>
    private static int KeyOf(ShapeStyling? styling, int style) => styling is null ? 0 : styling.ShapeKeys[style];

    /// <summary>The style of every code point: that of the span that holds it, or 0 with no styling.</summary>
    private static int[] StyleOfCodePoints(int count, int[] utf16Index, int textLength, ShapeStyling? styling)
    {
        int[] styleOf = new int[count];
        if (styling is null)
        {
            return styleOf;
        }

        int span = 0;
        for (int i = 0; i < count; i++)
        {
            while (span < styling.Spans.Count - 1 && utf16Index[i] >= styling.Spans[span].Start + styling.Spans[span].Length)
            {
                span++;
            }

            StyledSpan s = styling.Spans[span];
            if (utf16Index[i] < s.Start || utf16Index[i] >= s.Start + s.Length)
            {
                throw new ArgumentException($"The spans do not tile the text of {textLength} characters: nothing covers index {utf16Index[i]}.", nameof(styling));
            }

            styleOf[i] = s.Style;
        }

        return styleOf;
    }

    /// <summary>
    /// A font for every character, chosen per grapheme cluster so that a base and its marks are never split between fonts (nor between styles:
    /// a cluster is set in the style of its first character, which <paramref name="styleOf"/> is changed to say). A cluster of a real
    /// script takes the first font in its style's order that has a glyph for all of it (so the order is the order of preference); spaces,
    /// digits and punctuation, which belong to no script, stay in the font of the text before them when it covers them and the style is the same, so that a run is not
    /// cut at every space. When no font covers a whole cluster, the first font that covers its first character is used.
    /// </summary>
    private static int[] ChooseFonts(int[] codePoints, int[] utf16Index, IReadOnlyList<ShapingFont> fonts, ShapeStyling? styling, int[] styleOf, List<int> missing, bool everyCharacterIsACluster)
    {
        bool[]? boundaries = everyCharacterIsACluster ? null : UnicodeGraphemeBreaker.Boundaries(codePoints);
        int[] chosen = new int[codePoints.Length];
        int previous = styling is null ? 0 : styling.FontOrders[styleOf[0]][0];
        int previousStyle = styleOf[0];
        for (int start = 0; start < codePoints.Length;)
        {
            int end = start + 1;
            while (boundaries is not null && end < codePoints.Length && !boundaries[end])
            {
                end++;
            }

            int style = styleOf[start];
            int[]? order = styling?.FontOrders[style];
            int pick = PickFont(codePoints, start, end, KeyOf(styling, style) == KeyOf(styling, previousStyle) ? previous : -1, fonts, order, out bool anyMissing);
            for (int i = start; i < end; i++)
            {
                chosen[i] = pick;
                styleOf[i] = style;
                if (anyMissing && !NeedsNoGlyph(codePoints[i]) && fonts[pick].Font.GlyphForCodePoint(codePoints[i]) == 0)
                {
                    missing.Add(utf16Index[i]);
                }
            }

            previous = pick;
            previousStyle = style;
            start = end;
        }

        return chosen;
    }

    /// <summary>Picks the font for the cluster [start, end). <c>previous</c> is the font of the cluster before when it has the same style, and -1 when the style changed (a neutral character then takes the first font of the order that covers it).</summary>
    private static int PickFont(int[] codePoints, int start, int end, int previous, IReadOnlyList<ShapingFont> fonts, int[]? order, out bool anyMissing)
    {
        anyMissing = false;
        int fallback = previous >= 0 ? previous : (order is null ? 0 : order[0]);
        int first = -1;
        for (int i = start; i < end; i++)
        {
            if (!NeedsNoGlyph(codePoints[i]))
            {
                first = i;
                break;
            }
        }

        if (first < 0)
        {
            return fallback; // only characters that need no glyph
        }

        bool neutral = ScriptData.ScriptOf(codePoints[first]) == ScriptData.Common;
        if (neutral && previous >= 0 && Covers(fonts[previous], codePoints, start, end))
        {
            return previous;
        }

        int count = order?.Length ?? fonts.Count;
        for (int k = 0; k < count; k++)
        {
            int f = order is null ? k : order[k];
            if (Covers(fonts[f], codePoints, start, end))
            {
                return f;
            }
        }

        for (int k = 0; k < count; k++)
        {
            int f = order is null ? k : order[k];
            if (fonts[f].Font.GlyphForCodePoint(codePoints[first]) != 0)
            {
                anyMissing = true; // the marks after the base are drawn without their glyphs
                return f;
            }
        }

        anyMissing = true;
        return fallback;
    }

    private static bool Covers(ShapingFont font, int[] codePoints, int start, int end)
    {
        for (int i = start; i < end; i++)
        {
            if (!NeedsNoGlyph(codePoints[i]) && font.Font.GlyphForCodePoint(codePoints[i]) == 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether the faster paths are used (they give the same result as the general ones, which the tests hold them to by running both): the simple left-to-right path of <see cref="Shape"/> and the cache of shaped runs.
    /// An internal switch for tests; it is read at the start of every call.
    /// </summary>
    internal static volatile bool FastPaths = true;

    /// <summary>
    /// Whether the text can skip bidi resolution, script itemization and grapheme cluster breaking: only printable ASCII (a tab in <paramref name="bidiText"/> where <paramref name="text"/> has the space it is shaped as), so every
    /// character is one cluster and one level, and the paragraph does not start right to left. Digits, which the bidi rules would make level 2 after a right-to-left letter, cannot, there being none.
    /// </summary>
    private static bool IsSimpleLeftToRight(string text, string? bidiText, int paragraphLevel)
    {
        // Only level 0 and automatic can take the fast path; any other level goes to the general code, which refuses a level that is not 0, 1 or automatic.
        if (paragraphLevel is not (0 or UnicodeBidi.AutoLevel))
        {
            return false;
        }

        foreach (char c in text)
        {
            if (c is < ' ' or > '~')
            {
                return false;
            }
        }

        if (bidiText is null)
        {
            return true;
        }

        if (bidiText.Length != text.Length)
        {
            return false;
        }

        for (int i = 0; i < bidiText.Length; i++)
        {
            if (bidiText[i] != text[i] && !(bidiText[i] == '\t' && text[i] == ' '))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The bidi result of text that <see cref="IsSimpleLeftToRight"/> accepted: paragraph level 0 and every level 0, with the classes of the characters.</summary>
    private static BidiResult SimpleBidi(string text, string? bidiText)
    {
        string source = bidiText ?? text;
        BidiClass[] classes = new BidiClass[source.Length];
        for (int i = 0; i < classes.Length; i++)
        {
            classes[i] = BidiData.ClassOf(source[i]);
        }

        return new BidiResult(0, new byte[source.Length], classes);
    }

    private static ShapedRun ShapeRun(string text, int utf16Start, int utf16Length, byte level, int script, int fontIndex, ShapingFont font, string? language, int style, bool simple)
    {
        bool rightToLeft = (level & 1) == 1;
        string scriptTag = ScriptData.CodeOf(script);

        // Only a run that is the whole text is cached: it is shaped with no context, so its glyphs depend on nothing but the key.
        bool whole = FastPaths && utf16Start == 0 && utf16Length == text.Length;
        ShapedGlyph[]? glyphs = whole ? font.GetCached(text, rightToLeft, scriptTag, language) : null;
        if (glyphs is null)
        {
            // Words are shaped one by one only for left-to-right text of the simple path (printable ASCII, one script) in a font whose rules never involve the space glyph; every other run is shaped as a whole.
            // A whole text that is a single word gains nothing from it (the cache of whole texts already holds it), so it is shaped as it is.
            if (FastPaths && simple && !rightToLeft && (!whole || text.AsSpan().IndexOf(' ') >= 0) && font.CanShapeWordByWord)
            {
                glyphs = ShapeByWords(text, utf16Start, utf16Length, scriptTag, language, font);
            }

            glyphs ??= font.Shaper.Shape(font.Handle, text, utf16Start, utf16Length, rightToLeft, scriptTag, language);
            if (whole)
            {
                font.Cache(text, rightToLeft, scriptTag, language, glyphs);
            }
        }

        return new ShapedRun { Utf16Start = utf16Start, Utf16Length = utf16Length, Level = level, Script = script, FontIndex = fontIndex, Style = style, Glyphs = glyphs };
    }

    /// <summary>
    /// Shapes a run word by word: the maximal stretches of characters other than the space and the maximal stretches of spaces are each shaped on their own (no context, the run's script and language) and kept in the font's word cache,
    /// and their glyphs are joined with the clusters moved to where the segment lies in <paramref name="text"/>. For a font that passed the check for the space glyph (see <see cref="ShaperFont.WordShapingRefusal"/>) this gives the
    /// glyphs of shaping the run whole, which is how Chromium shapes and caches words; the tests hold it to that. Returns <see langword="null"/>, so that the caller shapes the run whole, when a segment has a glyph that is
    /// not a base glyph (a lookup that skips base glyphs could match across the space, which the check of the font cannot see).
    /// </summary>
    private static ShapedGlyph[]? ShapeByWords(string text, int start, int length, string scriptTag, string? language, ShapingFont font)
    {
        // Latin text has about one glyph for each character; the array grows if a ligature font makes more and is cut to size at the end.
        ShapedGlyph[] result = new ShapedGlyph[length];
        int count = 0;
        int end = start + length;
        for (int segment = start; segment < end;)
        {
            bool spaces = text[segment] == ' ';
            int segmentEnd = segment + 1;
            while (segmentEnd < end && (text[segmentEnd] == ' ') == spaces)
            {
                segmentEnd++;
            }

            int segmentLength = segmentEnd - segment;
            ShapingFont.ShapedWord? word = font.GetWord(text, segment, segmentLength, scriptTag, language);
            if (word is null)
            {
                string piece = text.Substring(segment, segmentLength);
                ShapedGlyph[] shaped = font.Shaper.Shape(font.Handle, piece, 0, segmentLength, false, scriptTag, language);
                bool allBase = true;
                foreach (ShapedGlyph g in shaped)
                {
                    if (!font.Handle.IsBaseGlyph(g.Glyph))
                    {
                        allBase = false;
                        break;
                    }
                }

                word = new ShapingFont.ShapedWord(shaped, allBase);
                font.CacheWord(piece, scriptTag, language, word);
            }

            if (!word.AllBase)
            {
                return null;
            }

            ShapedGlyph[] glyphs = word.Glyphs;
            if (count + glyphs.Length > result.Length)
            {
                Array.Resize(ref result, Math.Max(count + glyphs.Length, result.Length * 2));
            }

            for (int i = 0; i < glyphs.Length; i++)
            {
                result[count++] = glyphs[i] with { Cluster = glyphs[i].Cluster + segment };
            }

            segment = segmentEnd;
        }

        if (count != result.Length)
        {
            Array.Resize(ref result, count);
        }

        return result;
    }
}

/// <summary>
/// How a paragraph that is set in more than one style is shaped: the stretches (they tile the text), and for each style the order of fonts
/// that may draw it (indices into the font list) and the language that selects its shaping features.
/// </summary>
internal sealed class ShapeStyling
{
    /// <summary>The stretches of the text, in order, that tile it (UTF-16 indices).</summary>
    internal required IReadOnlyList<StyledSpan> Spans { get; init; }

    /// <summary>For each style, the fonts that may draw it, in order of preference; never empty.</summary>
    internal required IReadOnlyList<int[]> FontOrders { get; init; }

    /// <summary>For each style, the shaping group it belongs to: styles with the same font order, size and language have the same number, and a run may span them.</summary>
    internal required IReadOnlyList<int> ShapeKeys { get; init; }

    /// <summary>For each style, its BCP 47 language or <see langword="null"/>.</summary>
    internal required IReadOnlyList<string?> Languages { get; init; }
}
