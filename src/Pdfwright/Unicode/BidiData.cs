namespace Pdfwright.Unicode;

/// <summary>The bidirectional character types of UAX #9, table 4. The order is the one the generator writes: a test pins it.</summary>
internal enum BidiClass : byte
{
    L,
    R,
    AL,
    EN,
    ES,
    ET,
    AN,
    CS,
    NSM,
    BN,
    B,
    S,
    WS,
    ON,
    LRE,
    LRO,
    RLE,
    RLO,
    PDF,
    LRI,
    RLI,
    FSI,
    PDI,
}

/// <summary>Whether a character is a paired bracket and which half (<c>Bidi_Paired_Bracket_Type</c>).</summary>
internal enum BracketType : byte
{
    None = 0,
    Open = 1,
    Close = 2,
}

/// <summary>Lookups over the generated tables of <see cref="BidiTables"/>: class, paired bracket, canonical equivalent, mirror.</summary>
internal static class BidiData
{
    /// <summary>The bidirectional class of a code point; a value outside the code space (and a lone surrogate) is <see cref="BidiClass.L"/>.</summary>
    internal static BidiClass ClassOf(int codePoint)
    {
        if (codePoint is < 0 or > 0x10FFFF)
        {
            return BidiClass.L;
        }

        int run = Array.BinarySearch(BidiTables.RunStarts, codePoint);
        if (run < 0)
        {
            run = ~run - 1;
        }

        return (BidiClass)BidiTables.RunClasses[run];
    }

    internal static BracketType BracketTypeOf(int codePoint)
    {
        int at = Array.BinarySearch(BidiTables.BracketCodes, codePoint);
        return at < 0 ? BracketType.None : (BracketType)BidiTables.BracketTypes[at];
    }

    /// <summary>The bracket that closes an opening bracket (or opens a closing one), or the character itself when it is not a bracket.</summary>
    internal static int PairedBracket(int codePoint)
    {
        int at = Array.BinarySearch(BidiTables.BracketCodes, codePoint);
        return at < 0 ? codePoint : BidiTables.BracketPairs[at];
    }

    /// <summary>The character a bracket is canonically equivalent to (U+2329 is U+3008), or itself: brackets are matched by this (BD16).</summary>
    internal static int CanonicalBracket(int codePoint)
    {
        int at = Array.IndexOf(BidiTables.EquivalentFrom, codePoint);
        return at < 0 ? codePoint : BidiTables.EquivalentTo[at];
    }

    /// <summary>The mirror image of a character that has one (<c>(</c> and <c>)</c>), or the character itself.</summary>
    internal static int Mirror(int codePoint)
    {
        int at = Array.BinarySearch(BidiTables.MirrorCodes, codePoint);
        return at < 0 ? codePoint : BidiTables.MirrorImages[at];
    }
}
