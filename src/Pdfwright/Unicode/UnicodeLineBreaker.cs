namespace Pdfwright.Unicode;

/// <summary>What the line breaking algorithm says about the boundary before a character.</summary>
internal enum LineBreakKind : byte
{
    /// <summary>A line must not break here.</summary>
    None,

    /// <summary>A line may break here.</summary>
    Allowed,

    /// <summary>A line must break here (a hard line break).</summary>
    Mandatory,
}

/// <summary>
/// The Unicode Line Breaking Algorithm (UAX #14, Unicode 18.0, revision 57) with its default behaviour and no tailoring: where a line
/// of text may break and where it must. Written from the rules and held to <c>LineBreakTest.txt</c> by the tests.
/// </summary>
internal static class UnicodeLineBreaker
{
    /// <summary>
    /// The break opportunity before each character of <paramref name="text"/> (given as code points) and after the last: an array of
    /// <c>text.Length + 1</c>. Index 0 is never a break (LB2) and the last index always is, mandatory (LB3), for text that is not empty.
    /// </summary>
    internal static LineBreakKind[] Breaks(ReadOnlySpan<int> text)
    {
        LineBreakKind[] breaks = new LineBreakKind[text.Length + 1];
        Breaks(text, breaks);
        return breaks;
    }

    /// <summary>The same, written into the first <c>text.Length + 1</c> entries of <paramref name="breaks"/>, which may be longer and hold anything (every one of those entries is set).</summary>
    internal static void Breaks(ReadOnlySpan<int> text, LineBreakKind[] breaks)
    {
        int count = text.Length;
        if (breaks.Length < count + 1)
        {
            throw new ArgumentException("The array must have room for one entry more than the text has code points.", nameof(breaks));
        }

        Array.Clear(breaks, 0, count + 1);
        if (count == 0)
        {
            return;
        }

        // LB1 and, with LB9 and LB10, the units: a base character with the combining marks and joiners that attach to it.
        // The working arrays are rented (see ScratchArray) and given back: they hold the units of this call and nothing after it. Up to 4,096 elements they come from the shared pool, which may keep them until the
        // runtime trims it; for a longer text they are allocated and left to the garbage collector.
        // An entry is written when its unit is made, before anything reads it, so the contents a rented array arrives with do not matter.
        int[] unitStart = ScratchArray.Rent<int>(count);
        LineBreakClass[] cls = ScratchArray.Rent<LineBreakClass>(count);
        CharacterInfo[] info = ScratchArray.Rent<CharacterInfo>(count);
        bool[] endsInJoiner = ScratchArray.Rent<bool>(count);
        bool[] dottedCircle = ScratchArray.Rent<bool>(count);
        int[] runOfRegionalIndicators = ScratchArray.Rent<int>(count);
        LineBreakClass[] baseClass = ScratchArray.Rent<LineBreakClass>(count);
        try
        {
            FindBreaks(text, breaks, unitStart, cls, info, endsInJoiner, dottedCircle, runOfRegionalIndicators, baseClass);
        }
        finally
        {
            ScratchArray.Return(unitStart);
            ScratchArray.Return(cls);
            ScratchArray.Return(info);
            ScratchArray.Return(endsInJoiner);
            ScratchArray.Return(dottedCircle);
            ScratchArray.Return(runOfRegionalIndicators);
            ScratchArray.Return(baseClass);
        }
    }

    /// <summary>The first character of the printable ASCII range, which <see cref="AsciiClasses"/> and <see cref="AsciiInfo"/> start at.</summary>
    private const int FirstAscii = ' ';

    /// <summary>The number of printable ASCII characters, U+0020 to U+007E.</summary>
    private const int AsciiCount = '~' - ' ' + 1;

    // The line break class (after LB1) and the properties of each printable ASCII character, made by the same lookup and the same resolution that the general code applies to every character. Static initializers, so that they are made once and safely for any number of threads.
    private static readonly LineBreakClass[] AsciiClasses = MakeAsciiClasses();

    private static readonly CharacterInfo[] AsciiInfo = MakeAsciiInfo();

    private static LineBreakClass[] MakeAsciiClasses()
    {
        LineBreakClass[] classes = new LineBreakClass[AsciiCount];
        for (int i = 0; i < AsciiCount; i++)
        {
            classes[i] = Resolve(CharacterProperties.Of(FirstAscii + i));
        }

        return classes;
    }

    private static CharacterInfo[] MakeAsciiInfo()
    {
        CharacterInfo[] info = new CharacterInfo[AsciiCount];
        for (int i = 0; i < AsciiCount; i++)
        {
            info[i] = CharacterProperties.Of(FirstAscii + i);
        }

        return info;
    }

