using System.Buffers.Binary;
using System.Text;

namespace Pdfwright.Tests;

/// <summary>
/// The two facts of a TrueType file the tests need and nothing else reads yet: the OS/2 embedding permission
/// (<c>fsType</c>) and the name table's copyright line. The real font parser is slice S2 of the plan.
/// </summary>
internal static class TrueTypeInfo
{
    /// <summary>The OS/2 <c>fsType</c> value, or null when the font has no OS/2 table.</summary>
    internal static ushort? EmbeddingPermission(byte[] font)
    {
        int? table = FindTable(font, "OS/2");
        if (table is null)
        {
            return null;
        }

        // OS/2: version (2), xAvgCharWidth (2), usWeightClass (2), usWidthClass (2), fsType (2).
        return BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(table.Value + 8, 2));
    }

    /// <summary>
    /// Whether the font may be embedded as a subset, which is what Pdfwright does: fsType 0 (installable), 4 (preview
    /// and print) or 8 (editable). Bit 1 (restricted licence), bit 8 (no subsetting) and bit 9 (bitmap embedding only)
    /// forbid it.
    /// </summary>
    internal static bool MayEmbed(ushort fsType) => (fsType & 0x0100) == 0 && (fsType & 0x0200) == 0 && ((fsType & 0x0002) == 0 || (fsType & 0x000C) != 0);

    internal static string? Copyright(byte[] font)
    {
        int? table = FindTable(font, "name");
        if (table is null)
        {
            return null;
        }

        ushort count = BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(table.Value + 2, 2));
        ushort stringOffset = BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(table.Value + 4, 2));
        for (int i = 0; i < count; i++)
        {
            int record = table.Value + 6 + (i * 12);
            ushort platform = BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(record, 2));
            ushort nameId = BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(record + 6, 2));
            if (platform == 3 && nameId == 0)
            {
                ushort length = BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(record + 8, 2));
                ushort offset = BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(record + 10, 2));
                return Encoding.BigEndianUnicode.GetString(font, table.Value + stringOffset + offset, length);
            }
        }

        return null;
    }

    private static int? FindTable(byte[] font, string tag)
    {
        ushort tables = BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(4, 2));
        for (int i = 0; i < tables; i++)
        {
            int record = 12 + (i * 16);
            if (Encoding.ASCII.GetString(font, record, 4) == tag)
            {
                return (int)BinaryPrimitives.ReadUInt32BigEndian(font.AsSpan(record + 8, 4));
            }
        }

        return null;
    }
}
