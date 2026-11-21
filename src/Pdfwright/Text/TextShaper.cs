using System.Runtime.InteropServices;
using HarfBuzzSharp;
using Pdfwright.Fonts;
using Pdfwright.Unicode;

namespace Pdfwright.Text;

/// <summary>A font ready for shaping: the TrueType reader's view of it and HarfBuzz's, over one copy of the font's bytes.</summary>
internal sealed class ShapingFont : IDisposable
{
    private readonly Blob _blob;
    private readonly Face _face;

    internal ShapingFont(TrueTypeFont font)
    {
        ArgumentNullException.ThrowIfNull(font);
        Font = font;

        // The bytes are copied to native memory that HarfBuzz owns until it releases the blob: a managed buffer it did not pin could move.
        byte[] bytes = font.RawBytes;
        IntPtr native = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, native, bytes.Length);
        _blob = new Blob(native, bytes.Length, MemoryMode.ReadOnly, () => Marshal.FreeHGlobal(native));
        _face = new Face(_blob, 0);
        Shaper = new HarfBuzzSharp.Font(_face);
        Shaper.SetScale(font.UnitsPerEm, font.UnitsPerEm);
    }

    internal TrueTypeFont Font { get; }

    internal HarfBuzzSharp.Font Shaper { get; }

    public void Dispose()
    {
        Shaper.Dispose();
        _face.Dispose();
        _blob.Dispose();
    }
}

/// <summary>One glyph as the shaper placed it, in font units.</summary>
internal readonly record struct ShapedGlyph(int Glyph, int Cluster, int XAdvance, int XOffset, int YOffset);