    /// <summary>
    /// The same as <see cref="Breaks(ReadOnlySpan{int}, LineBreakKind[])"/> for a text of printable ASCII only (U+0020 to U+007E; <see cref="ArgumentException"/> for anything else). Such a text has no combining mark or joiner, so every character is a unit of its
    /// own, and no regional indicator, aksara or dotted circle, so the rules that ask about those never match. The classes and properties come from a table made once instead of a lookup for each character and the units need no arrays of their own,
    /// but the decision for every boundary is made by the very same code (<c>Decide</c>) as in the general case, which is why the two cannot differ; the tests compare them on every printable ASCII text of up to three characters, on every text of up to five characters over one character of each kind, and on 30,000 random longer ones (and, run by hand, on every printable ASCII text of four characters).
    /// </summary>
    internal static void BreaksOfPrintableAscii(ReadOnlySpan<int> text, LineBreakKind[] breaks)
    {
        int count = text.Length;
        if (breaks.Length < count + 1)
        {
            throw new ArgumentException("The array must have room for one entry more than the text has code points.", nameof(breaks));
        }

        Array.Clear(breaks, 0, count + 1);
        if (count == 0)
        {
            return;
        }

        LineBreakClass[] cls = ScratchArray.Rent<LineBreakClass>(count);
        CharacterInfo[] info = ScratchArray.Rent<CharacterInfo>(count);
        try
        {
            for (int i = 0; i < count; i++)
            {
                int index = text[i] - FirstAscii;
                if ((uint)index >= AsciiCount)
                {
                    throw new ArgumentException("The text must be printable ASCII.", nameof(text));
                }

                cls[i] = AsciiClasses[index];
                info[i] = AsciiInfo[index];
            }

            for (int k = 1; k < count; k++)
            {
                breaks[k] = Decide(cls, info, null, null, null, count, k);
            }

            breaks[count] = LineBreakKind.Mandatory;
        }
        finally
        {
            ScratchArray.Return(cls);
            ScratchArray.Return(info);
        }
    }

    private static void FindBreaks(ReadOnlySpan<int> text, LineBreakKind[] breaks, int[] unitStart, LineBreakClass[] cls, CharacterInfo[] info, bool[] endsInJoiner, bool[] dottedCircle, int[] runOfRegionalIndicators, LineBreakClass[] baseClass)
    {
        int count = text.Length;
        int units = 0;
        for (int i = 0; i < count; i++)
        {
            CharacterInfo character = CharacterProperties.Of(text[i]);
            LineBreakClass resolved = Resolve(character);
            bool attaches = resolved is LineBreakClass.CM or LineBreakClass.ZWJ
                && units > 0
                && baseClass[units - 1] is not (LineBreakClass.BK or LineBreakClass.CR or LineBreakClass.LF or LineBreakClass.NL
                    or LineBreakClass.SP or LineBreakClass.ZW);
            if (attaches)
            {
                endsInJoiner[units - 1] = resolved == LineBreakClass.ZWJ;
                continue;
            }

            // LB10: a combining mark or joiner that attached to nothing is an ordinary letter, with the properties of U+0041.
            bool leftover = resolved is LineBreakClass.CM or LineBreakClass.ZWJ;
            unitStart[units] = i;
            cls[units] = leftover ? LineBreakClass.AL : resolved;
            baseClass[units] = cls[units];
            info[units] = leftover ? CharacterProperties.Of(0x41) : character;
            endsInJoiner[units] = resolved == LineBreakClass.ZWJ;
            dottedCircle[units] = text[i] == 0x25CC;
            runOfRegionalIndicators[units] = cls[units] == LineBreakClass.RI && units > 0 ? runOfRegionalIndicators[units - 1] + 1 : cls[units] == LineBreakClass.RI ? 1 : 0;
            units++;
        }

        // Between two units there is a break opportunity, or not; inside a unit there is never one (LB9).
        for (int k = 1; k < units; k++)
        {
            breaks[unitStart[k]] = Decide(cls, info, endsInJoiner, dottedCircle, runOfRegionalIndicators, units, k);
        }

        breaks[count] = LineBreakKind.Mandatory;
    }

    private static LineBreakClass Resolve(CharacterInfo character) => character.LineBreak switch
    {
        LineBreakClass.AI or LineBreakClass.SG or LineBreakClass.XX => LineBreakClass.AL,
        LineBreakClass.SA => character.IsMark ? LineBreakClass.CM : LineBreakClass.AL,
        LineBreakClass.CJ => LineBreakClass.NS,
        LineBreakClass other => other,
    };

