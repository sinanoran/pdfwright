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
/// text in <c>ToUnicode</c>, so a reader that follows it extracts the text that was drawn. A code that stands for no text maps to the empty string, which
/// some readers (pdf.js) do not honour: see the assessment in <c>docs/plan-0.1.md</c>.
/// </para>
/// </remarks>
internal sealed class PdfEmbeddedFont
{
    private readonly TrueTypeFont _font;
    private readonly Dictionary<(int Glyph, string Text), int> _codes = [];
    private readonly SortedDictionary<int, (int Glyph, string Text)> _byCode = [];
    private readonly HashSet<int> _glyphsWithCode = [];
    private int _nextExtra;
    private bool _written;

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
    /// is only the continuation of a cluster (the second glyph of a split vowel) is recorded with no text, and extracts as nothing.
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
        if (_codes.TryGetValue((glyph, text), out int known))
        {
            return known;
        }

        bool first = !_glyphsWithCode.Contains(glyph);
        int code = first ? glyph : _nextExtra;
        if (code > 0xFFFF)
        {
            throw new InvalidOperationException(
                $"The font has no code left for glyph {glyph} drawn for another text: a two-byte code names 65,536 glyph and text pairs, and this font's {_font.GlyphCount} glyphs take the first of them.");
        }

        if (first)
        {
            _glyphsWithCode.Add(glyph);
        }
        else
        {
            _nextExtra++;
        }

        _codes[(glyph, text)] = code;
        _byCode[code] = (glyph, text);
        return code;
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
        if (_byCode.Count == 0)
        {
            // A font that no page used still has to be a valid object, because pages may refer to it.
            _byCode[0] = (0, string.Empty);
            _glyphsWithCode.Add(0);
        }

        string tag = SubsetTag();
        string baseFont = tag + "+" + _font.PostScriptName;
        byte[] subset = TrueTypeSubsetter.Subset(_font, [.. _glyphsWithCode]);

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
            ? document.AddObject(new PdfStream(new PdfDictionary(), CidToGidMap()))
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
            .Set("W", Widths())
            .Set("CIDToGIDMap", glyphMap));

        PdfReference toUnicode = document.AddObject(new PdfStream(new PdfDictionary(), Encoding.ASCII.GetBytes(ToUnicodeMap())));

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

    private string SubsetTag()
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(_font.Bytes);
        foreach (KeyValuePair<int, (int Glyph, string Text)> pair in _byCode)
        {
            hash.AppendData(BigEndian(pair.Key));
            hash.AppendData(BigEndian(pair.Value.Glyph));
            hash.AppendData(Encoding.UTF8.GetBytes(pair.Value.Text));
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
    private byte[] CidToGidMap()
    {
        byte[] map = new byte[_nextExtra * 2];
        foreach (KeyValuePair<int, (int Glyph, string Text)> pair in _byCode)
        {
            map[pair.Key * 2] = (byte)(pair.Value.Glyph >> 8);
            map[(pair.Key * 2) + 1] = (byte)pair.Value.Glyph;
        }

        return map;
    }

    /// <summary>The <c>/W</c> array: runs of consecutive codes as <c>first [w1 w2 ...]</c>.</summary>
    private PdfArray Widths()
    {
        PdfArray widths = new();
        int[] ids = [.. _byCode.Keys];
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
                run.Add(new PdfInteger((long)Advance(_byCode[ids[k]].Glyph)));
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
    private string ToUnicodeMap()
    {
        List<KeyValuePair<int, (int Glyph, string Text)>> mapped = [.. _byCode]; // a code with no text (the rest of a cluster) maps to the empty string
        StringBuilder cmap = new();
        cmap.Append("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n");
        cmap.Append("/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n");
        cmap.Append("/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n");
        cmap.Append("1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n");
        for (int block = 0; block < mapped.Count; block += 100)
        {
            List<KeyValuePair<int, (int Glyph, string Text)>> chunk = mapped.GetRange(block, Math.Min(100, mapped.Count - block));
            cmap.Append(chunk.Count.ToString(CultureInfo.InvariantCulture)).Append(" beginbfchar\n");
            foreach (KeyValuePair<int, (int Glyph, string Text)> pair in chunk)
            {
                cmap.Append('<').Append(pair.Key.ToString("X4", CultureInfo.InvariantCulture)).Append("> <")
                    .Append(Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(pair.Value.Text))).Append(">\n");
            }

            cmap.Append("endbfchar\n");
        }

        cmap.Append("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n");
        return cmap.ToString();
    }
}
