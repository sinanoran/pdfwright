using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Pdfwright.Fonts;

namespace Pdfwright.Pdf;

/// <summary>
/// A TrueType font embedded in a document as a subset (ISO 32000-1, 9.7): a Type 0 font with the identity encoding over a
/// CIDFontType2, so that content names glyphs by their id (two bytes each), with the widths of the glyphs used and a
/// <c>ToUnicode</c> map from each glyph to the text it was drawn for.
/// </summary>
/// <remarks>
/// The reference is handed out at once and the objects are written when the document is completed, when the set of glyphs
/// the pages used is known: a page's resources can refer to the font long before the font's last glyph is drawn.
/// </remarks>
internal sealed class PdfEmbeddedFont
{
    private readonly TrueTypeFont _font;
    private readonly SortedDictionary<int, string?> _glyphs = [];
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
        Reference = document.AllocateObject();
        document.OnComplete(Write);
    }

    /// <summary>The font object that a page's resources refer to.</summary>
    internal PdfReference Reference { get; }

    /// <summary>The font that content is drawn with.</summary>
    internal TrueTypeFont Font => _font;

    /// <summary>
    /// Records that the document draws a glyph, and the text it stands for (the source characters of its cluster), which
    /// <c>ToUnicode</c> will give back when the document is copied from or searched. An empty text is no text.
    /// </summary>
    /// <returns>
    /// <c>true</c> when <c>ToUnicode</c> will give back the text passed. <c>false</c> when the glyph is already drawn for different
    /// text, which keeps the first: the caller then wraps this use in <see cref="ContentStreamBuilder.BeginActualText"/> so that the
    /// text it means is what is extracted.
    /// </returns>
    internal bool UseGlyph(int glyph, string? text)
    {
        if (_written)
        {
            throw new InvalidOperationException("The document is complete: no more glyphs can be recorded.");
        }

        if (glyph < 0 || glyph >= _font.GlyphCount)
        {
            throw new ArgumentOutOfRangeException(nameof(glyph), glyph, $"The font has {_font.GlyphCount} glyphs.");
        }

        if (text is { Length: 0 })
        {
            text = null;
        }

        if (!_glyphs.TryGetValue(glyph, out string? existing))
        {
            _glyphs[glyph] = text;
            return true;
        }

        if (existing is null && text is not null)
        {
            _glyphs[glyph] = text;
            return true;
        }

        return text is null || text == existing;
    }

    /// <summary>
    /// A glyph's advance in thousandths of the font size, which is what PDF widths and <c>TJ</c> adjustments are in. It is a whole
    /// number, the one <c>/W</c> carries, so that the positions the layout computes are the ones a viewer draws.
    /// </summary>
    internal double Advance(int glyph) => Math.Round(_font.Advance(glyph) * 1000.0 / _font.UnitsPerEm);

    /// <summary>The bytes of a glyph in a content stream string: two, big-endian (the identity encoding's code is the glyph id).</summary>
    internal static byte[] Code(int glyph) => [(byte)(glyph >> 8), (byte)glyph];

    private void Write(PdfDocumentWriter document)
    {
        _written = true;
        if (_glyphs.Count == 0)
        {
            // A font that no page used still has to be a valid object, because pages may refer to it.
            _glyphs[0] = null;
        }

        string tag = SubsetTag();
        string baseFont = tag + "+" + _font.PostScriptName;
        byte[] subset = TrueTypeSubsetter.Subset(_font, [.. _glyphs.Keys]);

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
            .Set("CIDToGIDMap", "Identity"));

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
        foreach (int glyph in _glyphs.Keys)
        {
            hash.AppendData(BigEndian(glyph));
        }

        byte[] digest = hash.GetHashAndReset();
        char[] tag = new char[6];
        for (int i = 0; i < tag.Length; i++)
        {
            tag[i] = (char)('A' + (digest[i] % 26));
        }

        return new string(tag);
    }

    /// <summary>The <c>/W</c> array: runs of consecutive glyph ids as <c>first [w1 w2 ...]</c>.</summary>
    private PdfArray Widths()
    {
        PdfArray widths = new();
        int[] ids = [.. _glyphs.Keys];
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
                run.Add(new PdfInteger((long)Math.Round(Advance(ids[k]))));
            }

            widths.Add(new PdfInteger(ids[start]));
            widths.Add(run);
            i++;
        }

        return widths;
    }

    /// <summary>
    /// The <c>ToUnicode</c> CMap (ISO 32000-1, 9.10.3): one <c>bfchar</c> per glyph that has a text, the text as UTF-16BE
    /// (several code points when the glyph stands for a cluster, such as a ligature), in blocks of at most 100 as the format requires.
    /// </summary>
    private string ToUnicodeMap()
    {
        List<KeyValuePair<int, string?>> mapped = [.. _glyphs.Where(g => !string.IsNullOrEmpty(g.Value))];
        StringBuilder cmap = new();
        cmap.Append("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n");
        cmap.Append("/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n");
        cmap.Append("/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n");
        cmap.Append("1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n");
        for (int block = 0; block < mapped.Count; block += 100)
        {
            List<KeyValuePair<int, string?>> chunk = mapped.GetRange(block, Math.Min(100, mapped.Count - block));
            cmap.Append(chunk.Count.ToString(CultureInfo.InvariantCulture)).Append(" beginbfchar\n");
            foreach (KeyValuePair<int, string?> pair in chunk)
            {
                cmap.Append('<').Append(pair.Key.ToString("X4", CultureInfo.InvariantCulture)).Append("> <")
                    .Append(Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(pair.Value!))).Append(">\n");
            }

            cmap.Append("endbfchar\n");
        }

        cmap.Append("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n");
        return cmap.ToString();
    }
}
