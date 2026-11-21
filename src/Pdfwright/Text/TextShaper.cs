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
        ReadOnlySpan<byte> bytes = font.Bytes;
        IntPtr native = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes.ToArray(), 0, native, bytes.Length);
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

    /// <summary>The characters no font had a glyph for (as UTF-16 indices): they are drawn as the first font's .notdef glyph.</summary>
    internal required List<int> Missing { get; init; }
}

/// <summary>
/// Turns a paragraph of text into shaped runs: bidi levels (UAX #9), script runs (UAX #24), a font for every character from a fallback list,
/// and HarfBuzz shaping of each run with the text around it as context. Drawing and line breaking are done by the layers above.
/// </summary>
internal static class TextShaper
{
    /// <summary>
    /// Shapes <paramref name="text"/> as one paragraph. <paramref name="fonts"/> is the fallback list: a character uses the font of the
    /// character before it when that font has a glyph for it (so a run is not cut at every space), otherwise the first font that does.
    /// </summary>
    internal static ShapedParagraph Shape(string text, IReadOnlyList<ShapingFont> fonts, int paragraphLevel = UnicodeBidi.AutoLevel, string? language = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(fonts);
        if (fonts.Count == 0)
        {
            throw new ArgumentException("At least one font is needed.", nameof(fonts));
        }

        int[] codePoints = UnicodeText.CodePoints(text, out int[] utf16Index);
        if (codePoints.Length == 0)
        {
            return new ShapedParagraph { ParagraphLevel = 0, Runs = [], Missing = [] };
        }

        BidiResult bidi = UnicodeBidi.Resolve(codePoints, paragraphLevel);
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

        return new ShapedParagraph { ParagraphLevel = bidi.ParagraphLevel, Runs = runs, Missing = missing };
    }

    private static int[] ChooseFonts(int[] codePoints, int[] utf16Index, IReadOnlyList<ShapingFont> fonts, List<int> missing)
    {
        int[] chosen = new int[codePoints.Length];
        int previous = 0;
        for (int i = 0; i < codePoints.Length; i++)
        {
            int pick = -1;
            if (fonts[previous].Font.GlyphForCodePoint(codePoints[i]) != 0)
            {
                pick = previous;
            }
            else
            {
                for (int f = 0; f < fonts.Count; f++)
                {
                    if (fonts[f].Font.GlyphForCodePoint(codePoints[i]) != 0)
                    {
                        pick = f;
                        break;
                    }
                }
            }

            if (pick < 0)
            {
                pick = previous;
                missing.Add(utf16Index[i]);
            }

            chosen[i] = pick;
            previous = pick;
        }

        return chosen;
    }

    private static ShapedRun ShapeRun(string text, int utf16Start, int utf16Length, byte level, int script, int fontIndex, ShapingFont font, string? language)
    {
        using HarfBuzzSharp.Buffer buffer = new();

        // The item offset and length make HarfBuzz see the text before and after the run as context (a joining letter, a tatweel).
        buffer.AddUtf16(text, utf16Start, utf16Length);
        buffer.Direction = (level & 1) == 1 ? Direction.RightToLeft : Direction.LeftToRight;
        buffer.Script = HarfBuzzSharp.Script.Parse(ScriptData.CodeOf(script));
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
