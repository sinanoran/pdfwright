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
    /// <c>text.Length + 1</c>. Index 0 is never a break (LB2) and the last index always is, mandatory (LB3).
    /// </summary>
    internal static LineBreakKind[] Breaks(ReadOnlySpan<int> text)
    {
        int count = text.Length;
        LineBreakKind[] breaks = new LineBreakKind[count + 1];
        if (count == 0)
        {
            return breaks;
        }

        // LB1 and, with LB9 and LB10, the units: a base character with the combining marks and joiners that attach to it.
        int[] unitStart = new int[count];
        LineBreakClass[] cls = new LineBreakClass[count];
        CharacterInfo[] info = new CharacterInfo[count];
        bool[] endsInJoiner = new bool[count];
        bool[] dottedCircle = new bool[count];
        LineBreakClass[] baseClass = new LineBreakClass[count];
        int units = 0;
        for (int i = 0; i < count; i++)
        {
            CharacterInfo character = CharacterProperties.Of(text[i]);
            LineBreakClass resolved = Resolve(character);
            bool attaches = resolved is LineBreakClass.CM or LineBreakClass.ZWJ
                && units > 0
                && baseClass[units - 1] is not (LineBreakClass.BK or LineBreakClass.CR or LineBreakClass.LF or LineBreakClass.NL
                    or LineBreakClass.SP or LineBreakClass.ZW or LineBreakClass.CM or LineBreakClass.ZWJ);
            if (attaches)
            {
                endsInJoiner[units - 1] = resolved == LineBreakClass.ZWJ;
                continue;
            }

            unitStart[units] = i;
            baseClass[units] = resolved;
            cls[units] = resolved is LineBreakClass.CM or LineBreakClass.ZWJ ? LineBreakClass.AL : resolved; // LB10
            info[units] = character;
            endsInJoiner[units] = resolved == LineBreakClass.ZWJ;
            dottedCircle[units] = text[i] == 0x25CC;
            units++;
        }

        // Between two units there is a break opportunity, or not; inside a unit there is never one (LB9).
        for (int k = 1; k < units; k++)
        {
            breaks[unitStart[k]] = Decide(cls, info, endsInJoiner, dottedCircle, units, k);
        }

        breaks[count] = LineBreakKind.Mandatory;
        return breaks;
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

    private static bool IsAksara(LineBreakClass[] cls, bool[] dottedCircle, int unit) =>
        cls[unit] is LineBreakClass.AK or LineBreakClass.AS || dottedCircle[unit];

    private static bool IsAksaraOrCircle(LineBreakClass[] cls, bool[] dottedCircle, int unit) =>
        cls[unit] == LineBreakClass.AK || dottedCircle[unit];

    /// <summary>The first unit going back from <paramref name="unit"/> that is not in the class <paramref name="skip"/> (-1 when there is none).</summary>
    private static int Back(LineBreakClass[] cls, int unit, LineBreakClass skip)
    {
        while (unit >= 0 && cls[unit] == skip)
        {
            unit--;
        }

        return unit;
    }

    private static LineBreakKind Decide(LineBreakClass[] cls, CharacterInfo[] info, bool[] endsInJoiner, bool[] dottedCircle, int units, int k)
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
        if (endsInJoiner[k - 1])
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
            int run = 0;
            for (int j = k - 1; j >= 0 && cls[j] == LineBreakClass.RI; j--)
            {
                run++;
            }

            if (run % 2 == 1)
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