/// <summary>
/// A stretch of text that is shaped as one: one font, one script, one direction (one bidi level). <see cref="Glyphs"/> are in the order they
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
    /// </summary>
    internal static ShapedParagraph Shape(string text, IReadOnlyList<ShapingFont> fonts, int paragraphLevel = UnicodeBidi.AutoLevel, string? language = null, string? bidiText = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(fonts);
        if (fonts.Count == 0)
        {
            throw new ArgumentException("At least one font is needed.", nameof(fonts));
        }

        int[] codePoints = UnicodeText.CodePoints(text, out int[] utf16Index);
        BidiResult bidi = UnicodeBidi.Resolve(bidiText is null ? codePoints : BidiCodePoints(bidiText, codePoints.Length), paragraphLevel);
        if (codePoints.Length == 0)
        {
            return new ShapedParagraph { ParagraphLevel = bidi.ParagraphLevel, Runs = [], Missing = [], CodePoints = codePoints, Utf16Index = utf16Index, Bidi = bidi };
        }

        List<(int Start, int Length, int Script)> scriptRuns = ScriptItemizer.Itemize(codePoints);
        int[] scriptOf = new int[codePoints.Length];
        foreach ((int start, int length, int script) in scriptRuns)
        {
            Array.Fill(scriptOf, script, start, length);
        }

        List<int> missing = [];
        int[] fontOf = ChooseFonts(codePoints, utf16Index, fonts, missing);

        // A run ends where the level, the script or the font changes.
        List<ShapedRun> runs = [];
        int runStart = 0;
        for (int i = 1; i <= codePoints.Length; i++)
        {
            if (i < codePoints.Length && bidi.Levels[i] == bidi.Levels[runStart] && scriptOf[i] == scriptOf[runStart] && fontOf[i] == fontOf[runStart])
            {
                continue;
            }

            runs.Add(ShapeRun(text, utf16Index[runStart], utf16Index[i] - utf16Index[runStart], bidi.Levels[runStart], scriptOf[runStart], fontOf[runStart], fonts[fontOf[runStart]], language));
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

    /// <summary>
    /// A font for every character, chosen per grapheme cluster so that a base and its marks are never split between fonts. A cluster of a real
    /// script takes the first font in the list that has a glyph for all of it (so the order of the list is the order of preference); spaces,
    /// digits and punctuation, which belong to no script, stay in the font of the text before them when it covers them, so that a run is not
    /// cut at every space. When no font covers a whole cluster, the first font that covers its first character is used.
    /// </summary>
    private static int[] ChooseFonts(int[] codePoints, int[] utf16Index, IReadOnlyList<ShapingFont> fonts, List<int> missing)
    {
        bool[] boundaries = UnicodeGraphemeBreaker.Boundaries(codePoints);
        int[] chosen = new int[codePoints.Length];
        int previous = 0;
        for (int start = 0; start < codePoints.Length;)
        {
            int end = start + 1;
            while (end < codePoints.Length && !boundaries[end])
            {
                end++;
            }

            int pick = PickFont(codePoints, start, end, previous, fonts, out bool anyMissing);
            for (int i = start; i < end; i++)
            {
                chosen[i] = pick;
                if (anyMissing && !NeedsNoGlyph(codePoints[i]) && fonts[pick].Font.GlyphForCodePoint(codePoints[i]) == 0)
                {
                    missing.Add(utf16Index[i]);
                }
            }

            previous = pick;
            start = end;
        }

        return chosen;
    }

    private static int PickFont(int[] codePoints, int start, int end, int previous, IReadOnlyList<ShapingFont> fonts, out bool anyMissing)
    {
        anyMissing = false;
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
            return previous; // only characters that need no glyph
        }

        bool neutral = ScriptData.ScriptOf(codePoints[first]) == ScriptData.Common;
        if (neutral && Covers(fonts[previous], codePoints, start, end))
        {
            return previous;
        }

        for (int f = 0; f < fonts.Count; f++)
        {
            if (Covers(fonts[f], codePoints, start, end))
            {
                return f;
            }
        }

        for (int f = 0; f < fonts.Count; f++)
        {
            if (fonts[f].Font.GlyphForCodePoint(codePoints[first]) != 0)
            {
                anyMissing = true; // the marks after the base are drawn without their glyphs
                return f;
            }
        }

        anyMissing = true;
        return previous;
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

    private static ShapedRun ShapeRun(string text, int utf16Start, int utf16Length, byte level, int script, int fontIndex, ShapingFont font, string? language)
    {
        using HarfBuzzSharp.Buffer buffer = new();

        // The item offset and length make HarfBuzz see the text before and after the run as context (a joining letter, a tatweel).
        buffer.AddUtf16(text, utf16Start, utf16Length);
        buffer.Direction = (level & 1) == 1 ? Direction.RightToLeft : Direction.LeftToRight;
        buffer.Script = HarfBuzzSharp.Script.Parse(ScriptData.CodeOf(script));
        if ((level & 1) == 1)
        {
            // Cluster level 1 gives every mark of a right-to-left run a cluster, and so a text, of its own, which the writer needs to keep marks apart for pdf.js.
            // Left-to-right runs stay at the default level: level 1 there made xpdf misread five Indic lines.
            buffer.ClusterLevel = ClusterLevel.MonotoneCharacters;
        }

        if (language is not null)
        {
            buffer.Language = new HarfBuzzSharp.Language(language);
        }

        font.Shaper.Shape(buffer);
        GlyphInfo[] infos = buffer.GlyphInfos;
        GlyphPosition[] positions = buffer.GlyphPositions;
        ShapedGlyph[] glyphs = new ShapedGlyph[infos.Length];
        for (int i = 0; i < infos.Length; i++)
        {
            glyphs[i] = new ShapedGlyph((int)infos[i].Codepoint, (int)infos[i].Cluster, positions[i].XAdvance, positions[i].XOffset, positions[i].YOffset);
        }

        return new ShapedRun { Utf16Start = utf16Start, Utf16Length = utf16Length, Level = level, Script = script, FontIndex = fontIndex, Glyphs = glyphs };
    }
}
