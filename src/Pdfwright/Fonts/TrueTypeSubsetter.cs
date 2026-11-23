using System.Buffers.Binary;

namespace Pdfwright.Fonts;

/// <summary>
/// Cuts a TrueType font down to the glyphs a document uses, for embedding in a PDF as <c>FontFile2</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Glyph ids stay as they are.</b> A PDF's content stream names glyphs by id (a CID font with the identity encoding),
/// so the subset keeps every id the document used and the ids in between, with an empty outline for each glyph that was
/// not used, and ends at the last id used. Compression makes the run of empty glyphs nearly free, and nothing has to be
/// renumbered or remembered. Glyphs a composite glyph is built from are kept too.
/// </para>
/// <para>
/// The tables a PDF reader needs to draw (<c>head</c>, <c>hhea</c>, <c>maxp</c>, <c>hmtx</c>, <c>loca</c>, <c>glyf</c>) and the
/// ones its hinting program uses (<c>cvt </c>, <c>fpgm</c>, <c>prep</c>) are written; the character map, the names, OS/2 and
/// the layout tables are not: a PDF carries its own text mapping and widths.
/// </para>
/// </remarks>
internal static class TrueTypeSubsetter
{
    internal static byte[] Subset(TrueTypeFont font, IReadOnlyCollection<int> used)
    {
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(used);

        // The glyphs kept, as a flag for each glyph id, and the highest of them. Plain arrays rather than a sorted set and a stack of the framework: this runs once for each font of every document, and
        // generic collections of another assembly are compiled at run time on first use even in a ReadyToRun build.
        bool[] keep = new bool[Math.Max(font.GlyphCount, 1)];
        keep[0] = true;
        int highest = 0;
        int[] pending = new int[Math.Max(used.Count, 16)];
        int waiting = 0;
        foreach (int glyph in used)
        {
            Push(ref pending, ref waiting, glyph);
        }

        // The glyphs are taken from the end, as from a stack, in the order the stack of before took them (which only decides which of several glyphs outside the font is reported).
        while (waiting > 0)
        {
            int glyph = pending[--waiting];
            if (glyph < 0 || glyph >= font.GlyphCount)
            {
                throw new ArgumentOutOfRangeException(nameof(used), glyph, $"The font has {font.GlyphCount} glyphs.");
            }

            if (keep[glyph])
            {
                continue;
            }

            keep[glyph] = true;
            highest = Math.Max(highest, glyph);
            IReadOnlyList<int> components = font.Components(glyph);
            for (int c = 0; c < components.Count; c++)
            {
                Push(ref pending, ref waiting, components[c]);
            }
        }

        int count = highest + 1;

        // glyf and loca (always the long form: the offsets of a subset need no halving).
        using MemoryStream glyf = new();
        byte[] loca = new byte[(count + 1) * 4];
        for (int glyph = 0; glyph < count; glyph++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(loca.AsSpan(glyph * 4, 4), (uint)glyf.Length);
            if (keep[glyph])
            {
                ReadOnlySpan<byte> data = font.GlyphData(glyph);
                glyf.Write(data);
                while (glyf.Length % 4 != 0)
                {
                    glyf.WriteByte(0);
                }
            }
        }

        BinaryPrimitives.WriteUInt32BigEndian(loca.AsSpan(count * 4, 4), (uint)glyf.Length);

        // hmtx: a full metric for every kept glyph id, so numberOfHMetrics is the glyph count.
        byte[] hmtx = new byte[count * 4];
        for (int glyph = 0; glyph < count; glyph++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(hmtx.AsSpan(glyph * 4, 2), (ushort)font.Advance(glyph));
            BinaryPrimitives.WriteInt16BigEndian(hmtx.AsSpan((glyph * 4) + 2, 2), (short)font.LeftSideBearing(glyph));
        }

        byte[] head = font.Table("head").ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(head.AsSpan(8, 4), 0);
        BinaryPrimitives.WriteInt16BigEndian(head.AsSpan(50, 2), 1);

        byte[] hhea = font.Table("hhea").ToArray();
        BinaryPrimitives.WriteUInt16BigEndian(hhea.AsSpan(34, 2), (ushort)count);

        byte[] maxp = font.Table("maxp").ToArray();
        BinaryPrimitives.WriteUInt16BigEndian(maxp.AsSpan(4, 2), (ushort)count);

        // The tables in the order of their tags (ordinal), which the directory must follow: the copied ones that the font has, among the six that are always written. The order is known here, so it
        // is written out rather than sorted.
        List<string> tags = [];
        List<byte[]> tables = [];
        AddCopy(tags, tables, font, "cvt ");
        AddCopy(tags, tables, font, "fpgm");
        tags.AddRange(["glyf", "head", "hhea", "hmtx", "loca", "maxp"]);
        tables.AddRange([glyf.ToArray(), head, hhea, hmtx, loca, maxp]);
        AddCopy(tags, tables, font, "prep");
        return Assemble(tags, tables);
    }

