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

    /// <summary>
    /// The most entries the word cache holds; when it is full it is emptied, as the cache of whole texts is, so the memory stays bounded. A word of the full 64 characters costs about 1.6 KB (the string, about 150 bytes, its
    /// 64 glyphs of 20 bytes, and the entry), so the worst case is about 7 MB for each font, on top of the cache of whole texts; the words of real text are much shorter (about 10 characters, 300 bytes: about 1.2 MB). The bound is worth it:
    /// without it a program that shapes unending different words (identifiers, numbers) would grow without limit.
    /// </summary>
    internal const int WordCacheCapacity = 4096;

    /// <summary>The longest word (or stretch of spaces) that is cached; a longer one is shaped every time, which is rare in running text, and costs more to hold than it saves.</summary>
    internal const int WordCacheLength = 64;

    /// <summary>The most tables of words (scripts and languages) a font keeps.</summary>
    internal const int WordTableLimit = 64;

    /// <summary>A word as it was shaped on its own: its glyphs (clusters from 0) and whether every one is a base glyph, the condition for using it in word-by-word shaping. Never changed after it is made.</summary>
    internal sealed record ShapedWord(ShapedGlyph[] Glyphs, bool AllBase);

    // One table of words for each script and language. The tables are found without a lock and never removed (a clear empties them), so a caller may keep the table it got for the whole run.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string Script, string? Language), WordTable> _tables = new();
    private int _wordCount;

    /// <summary>How many tables of words (scripts and languages) the font holds now. For tests.</summary>
    internal int WordTableCount => _tables.Count;

    /// <summary>How many words the word cache holds now (counted, not the approximate count that bounds it). For tests.</summary>
    internal int WordCacheCount => _tables.Values.Sum(t => t.Count);

    /// <summary>Empties the cache of whole texts, which is how a test makes a text reach the word cache again. For tests.</summary>
    internal void ClearWholeTextCache()
    {
        lock (_cache)
        {
            _cache.Clear();
        }
    }

    /// <summary>The words of one script and language. A font is shared by every thread and document of its collection and a hit is the common case (one for each word of every cell), so a lookup takes no lock.</summary>
    internal WordTable GetWordTable(string script, string? language)
    {
        (string, string?) key = (script, language);
        if (_tables.TryGetValue(key, out WordTable? table))
        {
            return table;
        }

        // The number of languages is bounded too (a program that sets a different language tag for every text would otherwise make a table for each): past the limit new languages are shaped without a cache.
        return _tables.Count >= WordTableLimit ? new WordTable(this, kept: false) : _tables.GetOrAdd(key, _ => new WordTable(this, kept: true));
    }

    /// <summary>
    /// The words of one script and language in a font, read without a lock (a <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey, TValue}"/>, read with a span of the text on .NET 9 and later). Two threads that shape the same
    /// new word both add it and one add wins; both made the same glyphs from the same text, and a word is never changed, so it does not matter which stays. The bound is kept by an approximate count of the words added by all the tables of the font:
    /// when it passes the capacity every table is dropped, and threads racing with the clear may add a few words more or lose a few that were just added, which only costs a second shaping of them and keeps the memory within the capacity plus a few words for each thread.
    /// </summary>
    internal sealed class WordTable
    {
        private readonly ShapingFont _owner;
        private readonly bool _kept;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ShapedWord> _words = new(StringComparer.Ordinal);
#if NET9_0_OR_GREATER
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ShapedWord>.AlternateLookup<ReadOnlySpan<char>> _lookup;
#endif

        internal WordTable(ShapingFont owner, bool kept)
        {
            _owner = owner;
            _kept = kept;
#if NET9_0_OR_GREATER
            _lookup = _words.GetAlternateLookup<ReadOnlySpan<char>>();
#endif
        }

        internal int Count => _words.Count;

        /// <summary>The word shaped earlier for <c>text[start .. start + length]</c> (left to right, with no context), or <see langword="null"/>.</summary>
        internal ShapedWord? Get(string text, int start, int length)
        {
            if (!_kept || length > WordCacheLength)
            {
                return null;
            }

#if NET9_0_OR_GREATER
            return _lookup.TryGetValue(text.AsSpan(start, length), out ShapedWord? word) ? word : null;
#else
            return _words.TryGetValue(text.Substring(start, length), out ShapedWord? word) ? word : null;
#endif
        }

        /// <summary>Keeps a word for <see cref="Get"/>; one longer than <see cref="WordCacheLength"/> is not kept.</summary>
        internal void Add(string word, ShapedWord shaped)
        {
            if (!_kept || word.Length > WordCacheLength || !_words.TryAdd(word, shaped))
            {
                return;
            }

            if (Interlocked.Increment(ref _owner._wordCount) > WordCacheCapacity)
            {
                // The tables themselves go too, not only their words, so that the languages of old texts do not stay; a thread that holds a table finishes its run with it, and what it adds to it is lost with it.
                _owner._tables.Clear();

                Interlocked.Exchange(ref _owner._wordCount, 0);
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

    private int? _codePointCount;

    /// <summary>
    /// The number of code points of the text. The arrays above are as long as this unless the paragraph was shaped with <c>pooled</c> (see <see cref="TextShaper.Shape"/>), when they come from the array pool,
    /// may be longer and hold anything past this count: a reader must use the count, never the length of an array.
    /// </summary>
    internal int CodePointCount
    {
        get => _codePointCount ?? CodePoints.Length;
        init => _codePointCount = value;
    }

    /// <summary>Whether the arrays of the code points and of the bidi result were rented from <see cref="ScratchArray"/> and must be given back with <see cref="Release"/>.</summary>
    internal bool Pooled { get; init; }

    private bool _released;

    /// <summary>
    /// Gives the rented arrays back (a paragraph that is not <see cref="Pooled"/> has nothing to give back). The paragraph's code points, indices and bidi result must not be read afterwards, nor may anything hold them: the runs, the glyphs and the list of missing
    /// characters are not rented and stay valid. Calling it twice does no harm.
    /// </summary>
    internal void Release()
    {
        if (!Pooled || _released)
        {
            return;
        }

        _released = true;
        ScratchArray.Return(CodePoints);
        ScratchArray.Return(Utf16Index);
        ScratchArray.Return(Bidi.Levels);
        ScratchArray.Return(Bidi.Classes);
    }
}

/// <summary>
/// Turns a paragraph of text into shaped runs: bidi levels (UAX #9), script runs (UAX #24), a font for every character from a fallback list,
/// and shaping of each run by the shaping engine with the text around it as context, except that a run of the simple left-to-right path in a font that passes the check for the space glyph is shaped word by word with no context, which for printable ASCII gives the same glyphs (the tests hold it to that). Drawing and line breaking are done by the layers above.
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
    /// <paramref name="pooled"/> lets the simple path rent the arrays the paragraph keeps (its code points, their indices and the bidi result) from <see cref="ScratchArray"/>: the caller then owns the paragraph and must call
    /// <see cref="ShapedParagraph.Release"/> once it is done with them, after which nothing may read them. The runs and their glyphs are never rented, since they outlive the paragraph in the lines.
    /// </summary>
    internal static ShapedParagraph Shape(string text, IReadOnlyList<ShapingFont> fonts, int paragraphLevel = UnicodeBidi.AutoLevel, string? language = null, string? bidiText = null, ShapeStyling? styling = null, bool pooled = false)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(fonts);
        if (fonts.Count == 0)
        {
            throw new ArgumentException("At least one font is needed.", nameof(fonts));
        }

        // Every character of printable ASCII is one code point, one cluster and one UTF-16 unit, which is why the simple path needs no decoding (the text has a code point for each of its characters).
        bool simple = FastPaths && text.Length > 0 && IsSimpleLeftToRight(text, bidiText, paragraphLevel);
        bool rented = pooled && simple;
        int[] codePoints;
        int[] utf16Index;
        BidiResult bidi;
        if (rented)
        {
            // The arrays that the paragraph keeps are rented, and given back by its Release; a rented array may be longer than the text and holds what it was left with, so each is written in full to the count that is read.
            codePoints = ScratchArray.Rent<int>(text.Length);
            utf16Index = ScratchArray.Rent<int>(text.Length + 1);
            for (int i = 0; i < text.Length; i++)
            {
                codePoints[i] = text[i];
                utf16Index[i] = i;
            }

            utf16Index[text.Length] = text.Length;
            try
            {
                bidi = SimpleBidi(bidiText ?? text, true);
            }
            catch
            {
                ScratchArray.Return(codePoints);
                ScratchArray.Return(utf16Index);
                throw;
            }
        }
        else
        {
            codePoints = UnicodeText.CodePoints(text, out utf16Index);
            bidi = simple ? SimpleBidi(bidiText ?? text, false) : UnicodeBidi.Resolve(bidiText is null ? codePoints : BidiCodePoints(bidiText, codePoints.Length), paragraphLevel);
        }

        int count = rented ? text.Length : codePoints.Length;
        if (count == 0)
        {
            return new ShapedParagraph { ParagraphLevel = bidi.ParagraphLevel, Runs = [], Missing = [], CodePoints = codePoints, Utf16Index = utf16Index, Bidi = bidi };
        }

        // What follows is scratch that no result keeps (a run takes only its numbers from these arrays), so it is rented whatever the caller asked for and given back on every way out of the method.
        int[]? scriptOf = null;
        int[]? styleOf = null;
        int[]? fontOf = null;
        ShapedParagraph? result = null;
        try
        {
            // The text of the simple path is one script: Latin when it has a letter, else Common (the itemizer's answer for it, which the tests hold the path to).
            scriptOf = ScratchArray.Rent<int>(count);
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

                Array.Fill(scriptOf, only, 0, count);
            }
            else
            {
                foreach ((int start, int length, int script) in ScriptItemizer.Itemize(codePoints))
                {
                    Array.Fill(scriptOf, script, start, length);
                }
            }

            List<int> missing = [];
            styleOf = ScratchArray.Rent<int>(count);
            StyleOfCodePoints(styleOf, count, utf16Index, text.Length, styling);
            fontOf = ScratchArray.Rent<int>(count);
            ChooseFonts(fontOf, codePoints, count, utf16Index, fonts, styling, styleOf, missing, simple);

            // A run ends where the level, the script, the font or what shaping depends on in the style (the font order, the size, the language) changes: a change of colour alone does not end it, so that a word that is partly coloured is still shaped as one.
            List<ShapedRun> runs = [];
            int runStart = 0;
            for (int i = 1; i <= count; i++)
            {
                if (i < count && bidi.Levels[i] == bidi.Levels[runStart] && scriptOf[i] == scriptOf[runStart] && fontOf[i] == fontOf[runStart] && KeyOf(styling, styleOf[i]) == KeyOf(styling, styleOf[runStart]))
                {
                    continue;
                }

                string? runLanguage = styling is null ? language : styling.Languages[styleOf[runStart]];
                runs.Add(ShapeRun(text, utf16Index[runStart], utf16Index[i] - utf16Index[runStart], bidi.Levels[runStart], scriptOf[runStart], fontOf[runStart], fonts[fontOf[runStart]], runLanguage, styleOf[runStart], simple));
                runStart = i;
            }

            result = new ShapedParagraph { ParagraphLevel = bidi.ParagraphLevel, Runs = runs, Missing = missing, CodePoints = codePoints, Utf16Index = utf16Index, Bidi = bidi, CodePointCount = count, Pooled = rented };
            return result;
        }
        finally
        {
            if (scriptOf is not null)
            {
                ScratchArray.Return(scriptOf);
            }

            if (styleOf is not null)
            {
                ScratchArray.Return(styleOf);
            }

            if (fontOf is not null)
            {
                ScratchArray.Return(fontOf);
            }

            if (rented && result is null)
            {
                // Nobody got the paragraph (a style that does not tile the text, a font that fails): its arrays are given back here, since no caller will.
                ScratchArray.Return(codePoints);
                ScratchArray.Return(utf16Index);
                ScratchArray.Return(bidi.Levels);
                ScratchArray.Return(bidi.Classes);
            }
        }
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
    private static void StyleOfCodePoints(int[] styleOf, int count, int[] utf16Index, int textLength, ShapeStyling? styling)
    {
        Array.Clear(styleOf, 0, count);
        if (styling is null)
        {
            return;
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
    }

    /// <summary>
    /// A font for every character, chosen per grapheme cluster so that a base and its marks are never split between fonts (nor between styles:
    /// a cluster is set in the style of its first character, which <paramref name="styleOf"/> is changed to say). A cluster of a real
    /// script takes the first font in its style's order that has a glyph for all of it (so the order is the order of preference); spaces,
    /// digits and punctuation, which belong to no script, stay in the font of the text before them when it covers them and the style is the same, so that a run is not
    /// cut at every space. When no font covers a whole cluster, the first font that covers its first character is used.
    /// </summary>
    private static void ChooseFonts(int[] chosen, int[] codePoints, int count, int[] utf16Index, IReadOnlyList<ShapingFont> fonts, ShapeStyling? styling, int[] styleOf, List<int> missing, bool everyCharacterIsACluster)
    {
        bool[]? boundaries = everyCharacterIsACluster ? null : UnicodeGraphemeBreaker.Boundaries(codePoints.AsSpan(0, count));
        int previous = styling is null ? 0 : styling.FontOrders[styleOf[0]][0];
        int previousStyle = styleOf[0];
        for (int start = 0; start < count;)
        {
            int end = start + 1;
            while (boundaries is not null && end < count && !boundaries[end])
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

    /// <summary>
    /// The bidi result of <paramref name="source"/>, text that <see cref="IsSimpleLeftToRight"/> accepted: paragraph level 0 and every level 0, with the classes of the characters. With <paramref name="rented"/> the two arrays come from
    /// <see cref="ScratchArray"/> (longer than the text, and every entry that is read is written here) and the paragraph that holds them gives them back.
    /// </summary>
    private static BidiResult SimpleBidi(string source, bool rented)
    {
        BidiClass[] classes = rented ? ScratchArray.Rent<BidiClass>(source.Length) : new BidiClass[source.Length];
        byte[] levels;
        try
        {
            levels = rented ? ScratchArray.Rent<byte>(source.Length) : new byte[source.Length];
        }
        catch
        {
            if (rented)
            {
                ScratchArray.Return(classes);
            }

            throw;
        }

        for (int i = 0; i < source.Length; i++)
        {
            classes[i] = BidiData.ClassOf(source[i]);
        }

        if (rented)
        {
            Array.Clear(levels, 0, source.Length);
        }

        return new BidiResult(0, levels, classes) { Count = source.Length };
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
        // The words of the run are found first, so that the result is made once, of the size it will have: the glyphs of a run are kept by the lines (and by the font's cache of whole texts), so an array that is made too big and cut would leave a copy behind.
        // The segments of a run are at most as many as its characters; the array that holds them is rented and, being references, cleared before it goes back.
        ShapingFont.WordTable table = font.GetWordTable(scriptTag, language);
        ShapingFont.ShapedWord[] words = ScratchArray.Rent<ShapingFont.ShapedWord>(length);
        int wordCount = 0;
        try
        {
            int total = 0;
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
                ShapingFont.ShapedWord? word = table.Get(text, segment, segmentLength);
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
                    table.Add(piece, word);
                }

                if (!word.AllBase)
                {
                    return null;
                }

                words[wordCount++] = word;
                total += word.Glyphs.Length;
                segment = segmentEnd;
            }

            ShapedGlyph[] result = new ShapedGlyph[total];
            int count = 0;
            int at = start;
            for (int w = 0; w < wordCount; w++)
            {
                ShapedGlyph[] glyphs = words[w].Glyphs;
                for (int i = 0; i < glyphs.Length; i++)
                {
                    result[count++] = glyphs[i] with { Cluster = glyphs[i].Cluster + at };
                }

                // The segment's own length is not kept with the word, so the next segment starts where the glyphs of this one leave off in the text: found again from the text, as the first loop found it.
                at = NextSegmentStart(text, at, end);
            }

            return result;
        }
        finally
        {
            Array.Clear(words, 0, wordCount);
            ScratchArray.Return(words);
        }
    }

    /// <summary>The index where the segment after the one that starts at <paramref name="segment"/> starts: a stretch of spaces, or of other characters, ends at the first character of the other kind.</summary>
    private static int NextSegmentStart(string text, int segment, int end)
    {
        bool spaces = text[segment] == ' ';
        int next = segment + 1;
        while (next < end && (text[next] == ' ') == spaces)
        {
            next++;
        }

        return next;
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