    private static bool In(LineBreakClass value, LineBreakClass a, LineBreakClass b) => value == a || value == b;

    private static bool In(LineBreakClass value, LineBreakClass a, LineBreakClass b, LineBreakClass c) => value == a || value == b || value == c;

    private static bool IsAksara(LineBreakClass[] cls, bool[]? dottedCircle, int unit) =>
        cls[unit] is LineBreakClass.AK or LineBreakClass.AS || (dottedCircle is not null && dottedCircle[unit]);

    private static bool IsAksaraOrCircle(LineBreakClass[] cls, bool[]? dottedCircle, int unit) =>
        cls[unit] == LineBreakClass.AK || (dottedCircle is not null && dottedCircle[unit]);

    /// <summary>The first unit going back from <paramref name="unit"/> that is not in the class <paramref name="skip"/> (-1 when there is none).</summary>
    private static int Back(LineBreakClass[] cls, int unit, LineBreakClass skip)
    {
        while (unit >= 0 && cls[unit] == skip)
        {
            unit--;
        }

        return unit;
    }

    /// <summary>The decision for the boundary before unit <paramref name="k"/>. A <see langword="null"/> <paramref name="endsInJoiner"/> or <paramref name="dottedCircle"/> means that no unit has the flag, and a <see langword="null"/> <paramref name="runOfRegionalIndicators"/> that there is no regional indicator, so that the rule that reads it is never reached.</summary>
    private static LineBreakKind Decide(LineBreakClass[] cls, CharacterInfo[] info, bool[]? endsInJoiner, bool[]? dottedCircle, int[]? runOfRegionalIndicators, int units, int k)
    {
        LineBreakClass l = cls[k - 1];
        LineBreakClass r = cls[k];
        bool hasNext = k + 1 < units;
        LineBreakClass next = hasNext ? cls[k + 1] : LineBreakClass.XX;

        // LB4 to LB7
        if (l == LineBreakClass.BK)
        {
            return LineBreakKind.Mandatory;
        }

        if (l == LineBreakClass.CR && r == LineBreakClass.LF)
        {
            return LineBreakKind.None;
        }

        if (l is LineBreakClass.CR or LineBreakClass.LF or LineBreakClass.NL)
        {
            return LineBreakKind.Mandatory;
        }

        if (r is LineBreakClass.BK or LineBreakClass.CR or LineBreakClass.LF or LineBreakClass.NL or LineBreakClass.SP or LineBreakClass.ZW)
        {
            return LineBreakKind.None;
        }

        // LB8: a break after a zero width space, and the spaces after it
        int beforeSpaces = Back(cls, k - 1, LineBreakClass.SP);
        if (beforeSpaces >= 0 && cls[beforeSpaces] == LineBreakClass.ZW)
        {
            return LineBreakKind.Allowed;
        }

        // LB8a
        if (endsInJoiner is not null && endsInJoiner[k - 1])
        {
            return LineBreakKind.None;
        }

        // LB11, LB12, LB12a, LB13
        if (l == LineBreakClass.WJ || r == LineBreakClass.WJ || l == LineBreakClass.GL)
        {
            return LineBreakKind.None;
        }

        if (r == LineBreakClass.GL && l is not (LineBreakClass.SP or LineBreakClass.HY or LineBreakClass.HH))
        {
            return LineBreakKind.None;
        }

        if (r is LineBreakClass.CL or LineBreakClass.CP or LineBreakClass.EX or LineBreakClass.SY)
        {
            return LineBreakKind.None;
        }

        // LB14: OP SP* ×
        if (beforeSpaces >= 0 && cls[beforeSpaces] == LineBreakClass.OP)
        {
            return LineBreakKind.None;
        }

        // LB15a: (sot | BK | CR | LF | NL | OP | QU | GL | SP | ZW) [Pi & QU] SP* ×
        if (beforeSpaces >= 0 && cls[beforeSpaces] == LineBreakClass.QU && info[beforeSpaces].InitialQuote)
        {
            int before = beforeSpaces - 1;
            if (before < 0 || cls[before] is LineBreakClass.BK or LineBreakClass.CR or LineBreakClass.LF or LineBreakClass.NL
                or LineBreakClass.OP or LineBreakClass.QU or LineBreakClass.GL or LineBreakClass.SP or LineBreakClass.ZW)
            {
                return LineBreakKind.None;
            }
        }

        // LB15b: × [Pf & QU] ( SP | GL | WJ | CL | QU | CP | EX | IS | SY | BK | CR | LF | NL | ZW | eot )
        if (r == LineBreakClass.QU && info[k].FinalQuote
            && (!hasNext || next is LineBreakClass.SP or LineBreakClass.GL or LineBreakClass.WJ or LineBreakClass.CL or LineBreakClass.QU
                or LineBreakClass.CP or LineBreakClass.EX or LineBreakClass.IS or LineBreakClass.SY or LineBreakClass.BK
                or LineBreakClass.CR or LineBreakClass.LF or LineBreakClass.NL or LineBreakClass.ZW))
        {
            return LineBreakKind.None;
        }

        // LB15c, LB15d
        if (l == LineBreakClass.SP && r == LineBreakClass.IS && next == LineBreakClass.NU)
        {
            return LineBreakKind.Allowed;
        }

        if (r == LineBreakClass.IS)
        {
            return LineBreakKind.None;
        }

        // LB16: (CL | CP) SP* × NS
        if (r == LineBreakClass.NS && beforeSpaces >= 0 && cls[beforeSpaces] is LineBreakClass.CL or LineBreakClass.CP)
        {
            return LineBreakKind.None;
        }

        // LB17: B2 SP* × B2
        if (r == LineBreakClass.B2 && beforeSpaces >= 0 && cls[beforeSpaces] == LineBreakClass.B2)
        {
            return LineBreakKind.None;
        }

        // LB18
        if (l == LineBreakClass.SP)
        {
            return LineBreakKind.Allowed;
        }

        // LB19
        if (r == LineBreakClass.QU && !info[k].InitialQuote)
        {
            return LineBreakKind.None;
        }

        if (l == LineBreakClass.QU && !info[k - 1].FinalQuote)
        {
            return LineBreakKind.None;
        }

        // LB19a
        if (r == LineBreakClass.QU && (!info[k - 1].EastAsian || !hasNext || !info[k + 1].EastAsian))
        {
            return LineBreakKind.None;
        }

        if (l == LineBreakClass.QU && (!info[k].EastAsian || k < 2 || !info[k - 2].EastAsian))
        {
            return LineBreakKind.None;
        }

        // LB20
        if (r == LineBreakClass.CB || l == LineBreakClass.CB)
        {
            return LineBreakKind.Allowed;
        }

        // LB20a: (sot | BK | CR | LF | NL | SP | ZW | CB | GL) (HY | HH) × (AL | HL)
        if (In(l, LineBreakClass.HY, LineBreakClass.HH) && In(r, LineBreakClass.AL, LineBreakClass.HL)
            && (k < 2 || cls[k - 2] is LineBreakClass.BK or LineBreakClass.CR or LineBreakClass.LF or LineBreakClass.NL or LineBreakClass.SP
                or LineBreakClass.ZW or LineBreakClass.CB or LineBreakClass.GL))
        {
            return LineBreakKind.None;
        }

        // LB21, LB21a, LB21b
        if (r is LineBreakClass.BA or LineBreakClass.HH or LineBreakClass.HY or LineBreakClass.NS || l == LineBreakClass.BB)
        {
            return LineBreakKind.None;
        }

        if (In(l, LineBreakClass.HY, LineBreakClass.HH) && k >= 2 && cls[k - 2] == LineBreakClass.HL && r != LineBreakClass.HL)
        {
            return LineBreakKind.None;
        }

        if (l == LineBreakClass.SY && r == LineBreakClass.HL)
        {
            return LineBreakKind.None;
        }

        // LB22, LB23, LB23a, LB24
        if (r == LineBreakClass.IN)
        {
            return LineBreakKind.None;
        }

        if ((In(l, LineBreakClass.AL, LineBreakClass.HL) && r == LineBreakClass.NU) || (l == LineBreakClass.NU && In(r, LineBreakClass.AL, LineBreakClass.HL)))
        {
            return LineBreakKind.None;
        }

        if ((l == LineBreakClass.PR && In(r, LineBreakClass.ID, LineBreakClass.EB, LineBreakClass.EM))
            || (In(l, LineBreakClass.ID, LineBreakClass.EB, LineBreakClass.EM) && r == LineBreakClass.PO))
        {
            return LineBreakKind.None;
        }

        if ((In(l, LineBreakClass.PR, LineBreakClass.PO) && In(r, LineBreakClass.AL, LineBreakClass.HL))
            || (In(l, LineBreakClass.AL, LineBreakClass.HL) && In(r, LineBreakClass.PR, LineBreakClass.PO)))
        {
            return LineBreakKind.None;
        }

        // LB25
        if ((In(r, LineBreakClass.PO, LineBreakClass.PR) && IsNumberBefore(cls, k, closeAllowed: true))
            || (r == LineBreakClass.NU && IsNumberBefore(cls, k, closeAllowed: false)))
        {
            return LineBreakKind.None;
        }

        if (In(l, LineBreakClass.PO, LineBreakClass.PR))
        {
            if (r == LineBreakClass.NU
                || (r == LineBreakClass.OP && (next == LineBreakClass.NU || (next == LineBreakClass.IS && k + 2 < units && cls[k + 2] == LineBreakClass.NU))))
            {
                return LineBreakKind.None;
            }
        }

        if ((l == LineBreakClass.HY || l == LineBreakClass.IS) && r == LineBreakClass.NU)
        {
            return LineBreakKind.None;
        }

        // LB26, LB27, LB28
        if ((l == LineBreakClass.JL && r is LineBreakClass.JL or LineBreakClass.JV or LineBreakClass.H2 or LineBreakClass.H3)
            || (In(l, LineBreakClass.JV, LineBreakClass.H2) && In(r, LineBreakClass.JV, LineBreakClass.JT))
            || (In(l, LineBreakClass.JT, LineBreakClass.H3) && r == LineBreakClass.JT))
        {
            return LineBreakKind.None;
        }

        bool lHangul = l is LineBreakClass.JL or LineBreakClass.JV or LineBreakClass.JT or LineBreakClass.H2 or LineBreakClass.H3;
        bool rHangul = r is LineBreakClass.JL or LineBreakClass.JV or LineBreakClass.JT or LineBreakClass.H2 or LineBreakClass.H3;
        if ((lHangul && r == LineBreakClass.PO) || (l == LineBreakClass.PR && rHangul))
        {
            return LineBreakKind.None;
        }

        if (In(l, LineBreakClass.AL, LineBreakClass.HL) && In(r, LineBreakClass.AL, LineBreakClass.HL))
        {
            return LineBreakKind.None;
        }

        // LB28a (Brahmic scripts): AP × (AK | ◌ | AS); (AK | ◌ | AS) × (VF | VI); (AK | ◌ | AS) VI × (AK | ◌); (AK | ◌ | AS) × (AK | ◌ | AS) VF
        bool lAksara = IsAksara(cls, dottedCircle, k - 1);
        bool rAksara = IsAksara(cls, dottedCircle, k);
        if (l == LineBreakClass.AP && rAksara)
        {
            return LineBreakKind.None;
        }

        if (lAksara && In(r, LineBreakClass.VF, LineBreakClass.VI))
        {
            return LineBreakKind.None;
        }

        if (l == LineBreakClass.VI && k >= 2 && IsAksara(cls, dottedCircle, k - 2) && IsAksaraOrCircle(cls, dottedCircle, k))
        {
            return LineBreakKind.None;
        }

        if (lAksara && rAksara && next == LineBreakClass.VF)
        {
            return LineBreakKind.None;
        }

        // LB29, LB30
        if (l == LineBreakClass.IS && In(r, LineBreakClass.AL, LineBreakClass.HL))
        {
            return LineBreakKind.None;
        }

        if (In(l, LineBreakClass.AL, LineBreakClass.HL, LineBreakClass.NU) && r == LineBreakClass.OP && !info[k].EastAsian)
        {
            return LineBreakKind.None;
        }

        if (l == LineBreakClass.CP && !info[k - 1].EastAsian && In(r, LineBreakClass.AL, LineBreakClass.HL, LineBreakClass.NU))
        {
            return LineBreakKind.None;
        }

        // LB30a: regional indicators pair up
        if (l == LineBreakClass.RI && r == LineBreakClass.RI)
        {
            if (runOfRegionalIndicators![k - 1] % 2 == 1)
            {
                return LineBreakKind.None;
            }
        }

        // LB30b
        if (r == LineBreakClass.EM && (l == LineBreakClass.EB || (info[k - 1].ExtendedPictographic && info[k - 1].IsUnassigned)))
        {
            return LineBreakKind.None;
        }

        // LB31
        return LineBreakKind.Allowed;
    }

    /// <summary>Whether the units before the boundary <paramref name="k"/> are <c>NU (SY | IS)*</c>, or <c>NU (SY | IS)* (CL | CP)</c> when <paramref name="closeAllowed"/> (rule LB25).</summary>
    private static bool IsNumberBefore(LineBreakClass[] cls, int k, bool closeAllowed)
    {
        int j = k - 1;
        if (closeAllowed && cls[j] is LineBreakClass.CL or LineBreakClass.CP)
        {
            j--;
        }

        while (j >= 0 && cls[j] is LineBreakClass.SY or LineBreakClass.IS)
        {
            j--;
        }

        return j >= 0 && cls[j] == LineBreakClass.NU;
    }
}