    /// <summary>Adds a copy of a table that is written as it is (the hinting tables), when the font has it and it is not empty.</summary>
    private static void AddCopy(List<string> tags, List<byte[]> data, TrueTypeFont font, string tag)
    {
        ReadOnlySpan<byte> table = font.Table(tag);
        if (!table.IsEmpty)
        {
            tags.Add(tag);
            data.Add(table.ToArray());
        }
    }

    /// <summary>Writes the sfnt: the table directory in tag order (the order of <paramref name="tags"/>), each table on a four-byte boundary, and the head checksum adjustment.</summary>
    private static byte[] Assemble(List<string> tags, List<byte[]> tables)
    {
        int count = tags.Count;
        int entrySelector = 0;
        while ((1 << (entrySelector + 1)) <= count)
        {
            entrySelector++;
        }

        int searchRange = (1 << entrySelector) * 16;
        int offset = 12 + (count * 16);
        int total = offset;
        foreach (byte[] table in tables)
        {
            total += Padded(table.Length);
        }

        byte[] file = new byte[total];
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(0, 4), 0x00010000);
        BinaryPrimitives.WriteUInt16BigEndian(file.AsSpan(4, 2), (ushort)count);
        BinaryPrimitives.WriteUInt16BigEndian(file.AsSpan(6, 2), (ushort)searchRange);
        BinaryPrimitives.WriteUInt16BigEndian(file.AsSpan(8, 2), (ushort)entrySelector);
        BinaryPrimitives.WriteUInt16BigEndian(file.AsSpan(10, 2), (ushort)((count * 16) - searchRange));

        int headAt = -1;
        for (int i = 0; i < count; i++)
        {
            string tag = tags[i];
            byte[] data = tables[i];
            int record = 12 + (i * 16);
            for (int c = 0; c < 4; c++)
            {
                file[record + c] = (byte)tag[c];
            }

            BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(record + 4, 4), Checksum(data));
            BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(record + 8, 4), (uint)offset);
            BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(record + 12, 4), (uint)data.Length);
            data.CopyTo(file, offset);
            if (tag == "head")
            {
                headAt = offset;
            }

            offset += Padded(data.Length);
        }

        // The checksum adjustment makes the whole file sum to 0xB1B0AFBA.
        uint adjustment = unchecked(0xB1B0AFBA - Checksum(file));
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(headAt + 8, 4), adjustment);
        return file;
    }

    private static void Push(ref int[] stack, ref int count, int glyph)
    {
        if (count == stack.Length)
        {
            Array.Resize(ref stack, stack.Length * 2);
        }

        stack[count++] = glyph;
    }

    private static int Padded(int length) => (length + 3) & ~3;

    private static uint Checksum(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        int whole = data.Length / 4 * 4;
        for (int i = 0; i < whole; i += 4)
        {
            sum = unchecked(sum + BinaryPrimitives.ReadUInt32BigEndian(data[i..]));
        }

        if (whole < data.Length)
        {
            Span<byte> last = stackalloc byte[4];
            data[whole..].CopyTo(last);
            sum = unchecked(sum + BinaryPrimitives.ReadUInt32BigEndian(last));
        }

        return sum;
    }
}
