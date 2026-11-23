using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Pdfwright.Fonts;

namespace Pdfwright.Pdf;

/// <summary>
/// A TrueType font embedded in a document as a subset (ISO 32000-1, 9.7): a Type 0 font with the identity encoding over a
/// CIDFontType2, so that content names glyphs by a two-byte code, with the widths of the glyphs used and a <c>ToUnicode</c>
/// map from each code to the text it was drawn for.
/// </summary>
/// <remarks>
/// The reference is handed out at once and the objects are written when the document is completed, when the set of glyphs
/// the pages used is known: a page's resources can refer to the font long before the font's last glyph is drawn.
/// <para>
/// A code is the glyph's id the first time the glyph is drawn for a text. When the same glyph is drawn for another text (a
/// consonant alone and the same consonant inside a conjunct, U+2126 and U+03A9, a ligature in two words), it gets a code of
/// its own, above the font's glyph count, and a <c>CIDToGIDMap</c> sends both codes to the one glyph. Every code then has exactly one
/// text in <c>ToUnicode</c>, so a reader that follows it extracts the text that was drawn. A code that stands for no text (the rest of a cluster) maps to
/// U+034F COMBINING GRAPHEME JOINER, not to the empty string: pdf.js turns an empty mapping into the code itself, which put junk characters
/// into every Arabic and Indic line (27 of 61 corpus lines clean, 56 with the joiner). The joiner is invisible, a default-ignorable that does not
/// break the joining of Arabic letters, and a reader that ignores it gets the text exactly; a test strips it.
/// </para>
/// </remarks>
internal sealed class PdfEmbeddedFont
{
    /// <summary>U+034F COMBINING GRAPHEME JOINER: the text of a code that only continues a cluster (see the class remarks).</summary>
    internal const string ContinuationText = "\u034F";

    private readonly TrueTypeFont _font;

    // The codes handed out, kept by glyph. A glyph's first code is its id, so the text it was first drawn for is all that is kept for it (null for a glyph not drawn yet), and the codes of a glyph drawn
    // for other texts are a short chain of their own. Every glyph drawn is looked up here (more than a hundred thousand times for a 1,000-row table), so these are plain arrays indexed by the glyph instead
    // of a dictionary keyed by the glyph and its text, which hashed the text each time and, as a generic collection over a tuple, had to be compiled at run time even in a ReadyToRun build. The arrays are
    // in pages of 256 glyphs, made when a glyph of the page is first drawn, so that a document that draws a few glyphs of a font of 65,000 (a CJK font) makes a few pages, not arrays of the whole font
    // (a megabyte for each document); the list of pages is one reference for each 256 glyphs.
    private readonly GlyphPage?[] _pages;

    // The codes above the glyph count, in the order they were handed out, which is the order of the codes.
    private readonly List<ExtraCode> _extraCodes = [];
    private int _glyphsDrawn;
    private int _nextExtra;
    private bool _written;

    /// <summary>The number of glyphs in a page of <see cref="GlyphPage"/>.</summary>
    private const int PageSize = 256;

    /// <summary>For each glyph of one page (256 glyph ids that follow one another), the text it was first drawn for and the chain of its other codes.</summary>
    private sealed class GlyphPage
    {
        internal string?[] FirstText { get; } = new string?[PageSize];

        internal ExtraCode?[] Extras { get; } = new ExtraCode?[PageSize];
    }

    /// <summary>A code above the glyph count: a glyph drawn for another text than the one it was first drawn for. The codes of one glyph form a chain, the newest first.</summary>
    private sealed class ExtraCode(int code, int glyph, string text, ExtraCode? next)
    {
        internal int Code { get; } = code;

        internal int Glyph { get; } = glyph;

        internal string Text { get; } = text;

        internal ExtraCode? Next { get; } = next;
    }

    /// <summary>The codes handed out, in ascending order, with the glyph and the text of each (three arrays of one length).</summary>
    private readonly record struct CodeTable(int[] Codes, int[] Glyphs, string[] Texts)
    {
        internal int Count => Codes.Length;
    }

