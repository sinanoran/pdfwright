namespace Pdfwright.Unicode;

/// <summary>The line breaking classes of UAX #14, table 1. The order is the one the generator writes: a test pins it.</summary>
internal enum LineBreakClass : byte
{
    BK,
    CR,
    LF,
    NL,
    CM,
    SG,
    WJ,
    ZW,
    GL,
    SP,
    ZWJ,
    B2,
    BA,
    BB,
    HY,
    HH,
    CB,
    CL,
    CP,
    EX,
    IN,
    NS,
    OP,
    QU,
    IS,
    NU,
    PO,
    PR,
    SY,
    AI,
    AK,
    AL,
    AP,
    AS,
    CJ,
    EB,
    EM,
    H2,
    H3,
    HL,
    ID,
    JL,
    JV,
    JT,
    RI,
    SA,
    VF,
    VI,
    XX,
}

/// <summary>The Grapheme_Cluster_Break values of UAX #29. The order is the one the generator writes: a test pins it.</summary>
internal enum GraphemeBreakClass : byte
{
    Other,
    CR,
    LF,
    Control,
    Extend,
    ZWJ,
    RegionalIndicator,
    Prepend,
    SpacingMark,
    L,
    V,
    T,
    LV,
    LVT,
}

/// <summary>Indic_Conjunct_Break (UAX #44) as rule GB9c uses it.</summary>
internal enum ConjunctBreak : byte
{
    None,
    Linker,
    Consonant,
    Extend,
}

/// <summary>What the line and grapheme break rules ask about one code point, unpacked from <see cref="CharacterTables"/>.</summary>
internal readonly struct CharacterInfo(uint packed)
{
    // The layout the generator writes (tools/Pdfwright.UnicodeGen/PropertyDataGenerator.cs).
    internal LineBreakClass LineBreak => (LineBreakClass)(packed & 0x3F);

    internal GraphemeBreakClass GraphemeBreak => (GraphemeBreakClass)((packed >> 6) & 0xF);

    /// <summary>East Asian Width F, W or H: the <c>$EastAsian</c> of rules LB19a and LB30.</summary>
    internal bool EastAsian => (packed & (1u << 10)) != 0;

    /// <summary>General_Category Pi.</summary>
    internal bool InitialQuote => (packed & (1u << 11)) != 0;

    /// <summary>General_Category Pf.</summary>
    internal bool FinalQuote => (packed & (1u << 12)) != 0;

    /// <summary>General_Category Mn or Mc.</summary>
    internal bool IsMark => (packed & (1u << 13)) != 0;

    /// <summary>General_Category Cn: not assigned.</summary>
    internal bool IsUnassigned => (packed & (1u << 14)) != 0;

    internal bool ExtendedPictographic => (packed & (1u << 15)) != 0;

    internal ConjunctBreak Conjunct => (ConjunctBreak)((packed >> 16) & 0x3);
}

/// <summary>Lookup of the packed per-code-point properties.</summary>
internal static class CharacterProperties
{
    /// <summary>The properties of a code point; a value outside the code space has none (class XX, nothing set).</summary>
    internal static CharacterInfo Of(int codePoint)
    {
        if (codePoint is < 0 or > 0x10FFFF)
        {
            return new CharacterInfo((uint)LineBreakClass.XX);
        }

        int run = Array.BinarySearch(CharacterTables.RunStarts, codePoint);
        if (run < 0)
        {
            run = ~run - 1;
        }

        return new CharacterInfo(CharacterTables.RunValues[run]);
    }
}
