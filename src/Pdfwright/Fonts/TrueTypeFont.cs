using System.Buffers.Binary;
using System.Text;

namespace Pdfwright.Fonts;

/// <summary>
/// A TrueType font (an sfnt with <c>glyf</c> outlines): the tables a PDF writer needs, read once. Fonts with CFF outlines
/// (OpenType "OTTO") are refused with a clear message: embedding them, and subsetting them, is a separate piece of work
/// planned after 0.1 (section 4 of <c>docs/plan-0.1.md</c>).
/// </summary>
/// <remarks>
/// The bytes of the font are copied once and held, not copied per table, and everything the font says is checked against
/// them when it is parsed, so nothing read afterwards can fail on a corrupt font. Instances are immutable and safe to share
/// between threads. A collection (.ttc) is read as its first font; a variable font is read as its default instance (its
/// outlines are not varied); a symbol font whose only character map is (3, 0) is refused.
/// </remarks>
internal sealed class TrueTypeFont
{
    private readonly byte[] _data;
    private readonly Dictionary<string, (int Offset, int Length)> _tables = [];
    private readonly Func<int, int> _glyphOf;
    private readonly int _numberOfHMetrics;
    private readonly int _hmtx;
    private readonly bool _longLoca;
    private readonly int _loca;
    private readonly int _glyf;

    private TrueTypeFont(byte[] data)
    {
        _data = data;
        int directory = 0;
        if (Tag(0) == "ttcf")
        {
            // A collection: the first font in it.
            directory = (int)U32(12);
        }

        string scaler = Tag(directory);
        if (scaler == "OTTO")
        {
            throw new NotSupportedException("A font with CFF outlines (OpenType 'OTTO') is not supported yet: use a TrueType (glyf) font.");
        }

        if (U32(directory) != 0x00010000 && scaler != "true")
        {
            throw new InvalidDataException("Not a TrueType font: the file does not start with an sfnt header.");
        }

        int tableCount = U16(directory + 4);
        for (int i = 0; i < tableCount; i++)
        {
            int record = directory + 12 + (i * 16);
            long offset = U32(record + 8);
            long length = U32(record + 12);
            if (offset + length > _data.Length)
            {
                throw new InvalidDataException($"The font's '{Tag(record)}' table runs past the end of the file.");
            }

            _tables[Tag(record)] = ((int)offset, (int)length);
        }

        foreach (string required in new[] { "head", "hhea", "maxp", "hmtx", "loca", "glyf", "cmap" })
        {
            if (!_tables.ContainsKey(required))
            {
                throw new InvalidDataException($"The font has no '{required}' table.");
            }
        }

        Require("head", 54);
        Require("hhea", 36);
        Require("maxp", 6);
        Require("cmap", 4);
        int head = _tables["head"].Offset;
        UnitsPerEm = U16(head + 18);
        if (UnitsPerEm is < 16 or > 16384)
        {
            throw new InvalidDataException($"The font's unitsPerEm ({UnitsPerEm}) is outside 16 to 16384.");
        }

        IsItalic = (U16(head + 44) & 0x0002) != 0;
        XMin = I16(head + 36);
        YMin = I16(head + 38);
        XMax = I16(head + 40);
        YMax = I16(head + 42);
        _longLoca = I16(head + 50) != 0;
        GlyphCount = U16(_tables["maxp"].Offset + 4);
        if (GlyphCount == 0)
        {
            throw new InvalidDataException("The font has no glyphs.");
        }

        int hhea = _tables["hhea"].Offset;
        Ascender = I16(hhea + 4);
        Descender = I16(hhea + 6);
        _numberOfHMetrics = U16(hhea + 34);
        if (_numberOfHMetrics < 1 || _numberOfHMetrics > GlyphCount)
        {
            throw new InvalidDataException($"The font's numberOfHMetrics ({_numberOfHMetrics}) is outside 1 to its {GlyphCount} glyphs.");
        }

        Require("hmtx", (_numberOfHMetrics * 4) + ((GlyphCount - _numberOfHMetrics) * 2));
        Require("loca", (GlyphCount + 1) * (_longLoca ? 4 : 2));
        _hmtx = _tables["hmtx"].Offset;
        _loca = _tables["loca"].Offset;
        _glyf = _tables["glyf"].Offset;
        ValidateGlyphs();

        _glyphOf = BuildCmap();

        if (_tables.TryGetValue("OS/2", out (int Offset, int Length) os2))
        {
            if (os2.Length < 10)
            {
                throw new InvalidDataException("The font's 'OS/2' table is too short.");
            }

            EmbeddingPermission = U16(os2.Offset + 8);
            WeightClass = U16(os2.Offset + 4);
            if (os2.Length >= 64)
            {
                // fsSelection: bit 0 is italic, bit 6 is regular (which overrides a macStyle that says italic).
                int selection = U16(os2.Offset + 62);
                IsItalic = (selection & 0x0001) != 0 || (IsItalic && (selection & 0x0040) == 0);
            }

            if (os2.Length >= 90 && U16(os2.Offset) >= 2)
            {
                CapHeight = I16(os2.Offset + 88);
            }
        }

        if (_tables.TryGetValue("post", out (int Offset, int Length) post))
        {
            if (post.Length >= 16)
            {
                ItalicAngle = I32(post.Offset + 4) / 65536.0;
                IsFixedPitch = U32(post.Offset + 12) != 0;
            }
        }

        (FamilyName, PostScriptName) = ReadNames();
    }

