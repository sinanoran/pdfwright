using System.Buffers.Binary;

namespace Pdfwright.Text;

/// <summary>
/// The part of the check for word-by-word shaping that the shaping engine's own list of glyphs cannot do: a rule that matches "every glyph that is not listed in a class definition" (class 0) can match the space glyph
/// without the space being in any set that <c>hb_ot_layout_lookup_collect_glyphs</c> reports, because HarfBuzz reports only the glyphs a class definition lists. Two kinds of subtable do this:
/// a pair adjustment of format 2 (kerning by class) whose second class is 0 for the space, with a value in the column of that class, and a context or chaining context rule of format 2 (class based) that has the space's class in a
/// backtrack, input (after the first glyph, which must be in the coverage) or lookahead position. This reads the GSUB or GPOS table itself, from its bytes, follows the extension lookups (GSUB type 7, GPOS type 9) to the real subtables,
/// and refuses the font in either case. It also looks at every coverage table that a lookup names, as a second line beside the engine's list. A table that is cut short or points out of itself is refused.
/// </summary>
/// <remarks>
/// What each kind of subtable can match without the space being named, as concluded for this check: GSUB single, multiple, alternate and ligature substitutions, and GPOS single, cursive, mark-to-base, mark-to-ligature and
/// mark-to-mark positioning, match glyphs of a coverage table, a list, or ligature components, all named (the coverages are checked here too); GPOS pair adjustment of format 1 matches the second glyph by name; format 2 is the
/// hole described above. Context and chaining context of format 1 name their glyphs and format 3 names a coverage for each position, so they are named; format 2 is the hole. Reverse chaining substitution names coverages. Lookups that
/// skip glyph classes (marks, ligatures, bases) are answered by the base-glyph rule of the shaper, not here.
/// </remarks>
internal static class OpenTypeSpaceCheck
{
    /// <summary>
    /// <see langword="null"/> when the table (<c>GSUB</c> when <paramref name="positioning"/> is false, else <c>GPOS</c>) has no subtable that can match <paramref name="space"/> unnamed, else the reason, in words.
    /// </summary>
    internal static string? Refusal(byte[] table, bool positioning, int space)
    {
        string name = positioning ? "GPOS" : "GSUB";
        try
        {
            return new Reader(table).CheckTable(positioning, name, space);
        }
        catch (Exception e) when (e is FormatException or ArgumentOutOfRangeException or IndexOutOfRangeException or OverflowException)
        {
            return $"the {name} table is malformed ({e.Message})";
        }
    }

    private sealed class Reader(byte[] data)
    {
        private int U16(int at) => at < 0 || at + 2 > data.Length ? throw new FormatException("a read is outside the table") : BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(at));