    internal PdfEmbeddedFont(PdfDocumentWriter document, TrueTypeFont font)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(font);
        if (!font.MayEmbedSubset)
        {
            throw new InvalidOperationException(
                $"The font '{font.FamilyName}' may not be embedded as a subset: its licence (OS/2 fsType {font.EmbeddingPermission}) forbids it.");
        }

        _font = font;
        _nextExtra = font.GlyphCount;
        _pages = new GlyphPage?[Math.Max(1, (font.GlyphCount + PageSize - 1) / PageSize)];
        Reference = document.AllocateObject();
        document.OnComplete(Write);
    }

    /// <summary>The font object that a page's resources refer to.</summary>
    internal PdfReference Reference { get; }

    /// <summary>The font that content is drawn with.</summary>
    internal TrueTypeFont Font => _font;

    /// <summary>
    /// Records that the document draws a glyph for a text (the source characters of its cluster) and gives the code to draw it
    /// with, which <c>ToUnicode</c> will map back to exactly that text when the document is copied from or searched. A glyph that
    /// is only the continuation of a cluster (the second glyph of a split vowel) is recorded with no text, and <c>ToUnicode</c> maps it to <see cref="ContinuationText"/>.
    /// </summary>
    /// <returns>
    /// The code of the glyph for this text: write it with <see cref="Code"/>. Codes are handed out in the order of the calls, so
    /// call this only when drawing, never to measure: a measuring pass would add codes, and change the bytes of the file.
    /// </returns>
    internal int UseGlyph(int glyph, string? text)
    {
        if (_written)
        {
            throw new InvalidOperationException("The document is complete: no more glyphs can be recorded.");
        }

        if (glyph < 0 || glyph >= _font.GlyphCount)
        {
            throw new ArgumentOutOfRangeException(nameof(glyph), glyph, $"The font has {_font.GlyphCount} glyphs.");
        }

        text ??= string.Empty;
        GlyphPage page = _pages[glyph / PageSize] ??= new GlyphPage();
        int slot = glyph % PageSize;
        string? firstText = page.FirstText[slot];
        if (firstText is null)
        {
            page.FirstText[slot] = text;
            _glyphsDrawn++;
            return glyph;
        }

        if (ReferenceEquals(firstText, text) || string.Equals(firstText, text, StringComparison.Ordinal))
        {
            return glyph;
        }

        for (ExtraCode? extra = page.Extras[slot]; extra is not null; extra = extra.Next)
        {
            if (string.Equals(extra.Text, text, StringComparison.Ordinal))
            {
                return extra.Code;
            }
        }

        int code = _nextExtra;
        if (code > 0xFFFF)
        {
            throw new InvalidOperationException(
                $"The font has no code left for glyph {glyph} drawn for another text: a two-byte code names 65,536 glyph and text pairs, and this font's {_font.GlyphCount} glyphs take the first of them.");
        }

        _nextExtra++;
        ExtraCode made = new(code, glyph, text, page.Extras[slot]);
        page.Extras[slot] = made;
        _extraCodes.Add(made);
        return code;
    }

    /// <summary>
    /// Every code handed out, in ascending order: the glyphs drawn (whose code is their id) in the order of their ids, then the codes above the glyph count in the order they were handed out, which is
    /// ascending as well. This is the order the font's objects are written in.
    /// </summary>
    private CodeTable Codes()
    {
        int count = _glyphsDrawn + _extraCodes.Count;
        CodeTable table = new(new int[count], new int[count], new string[count]);
        int at = 0;
        for (int p = 0; p < _pages.Length; p++)
        {
            if (_pages[p] is not { } page)
            {
                continue;
            }

            for (int slot = 0; slot < PageSize; slot++)
            {
                if (page.FirstText[slot] is { } text)
                {
                    int glyph = (p * PageSize) + slot;
                    table.Codes[at] = glyph;
                    table.Glyphs[at] = glyph;
                    table.Texts[at] = text;
                    at++;
                }
            }
        }

        foreach (ExtraCode extra in _extraCodes)
        {
            table.Codes[at] = extra.Code;
            table.Glyphs[at] = extra.Glyph;
            table.Texts[at] = extra.Text;
            at++;
        }

        return table;
    }

    /// <summary>The glyphs drawn, each once, in the order of their ids: what the subset keeps besides the glyphs these need.</summary>
    private int[] GlyphsDrawn()
    {
        int[] glyphs = new int[_glyphsDrawn];
        int at = 0;
        for (int p = 0; p < _pages.Length; p++)
        {
            if (_pages[p] is not { } page)
            {
                continue;
            }

            for (int slot = 0; slot < PageSize; slot++)
            {
                if (page.FirstText[slot] is not null)
                {
                    glyphs[at++] = (p * PageSize) + slot;
                }
            }
        }

        return glyphs;
    }

    /// <summary>
    /// A glyph's advance in thousandths of the font size, which is what PDF widths and <c>TJ</c> adjustments are in. It is a whole
    /// number, the one <c>/W</c> carries, so that the positions the layout computes are the ones a viewer draws.
    /// </summary>
    internal double Advance(int glyph) => Math.Round(_font.Advance(glyph) * 1000.0 / _font.UnitsPerEm);

    /// <summary>The bytes of a code in a content stream string: two, big-endian (the identity encoding).</summary>
    internal static byte[] Code(int code) => [(byte)(code >> 8), (byte)code];

    private void Write(PdfDocumentWriter document)
    {
        _written = true;
        if (_glyphsDrawn == 0)
        {
            // A font that no page used still has to be a valid object, because pages may refer to it.
            (_pages[0] ??= new GlyphPage()).FirstText[0] = string.Empty;
            _glyphsDrawn = 1;
        }

        CodeTable codes = Codes();
        string tag = SubsetTag(codes);
        string baseFont = tag + "+" + _font.PostScriptName;
        byte[] subset = TrueTypeSubsetter.Subset(_font, GlyphsDrawn());

        PdfReference file = document.AddObject(new PdfStream(
            new PdfDictionary().Set("Length1", subset.Length),
            subset));

        double scale = 1000.0 / _font.UnitsPerEm;
        int capHeight = _font.CapHeight > 0 ? _font.CapHeight : (int)(_font.Ascender * 0.7);
        int flags = 32;
        if (_font.IsFixedPitch)
        {
            flags |= 1;
        }

        if (_font.ItalicAngle != 0)
        {
            flags |= 64;
        }

        PdfReference descriptor = document.AddObject(new PdfDictionary()
            .Set("Type", "FontDescriptor")
            .Set("FontName", baseFont)
            .Set("Flags", flags)
            .Set("FontBBox", PdfArray.Of(
                Math.Round(_font.XMin * scale),
                Math.Round(_font.YMin * scale),
                Math.Round(_font.XMax * scale),
                Math.Round(_font.YMax * scale)))
            .Set("ItalicAngle", new PdfReal(_font.ItalicAngle))
            .Set("Ascent", (long)Math.Round(_font.Ascender * scale))
            .Set("Descent", (long)Math.Round(_font.Descender * scale))
            .Set("CapHeight", (long)Math.Round(capHeight * scale))
            .Set("StemV", 40 + (_font.WeightClass / 10))
            .Set("FontFile2", file));

        // Codes above the glyph count exist only when a glyph was drawn for a second text: a map sends them to their glyph.
        PdfObject glyphMap = _nextExtra > _font.GlyphCount
            ? document.AddObject(new PdfStream(new PdfDictionary(), CidToGidMap(codes)))
            : new PdfName("Identity");
        PdfReference cidFont = document.AddObject(new PdfDictionary()
            .Set("Type", "Font")
            .Set("Subtype", "CIDFontType2")
            .Set("BaseFont", baseFont)
            .Set("CIDSystemInfo", new PdfDictionary()
                .Set("Registry", new PdfString(Encoding.ASCII.GetBytes("Adobe"), hex: false))
                .Set("Ordering", new PdfString(Encoding.ASCII.GetBytes("Identity"), hex: false))
                .Set("Supplement", 0))
            .Set("FontDescriptor", descriptor)
            .Set("DW", 1000)
            .Set("W", Widths(codes))
            .Set("CIDToGIDMap", glyphMap));

        PdfReference toUnicode = document.AddObject(new PdfStream(new PdfDictionary(), Encoding.ASCII.GetBytes(ToUnicodeMap(codes))));

        document.WriteObject(Reference, new PdfDictionary()
            .Set("Type", "Font")
            .Set("Subtype", "Type0")
            .Set("BaseFont", baseFont)
            .Set("Encoding", "Identity-H")
            .Set("DescendantFonts", new PdfArray(cidFont))
            .Set("ToUnicode", toUnicode));
    }

    /// <summary>
    /// Six capital letters (ISO 32000-1, 9.6.4) that tell two subsets of one font apart, taken from a hash of the font and of
    /// the glyphs kept, so that the same document is the same file and different subsets do not collide.
    /// </summary>
    private static byte[] BigEndian(int value) => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

    private string SubsetTag(CodeTable codes)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(_font.Bytes);
        for (int i = 0; i < codes.Count; i++)
        {
            hash.AppendData(BigEndian(codes.Codes[i]));
            hash.AppendData(BigEndian(codes.Glyphs[i]));
            hash.AppendData(Encoding.UTF8.GetBytes(codes.Texts[i]));
            hash.AppendData([0]);
        }

        byte[] digest = hash.GetHashAndReset();
        char[] tag = new char[6];
        for (int i = 0; i < tag.Length; i++)
        {
            tag[i] = (char)('A' + (digest[i] % 26));
        }

        return new string(tag);
    }

    /// <summary>
    /// The map from codes to glyphs: two bytes per code. Only the codes that were drawn are filled in; every other entry is zero
    /// (the <c>.notdef</c> glyph, which nothing draws), so that the map compresses to the size of what was used.
    /// </summary>
    private byte[] CidToGidMap(CodeTable codes)
    {
        byte[] map = new byte[_nextExtra * 2];
        for (int i = 0; i < codes.Count; i++)
        {
            map[codes.Codes[i] * 2] = (byte)(codes.Glyphs[i] >> 8);
            map[(codes.Codes[i] * 2) + 1] = (byte)codes.Glyphs[i];
        }

        return map;
    }

    /// <summary>The <c>/W</c> array: runs of consecutive codes as <c>first [w1 w2 ...]</c>.</summary>
    private PdfArray Widths(CodeTable codes)
    {
        PdfArray widths = new();
        int[] ids = codes.Codes;
        int i = 0;
        while (i < ids.Length)
        {
            int start = i;
            while (i + 1 < ids.Length && ids[i + 1] == ids[i] + 1)
            {
                i++;
            }

            PdfArray run = new();
            for (int k = start; k <= i; k++)
            {
                run.Add(new PdfInteger((long)Advance(codes.Glyphs[k])));
            }

            widths.Add(new PdfInteger(ids[start]));
            widths.Add(run);
            i++;
        }

        return widths;
    }

    /// <summary>
    /// The <c>ToUnicode</c> CMap (ISO 32000-1, 9.10.3): one <c>bfchar</c> per code, the text as UTF-16BE
    /// (several code points when the glyph stands for a cluster, such as a ligature), in blocks of at most 100 as the format requires.
    /// </summary>
    private static string ToUnicodeMap(CodeTable codes)
    {
        // A code with no text (the rest of a cluster) maps to U+034F, see the class remarks.
        StringBuilder cmap = new();
        cmap.Append("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n");
        cmap.Append("/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n");
        cmap.Append("/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n");
        cmap.Append("1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n");
        for (int block = 0; block < codes.Count; block += 100)
        {
            int chunk = Math.Min(100, codes.Count - block);
            cmap.Append(chunk.ToString(CultureInfo.InvariantCulture)).Append(" beginbfchar\n");
            for (int i = block; i < block + chunk; i++)
            {
                string text = codes.Texts[i];
                cmap.Append('<').Append(codes.Codes[i].ToString("X4", CultureInfo.InvariantCulture)).Append("> <")
                    .Append(Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(text.Length == 0 ? ContinuationText : text))).Append(">\n");
            }

            cmap.Append("endbfchar\n");
        }

        cmap.Append("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n");
        return cmap.ToString();
    }
}