    internal int UnitsPerEm { get; }

    internal int GlyphCount { get; }

    internal short XMin { get; }

    internal short YMin { get; }

    internal short XMax { get; }

    internal short YMax { get; }

    internal short Ascender { get; }

    internal short Descender { get; }

    /// <summary>The cap height from the OS/2 table, or 0 when the table does not say (the embedding then estimates it).</summary>
    internal int CapHeight { get; } = 0;

    internal int WeightClass { get; } = 400;

    /// <summary>The font is italic or oblique: OS/2 <c>fsSelection</c> bit 0, or the <c>head</c> table's <c>macStyle</c> bit 1 when the OS/2 table does not say.</summary>
    internal bool IsItalic { get; }

    /// <summary>The OS/2 <c>fsType</c>: what the font's licence says about embedding (0 when there is no OS/2 table).</summary>
    internal int EmbeddingPermission { get; }

    internal double ItalicAngle { get; }

    internal bool IsFixedPitch { get; }

    internal string FamilyName { get; }

    internal string PostScriptName { get; }

    /// <summary>The font file as it was read: what a hash of the font is taken over.</summary>
    internal ReadOnlySpan<byte> Bytes => _data;

    /// <summary>The same bytes as an array, for a consumer that needs one to copy from; it must not be changed.</summary>
    internal byte[] RawBytes => _data;

    /// <summary>
    /// Whether the licence allows embedding a <b>subset</b>, which is all Pdfwright does: fsType 0 (installable), 4 (preview and
    /// print) and 8 (editable) allow it; restricted licence (bit 1), no subsetting (bit 8) and bitmap-only embedding (bit 9) do not.
    /// A font of OS/2 version 0 to 2 that sets several of the usage bits (0x0006, say) is read as restricted, which is the safe
    /// side of a rule that says the least restrictive bit applies.
    /// </summary>
    internal bool MayEmbedSubset => (EmbeddingPermission & 0x0002) == 0 && (EmbeddingPermission & 0x0100) == 0 && (EmbeddingPermission & 0x0200) == 0;

    internal static TrueTypeFont Parse(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length < 12)
        {
            throw new InvalidDataException("Not a TrueType font: the file is shorter than an sfnt header.");
        }

