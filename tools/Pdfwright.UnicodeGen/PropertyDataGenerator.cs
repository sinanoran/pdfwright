using System.Globalization;
using System.Text;

namespace Pdfwright.UnicodeGen;

/// <summary>
/// Makes <c>Pdfwright.Unicode.CharacterTables</c>: for every code point one 32-bit value that packs the line break class, the
/// grapheme cluster break class and the few properties the line and grapheme break rules ask about. Runs of equal values are
/// stored once, so the table is a few thousand entries. The layout is documented in <c>CharacterProperties</c> and pinned by a test.
/// </summary>
public static class PropertyDataGenerator
{
    /// <summary>The order is the order of <c>LineBreakClass</c> in the library: a test pins it.</summary>
    internal static readonly string[] LineBreakNames =
    [
        "BK", "CR", "LF", "NL", "CM", "SG", "WJ", "ZW", "GL", "SP", "ZWJ", "B2", "BA", "BB", "HY", "HH", "CB", "CL", "CP", "EX",
        "IN", "NS", "OP", "QU", "IS", "NU", "PO", "PR", "SY", "AI", "AK", "AL", "AP", "AS", "CJ", "EB", "EM", "H2", "H3", "HL",
        "ID", "JL", "JV", "JT", "RI", "SA", "VF", "VI", "XX",
    ];

    /// <summary>The order is the order of <c>GraphemeBreakClass</c> in the library: a test pins it.</summary>
    internal static readonly string[] GraphemeNames =
        ["Other", "CR", "LF", "Control", "Extend", "ZWJ", "Regional_Indicator", "Prepend", "SpacingMark", "L", "V", "T", "LV", "LVT"];

    // Bit layout of a value: bits 0-5 line break class, 6-9 grapheme class, then the flags below.
    private const int GraphemeShift = 6;
    private const uint EastAsianWide = 1u << 10;
    private const uint InitialQuote = 1u << 11;
    private const uint FinalQuote = 1u << 12;
    private const uint Mark = 1u << 13;
    private const uint Unassigned = 1u << 14;
    private const uint ExtendedPictographic = 1u << 15;
    private const int ConjunctShift = 16;