        private int U32(int at) => at < 0 || at + 4 > data.Length ? throw new FormatException("a read is outside the table") : checked((int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at)));

        internal string? CheckTable(bool gpos, string name, int space)
        {
            if (data.Length == 0)
            {
                return null;
            }

            if (U16(0) != 1)
            {
                return $"the {name} table has a major version that is not 1";
            }

            int lookupListOffset = U16(8);
            if (lookupListOffset == 0)
            {
                return null;
            }

            int lookupList = lookupListOffset;
            int lookupCount = U16(lookupList);
            for (int l = 0; l < lookupCount; l++)
            {
                int lookup = lookupList + U16(lookupList + 2 + (2 * l));
                int type = U16(lookup);
                int subCount = U16(lookup + 4);
                for (int s = 0; s < subCount; s++)
                {
                    int sub = lookup + U16(lookup + 6 + (2 * s));
                    int subType = type;
                    if (type == (gpos ? 9 : 7))
                    {
                        if (U16(sub) != 1)
                        {
                            throw new FormatException("an extension subtable has a format that is not 1");
                        }

                        subType = U16(sub + 2);
                        if (subType == type)
                        {
                            throw new FormatException("an extension subtable names itself");
                        }

                        sub += U32(sub + 4);
                    }

                    string? reason = CheckSubtable(gpos, subType, sub, space);
                    if (reason is not null)
                    {
                        return $"{reason} ({name} lookup {l}, subtable {s})";
                    }
                }
            }

            return null;
        }

        private string? CheckSubtable(bool gpos, int type, int sub, int space)
        {
            int format = U16(sub);
            if (gpos)
            {
                switch (type)
                {
                    case 1: // single
                    case 3: // cursive
                        return CoverageHas(sub + U16(sub + 2), space) ? "the space glyph is in a coverage" : null;
                    case 2: // pair
                        if (CoverageHas(sub + U16(sub + 2), space))
                        {
                            return "the space glyph is in a coverage";
                        }

                        return format == 2 ? CheckPairClasses(sub, space) : null;
                    case 4: // mark to base
                    case 5: // mark to ligature
                    case 6: // mark to mark
                        return CoverageHas(sub + U16(sub + 2), space) || CoverageHas(sub + U16(sub + 4), space) ? "the space glyph is in a coverage" : null;
                    case 7: // context
                        return CheckContext(sub, format, space);
                    case 8: // chaining context
                        return CheckChain(sub, format, space);
                    default:
                        throw new FormatException($"a lookup has the unknown type {type}");
                }
            }

            switch (type)
            {
                case 1: // single
                case 2: // multiple
                case 3: // alternate
                case 4: // ligature
                    return CoverageHas(sub + U16(sub + 2), space) ? "the space glyph is in a coverage" : null;
                case 5:
                    return CheckContext(sub, format, space);
                case 6:
                    return CheckChain(sub, format, space);
                case 8: // reverse chaining
                    return CheckReverse(sub, space);
                default:
                    throw new FormatException($"a lookup has the unknown type {type}");
            }
        }

        /// <summary>Pair adjustment of format 2: a value in the column of the space's class 2 is applied to the first glyph when the space follows it, which pairs shaped word by word would not see.</summary>
        private string? CheckPairClasses(int sub, int space)
        {
            int valueFormat1 = U16(sub + 4);
            int valueFormat2 = U16(sub + 6);
            int classDef2 = U16(sub + 10);
            int class1Count = U16(sub + 12);
            int class2Count = U16(sub + 14);
            int spaceClass = ClassOf(classDef2 == 0 ? -1 : sub + classDef2, space);
            if (spaceClass >= class2Count)
            {
                throw new FormatException("a class is beyond the class count");
            }

            int recordSize = 2 * (System.Numerics.BitOperations.PopCount((uint)(valueFormat1 & 0xFF)) + System.Numerics.BitOperations.PopCount((uint)(valueFormat2 & 0xFF)));
            for (int c1 = 0; c1 < class1Count; c1++)
            {
                int record = sub + 16 + (((c1 * class2Count) + spaceClass) * recordSize);
                for (int b = 0; b < recordSize; b += 2)
                {
                    if (U16(record + b) != 0)
                    {
                        return $"a pair adjustment of format 2 has a value against the class {spaceClass} of the space glyph";
                    }
                }
            }

            return null;
        }

        private string? CheckContext(int sub, int format, int space)
        {
            switch (format)
            {
                case 1:
                    return null; // glyph sequences: every glyph is named
                case 2:
                {
                    int classDef = U16(sub + 4);
                    int spaceClass = ClassOf(classDef == 0 ? -1 : sub + classDef, space);
                    int setCount = U16(sub + 6);
                    for (int i = 0; i < setCount; i++)
                    {
                        int setOffset = U16(sub + 8 + (2 * i));
                        if (setOffset == 0)
                        {
                            continue;
                        }

                        int set = sub + setOffset;
                        int ruleCount = U16(set);
                        for (int r = 0; r < ruleCount; r++)
                        {
                            int rule = set + U16(set + 2 + (2 * r));
                            int glyphCount = U16(rule);
                            if (glyphCount == 0)
                            {
                                throw new FormatException("a rule has no glyphs");
                            }

                            if (HasClass(rule + 4, glyphCount - 1, spaceClass))
                            {
                                return $"a context rule of format 2 has the class {spaceClass} of the space glyph after its first glyph";
                            }
                        }
                    }

                    return null;
                }

                case 3:
                {
                    int glyphCount = U16(sub + 2);
                    for (int i = 0; i < glyphCount; i++)
                    {
                        if (CoverageHas(sub + U16(sub + 6 + (2 * i)), space))
                        {
                            return "the space glyph is in a coverage";
                        }
                    }

                    return null;
                }

                default:
                    throw new FormatException($"a context subtable has the unknown format {format}");
            }
        }

        private string? CheckChain(int sub, int format, int space)
        {
            switch (format)
            {
                case 1:
                    return null;
                case 2:
                {
                    int backtrackDef = U16(sub + 4);
                    int inputDef = U16(sub + 6);
                    int lookaheadDef = U16(sub + 8);
                    int backtrackSpace = ClassOf(backtrackDef == 0 ? -1 : sub + backtrackDef, space);
                    int inputSpace = ClassOf(inputDef == 0 ? -1 : sub + inputDef, space);
                    int lookaheadSpace = ClassOf(lookaheadDef == 0 ? -1 : sub + lookaheadDef, space);
                    int setCount = U16(sub + 10);
                    for (int i = 0; i < setCount; i++)
                    {
                        int setOffset = U16(sub + 12 + (2 * i));
                        if (setOffset == 0)
                        {
                            continue;
                        }

                        int set = sub + setOffset;
                        int ruleCount = U16(set);
                        for (int r = 0; r < ruleCount; r++)
                        {
                            int at = set + U16(set + 2 + (2 * r));
                            int backtrack = U16(at);
                            if (HasClass(at + 2, backtrack, backtrackSpace))
                            {
                                return $"a chaining context rule of format 2 has the class {backtrackSpace} of the space glyph in its backtrack";
                            }

                            at += 2 + (2 * backtrack);
                            int input = U16(at);
                            if (input == 0)
                            {
                                throw new FormatException("a rule has no input glyphs");
                            }

                            if (HasClass(at + 2, input - 1, inputSpace))
                            {
                                return $"a chaining context rule of format 2 has the class {inputSpace} of the space glyph in its input";
                            }

                            at += 2 + (2 * (input - 1));
                            int lookahead = U16(at);
                            if (HasClass(at + 2, lookahead, lookaheadSpace))
                            {
                                return $"a chaining context rule of format 2 has the class {lookaheadSpace} of the space glyph in its lookahead";
                            }
                        }
                    }

                    return null;
                }

                case 3:
                {
                    int at = sub + 2;
                    for (int part = 0; part < 3; part++)
                    {
                        int count = U16(at);
                        for (int i = 0; i < count; i++)
                        {
                            if (CoverageHas(sub + U16(at + 2 + (2 * i)), space))
                            {
                                return "the space glyph is in a coverage";
                            }
                        }

                        at += 2 + (2 * count);
                    }

                    return null;
                }

                default:
                    throw new FormatException($"a chaining context subtable has the unknown format {format}");
            }
        }

        private string? CheckReverse(int sub, int space)
        {
            if (CoverageHas(sub + U16(sub + 2), space))
            {
                return "the space glyph is in a coverage";
            }

            int at = sub + 4;
            for (int part = 0; part < 2; part++)
            {
                int count = U16(at);
                for (int i = 0; i < count; i++)
                {
                    if (CoverageHas(sub + U16(at + 2 + (2 * i)), space))
                    {
                        return "the space glyph is in a coverage";
                    }
                }

                at += 2 + (2 * count);
            }

            return null;
        }

        /// <summary>Whether any of <paramref name="count"/> classes (16-bit values from <paramref name="at"/>) is <paramref name="spaceClass"/>.</summary>
        private bool HasClass(int at, int count, int spaceClass)
        {
            for (int i = 0; i < count; i++)
            {
                if (U16(at + (2 * i)) == spaceClass)
                {
                    return true;
                }
            }

            return false;
        }

        private bool CoverageHas(int at, int glyph)
        {
            int format = U16(at);
            int count = U16(at + 2);
            if (format == 1)
            {
                for (int i = 0; i < count; i++)
                {
                    if (U16(at + 4 + (2 * i)) == glyph)
                    {
                        return true;
                    }
                }

                return false;
            }

            if (format == 2)
            {
                for (int i = 0; i < count; i++)
                {
                    int record = at + 4 + (6 * i);
                    if (glyph >= U16(record) && glyph <= U16(record + 2))
                    {
                        return true;
                    }
                }

                return false;
            }

            throw new FormatException($"a coverage has the unknown format {format}");
        }

        /// <summary>The class of a glyph in a class definition at <paramref name="at"/>, or 0 for a glyph it does not list (and for no class definition at all, <paramref name="at"/> below 0).</summary>
        private int ClassOf(int at, int glyph)
        {
            if (at < 0)
            {
                return 0;
            }

            int format = U16(at);
            if (format == 1)
            {
                int first = U16(at + 2);
                int count = U16(at + 4);
                int index = glyph - first;
                return index >= 0 && index < count ? U16(at + 6 + (2 * index)) : 0;
            }

            if (format == 2)
            {
                int count = U16(at + 2);
                for (int i = 0; i < count; i++)
                {
                    int record = at + 4 + (6 * i);
                    if (glyph >= U16(record) && glyph <= U16(record + 2))
                    {
                        return U16(record + 4);
                    }
                }

                return 0;
            }

            throw new FormatException($"a class definition has the unknown format {format}");
        }
    }
}