        try
        {
            return new TrueTypeFont(data.ToArray());
        }
        catch (Exception error) when (error is ArgumentException or OverflowException)
        {
            throw new InvalidDataException("The font file is truncated or corrupt.", error);
        }
    }

    /// <summary>The glyph the font gives a character (before any shaping), or 0 (<c>.notdef</c>) when it has none.</summary>
    internal int GlyphForCodePoint(int codePoint) => _glyphOf(codePoint);

    /// <summary>The advance width of a glyph in font units.</summary>
    internal int Advance(int glyph)
    {
        if (glyph < 0 || glyph >= GlyphCount)
        {
            throw new ArgumentOutOfRangeException(nameof(glyph), glyph, $"The font has {GlyphCount} glyphs.");
        }

        int metric = Math.Min(glyph, _numberOfHMetrics - 1);
        return U16(_hmtx + (metric * 4));
    }

    /// <summary>The left side bearing of a glyph in font units.</summary>
    internal int LeftSideBearing(int glyph)
    {
        if (glyph < _numberOfHMetrics)
        {
            return I16(_hmtx + (glyph * 4) + 2);
        }

        return I16(_hmtx + (_numberOfHMetrics * 4) + ((glyph - _numberOfHMetrics) * 2));
    }

    /// <summary>The outline data of a glyph (empty for a glyph with no outline, such as a space).</summary>
    internal ReadOnlySpan<byte> GlyphData(int glyph)
    {
        if (glyph < 0 || glyph >= GlyphCount)
        {
            throw new ArgumentOutOfRangeException(nameof(glyph), glyph, $"The font has {GlyphCount} glyphs.");
        }

        (long start, long end) = LocaRange(glyph);
        return end <= start ? [] : _data.AsSpan(_glyf + (int)start, (int)(end - start));
    }

    private (long Start, long End) LocaRange(int glyph) => _longLoca
        ? (U32(_loca + (glyph * 4)), U32(_loca + ((glyph + 1) * 4)))
        : (U16(_loca + (glyph * 2)) * 2L, U16(_loca + ((glyph + 1) * 2)) * 2L);

    private void Require(string tag, int minimumLength)
    {
        if (!_tables.TryGetValue(tag, out (int Offset, int Length) table) || table.Length < minimumLength)
        {
            throw new InvalidDataException($"The font's '{tag}' table is missing or shorter than the {minimumLength} bytes it needs.");
        }
    }

    /// <summary>Checks every glyph's range in <c>glyf</c> and every composite once, so that reading a glyph later cannot fail.</summary>
    private void ValidateGlyphs()
    {
        int glyfLength = _tables["glyf"].Length;
        long previousEnd = 0;
        for (int glyph = 0; glyph < GlyphCount; glyph++)
        {
            (long start, long end) = LocaRange(glyph);
            if (end < start || end > glyfLength || (end > start && start < previousEnd))
            {
                throw new InvalidDataException($"The font's 'loca' table is not valid at glyph {glyph}: its outline is outside 'glyf' or overlaps the previous one.");
            }

            if (end > start)
            {
                previousEnd = end;
            }

            _ = Components(glyph);
        }
    }

    /// <summary>The glyphs a composite glyph is built from (none for a simple glyph).</summary>
    internal IReadOnlyList<int> Components(int glyph)
    {
        ReadOnlySpan<byte> data = GlyphData(glyph);
        List<int> components = [];
        if (data.Length < 10 || BinaryPrimitives.ReadInt16BigEndian(data) >= 0)
        {
            return components;
        }

        int at = 10;
        while (true)
        {
            if (at + 4 > data.Length)
            {
                throw new InvalidDataException($"Glyph {glyph} is a composite whose components run past its data.");
            }

            ushort flags = BinaryPrimitives.ReadUInt16BigEndian(data[at..]);
            int component = BinaryPrimitives.ReadUInt16BigEndian(data[(at + 2)..]);
            if (component >= GlyphCount)
            {
                throw new InvalidDataException($"Glyph {glyph} is a composite of glyph {component}, which the font does not have.");
            }

            components.Add(component);
            at += 4 + ((flags & 0x0001) != 0 ? 4 : 2);
            if ((flags & 0x0008) != 0)
            {
                at += 2;
            }
            else if ((flags & 0x0040) != 0)
            {
                at += 4;
            }
            else if ((flags & 0x0080) != 0)
            {
                at += 8;
            }

            if ((flags & 0x0020) == 0)
            {
                if (at > data.Length)
                {
                    throw new InvalidDataException($"Glyph {glyph} is a composite whose components run past its data.");
                }

                return components;
            }
        }
    }

    /// <summary>The raw bytes of a table, or empty when the font has none.</summary>
    internal ReadOnlySpan<byte> Table(string tag) =>
        _tables.TryGetValue(tag, out (int Offset, int Length) table) ? _data.AsSpan(table.Offset, table.Length) : [];

    private Func<int, int> BuildCmap()
    {
        int cmap = _tables["cmap"].Offset;
        int count = U16(cmap + 2);
        (int Offset, int Format)? best = null;
        int bestRank = -1;
        for (int i = 0; i < count; i++)
        {
            int platform = U16(cmap + 4 + (i * 8));
            int encoding = U16(cmap + 6 + (i * 8));
            long relative = U32(cmap + 8 + (i * 8));
            if (relative + 4 > _tables["cmap"].Length)
            {
                continue;
            }

            int offset = cmap + (int)relative;
            int format = U16(offset);
            int rank = (platform, encoding, format) switch
            {
                (3, 10, 12) => 6,
                (0, 4 or 6, 12) => 5,
                (3, 1, 4) => 4,
                (0, 3, 4) => 3,
                (0, _, 12) => 2,
                (0, _, 4) => 1,
                _ => -1,
            };
            if (rank > bestRank)
            {
                bestRank = rank;
                best = (offset, format);
            }
        }

        if (best is null)
        {
            throw new InvalidDataException("The font has no Unicode character map (cmap format 4 or 12).");
        }

        (int tableOffset, int tableFormat) = best.Value;
        Func<int, int> lookup = tableFormat == 12 ? Format12(tableOffset) : Format4(tableOffset);

        // A character map that names a glyph the font does not have means no glyph.
        return codePoint =>
        {
            int glyph = lookup(codePoint);
            return glyph >= 0 && glyph < GlyphCount ? glyph : 0;
        };
    }

    private int CmapRemaining(int offset) => _tables["cmap"].Offset + _tables["cmap"].Length - offset;

    private Func<int, int> Format12(int offset)
    {
        long declared = CmapRemaining(offset) >= 16 ? U32(offset + 12) : long.MaxValue;
        if (16 + (declared * 12) > CmapRemaining(offset))
        {
            throw new InvalidDataException("The font's character map (format 12) has more groups than its table holds.");
        }

        int groups = (int)declared;
        return codePoint =>
        {
            int low = 0;
            int high = groups - 1;
            while (low <= high)
            {
                int middle = (low + high) / 2;
                int group = offset + 16 + (middle * 12);
                uint start = U32(group);
                uint end = U32(group + 4);
                if (codePoint < start)
                {
                    high = middle - 1;
                }
                else if (codePoint > end)
                {
                    low = middle + 1;
                }
                else
                {
                    long glyph = U32(group + 8) + (long)codePoint - start;
                    return glyph is >= 0 and < int.MaxValue ? (int)glyph : 0;
                }
            }

            return 0;
        };
    }

    private Func<int, int> Format4(int offset)
    {
        int segments = U16(offset + 6) / 2;
        if (16 + (segments * 8) > CmapRemaining(offset))
        {
            throw new InvalidDataException("The font's character map (format 4) has more segments than its table holds.");
        }

        int cmapEnd = _tables["cmap"].Offset + _tables["cmap"].Length;
        int endCodes = offset + 14;
        int startCodes = endCodes + (segments * 2) + 2;
        int deltas = startCodes + (segments * 2);
        int rangeOffsets = deltas + (segments * 2);
        return codePoint =>
        {
            if (codePoint > 0xFFFF)
            {
                return 0;
            }

            // The segments are sorted by their end codes: the first whose end is not below the character is the only candidate.
            int low = 0;
            int high = segments - 1;
            int segment = -1;
            while (low <= high)
            {
                int middle = (low + high) / 2;
                if (U16(endCodes + (middle * 2)) >= codePoint)
                {
                    segment = middle;
                    high = middle - 1;
                }
                else
                {
                    low = middle + 1;
                }
            }

            if (segment >= 0)
            {
                int start = U16(startCodes + (segment * 2));
                if (codePoint < start)
                {
                    return 0;
                }

                int delta = I16(deltas + (segment * 2));
                int rangeOffset = U16(rangeOffsets + (segment * 2));
                if (rangeOffset == 0)
                {
                    return (codePoint + delta) & 0xFFFF;
                }

                int glyphAt = rangeOffsets + (segment * 2) + rangeOffset + ((codePoint - start) * 2);
                if (glyphAt + 2 > cmapEnd)
                {
                    return 0;
                }

                int glyph = U16(glyphAt);
                return glyph == 0 ? 0 : (glyph + delta) & 0xFFFF;
            }

            return 0;
        };
    }

    private (string Family, string PostScript) ReadNames()
    {
        if (!_tables.TryGetValue("name", out (int Offset, int Length) name))
        {
            return ("Font", "Font");
        }

        int count = U16(name.Offset + 2);
        int strings = name.Offset + U16(name.Offset + 4);
        string? family = null;
        string? typographicFamily = null;
        string? postScript = null;
        for (int i = 0; i < count; i++)
        {
            int record = name.Offset + 6 + (i * 12);
            int platform = U16(record);
            int nameId = U16(record + 6);
            int length = U16(record + 8);
            int offset = strings + U16(record + 10);
            if (nameId is not (1 or 6 or 16) || offset + length > name.Offset + name.Length)
            {
                continue;
            }

            string? text = platform switch
            {
                3 or 0 => Encoding.BigEndianUnicode.GetString(_data, offset, length),
                1 => Encoding.Latin1.GetString(_data, offset, length),
                _ => null,
            };
            if (text is null)
            {
                continue;
            }

            switch (nameId)
            {
                case 1:
                    family ??= text;
                    break;
                case 16:
                    typographicFamily ??= text;
                    break;
                default:
                    postScript ??= text;
                    break;
            }
        }

        string chosenFamily = typographicFamily ?? family ?? "Font";
        return (chosenFamily, PlainName(postScript ?? chosenFamily));
    }

    /// <summary>A PostScript name as a PDF name can carry it: letters, digits, hyphen, underscore and dot, at most 63 of them.</summary>
    private static string PlainName(string name)
    {
        string plain = new(name.Where(c => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_' or '.').Take(63).ToArray());
        return plain.Length == 0 ? "Font" : plain;
    }

    private string Tag(int at) => Encoding.ASCII.GetString(_data, at, 4);

    private int U16(int at) => BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(at, 2));

    private short I16(int at) => BinaryPrimitives.ReadInt16BigEndian(_data.AsSpan(at, 2));

    private uint U32(int at) => BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(at, 4));

    private int I32(int at) => BinaryPrimitives.ReadInt32BigEndian(_data.AsSpan(at, 4));
}