    /// <summary>The text of <c>CharacterTables.g.cs</c> for the files in <paramref name="dataDirectory"/>, with LF line ends.</summary>
    public static string Generate(string dataDirectory)
    {
        uint[] values = new uint[0x110000];

        // Line break class: the file's default (XX), then every listed range.
        int unknown = Array.IndexOf(LineBreakNames, "XX");
        Array.Fill(values, (uint)unknown);
        Apply(values, Path.Combine(dataDirectory, "LineBreak.txt"), name => (uint)IndexOf(LineBreakNames, name), 0x3Fu, 1);

        Apply(values, Path.Combine(dataDirectory, "GraphemeBreakProperty.txt"), name => (uint)IndexOf(GraphemeNames, name) << GraphemeShift, 0xFu << GraphemeShift, 1);

        // East Asian Width: F, W and H are "East Asian" for rules LB19a and LB30.
        foreach (string[] fields in BidiDataGenerator.DataLines(Path.Combine(dataDirectory, "EastAsianWidth.txt")))
        {
            if (fields[1] is "F" or "W" or "H")
            {
                SetFlag(values, fields[0], EastAsianWide);
            }
        }

        foreach (string[] fields in BidiDataGenerator.DataLines(Path.Combine(dataDirectory, "emoji-data.txt")))
        {
            if (fields[1] == "Extended_Pictographic")
            {
                SetFlag(values, fields[0], ExtendedPictographic);
            }
        }

        // General category: marks, initial and final quotes, and which code points are assigned at all.
        bool[] assigned = new bool[0x110000];
        int rangeStart = -1;
        foreach (string[] fields in BidiDataGenerator.DataLines(Path.Combine(dataDirectory, "UnicodeData.txt")))
        {
            int cp = BidiDataGenerator.Hex(fields[0]);
            string name = fields[1];
            string category = fields[2];
            int last = cp;
            if (name.EndsWith(", First>", StringComparison.Ordinal))
            {
                rangeStart = cp;
                continue;
            }

            if (name.EndsWith(", Last>", StringComparison.Ordinal))
            {
                cp = rangeStart;
            }

            for (int c = cp; c <= last; c++)
            {
                assigned[c] = true;
                if (category is "Mn" or "Mc")
                {
                    values[c] |= Mark;
                }
                else if (category == "Pi")
                {
                    values[c] |= InitialQuote;
                }
                else if (category == "Pf")
                {
                    values[c] |= FinalQuote;
                }
            }
        }

        for (int c = 0; c < assigned.Length; c++)
        {
            if (!assigned[c])
            {
                values[c] |= Unassigned;
            }
        }

        // Indic_Conjunct_Break: Linker 1, Consonant 2, Extend 3 (rule GB9c).
        foreach (string[] fields in BidiDataGenerator.DataLines(Path.Combine(dataDirectory, "DerivedCoreProperties.txt")))
        {
            if (fields.Length >= 3 && fields[1] == "InCB")
            {
                uint kind = fields[2] switch
                {
                    "Linker" => 1u,
                    "Consonant" => 2u,
                    "Extend" => 3u,
                    _ => throw new InvalidDataException($"Unknown Indic_Conjunct_Break value '{fields[2]}'."),
                };
                (int first, int last) = Range(fields[0]);
                for (int cp = first; cp <= last; cp++)
                {
                    if ((values[cp] >> ConjunctShift & 3) != 0)
                    {
                        throw new InvalidDataException($"U+{cp:X4} has two Indic_Conjunct_Break values.");
                    }
                }

                SetFlag(values, fields[0], kind << ConjunctShift);
            }
        }

        List<int> starts = [];
        List<uint> runValues = [];
        for (int cp = 0; cp < values.Length; cp++)
        {
            if (cp == 0 || values[cp] != values[cp - 1])
            {
                starts.Add(cp);
                runValues.Add(values[cp]);
            }
        }

        StringBuilder output = new();
        output.Append("// <auto-generated>\n");
        output.Append("// Generated by tools/Pdfwright.UnicodeGen from the Unicode Character Database 18.0.0 files in data/unicode/18.0.0.\n");
        output.Append("// Do not edit: change the generator and run it again. A test regenerates this file and compares.\n");
        output.Append("// </auto-generated>\n");
        output.Append("namespace Pdfwright.Unicode;\n\n");
        output.Append("internal static class CharacterTables\n{\n");
        output.Append("    /// <summary>The first code point of each run of equal packed values, in order; a run lasts until the next start.</summary>\n");
        BidiDataGenerator.AppendArray(output, "int", "RunStarts", starts.Select(s => s.ToString(CultureInfo.InvariantCulture)));
        output.Append("\n    /// <summary>The packed value of each run: see <c>CharacterProperties</c> for the layout.</summary>\n");
        BidiDataGenerator.AppendArray(output, "uint", "RunValues", runValues.Select(v => "0x" + v.ToString("X", CultureInfo.InvariantCulture)));
        output.Append("}\n");
        return output.ToString();
    }

    private static int IndexOf(string[] names, string name)
    {
        int index = Array.IndexOf(names, name);
        return index >= 0 ? index : throw new InvalidDataException($"Unknown property value '{name}'.");
    }

    /// <summary>Sets a field of the packed value for each listed range: <paramref name="mask"/> is cleared, then the value is put in.</summary>
    private static void Apply(uint[] values, string path, Func<string, uint> encode, uint mask, int nameField)
    {
        foreach (string[] fields in BidiDataGenerator.DataLines(path))
        {
            (int first, int last) = Range(fields[0]);
            uint encoded = encode(fields[nameField]);
            for (int cp = first; cp <= last; cp++)
            {
                values[cp] = (values[cp] & ~mask) | encoded;
            }
        }
    }

    private static void SetFlag(uint[] values, string range, uint flag)
    {
        (int first, int last) = Range(range);
        for (int cp = first; cp <= last; cp++)
        {
            values[cp] |= flag;
        }
    }

    private static (int First, int Last) Range(string text)
    {
        string[] ends = text.Split("..");
        int first = BidiDataGenerator.Hex(ends[0]);
        return (first, ends.Length > 1 ? BidiDataGenerator.Hex(ends[1]) : first);
    }
}
