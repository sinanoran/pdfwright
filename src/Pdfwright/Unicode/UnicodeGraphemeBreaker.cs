namespace Pdfwright.Unicode;

/// <summary>
/// Extended grapheme cluster boundaries (UAX #29, Unicode 18.0, section 3.1.1: rules GB1 to GB999): the text units a person counts as one
/// character, which a line may not split and a cursor steps over. Held to <c>GraphemeBreakTest.txt</c> by the tests.
/// </summary>
internal static class UnicodeGraphemeBreaker
{
    /// <summary>
    /// Whether a boundary lies before each character of <paramref name="text"/> (code points) and after the last: an array of
    /// <c>text.Length + 1</c>. Index 0 and the last index are always boundaries (GB1, GB2) for text that is not empty.
    /// </summary>
    internal static bool[] Boundaries(ReadOnlySpan<int> text)
    {
        bool[] boundaries = new bool[text.Length + 1];
        Boundaries(text, boundaries);
        return boundaries;
    }

    /// <summary>The same, written into the first <c>text.Length + 1</c> entries of <paramref name="boundaries"/>, which may be longer and hold anything (every one of those entries is set).</summary>
    internal static void Boundaries(ReadOnlySpan<int> text, bool[] boundaries)
    {
        int count = text.Length;
        if (boundaries.Length < count + 1)
        {
            throw new ArgumentException("The array must have room for one entry more than the text has code points.", nameof(boundaries));
        }

        Array.Clear(boundaries, 0, count + 1);
        if (count == 0)
        {
            return;
        }

        // Rented (see ScratchArray: from the shared pool up to 4,096 elements, allocated above that) and given back; every entry up to count is written before it is read.
        CharacterInfo[] info = ScratchArray.Rent<CharacterInfo>(count);
        try
        {
            for (int i = 0; i < count; i++)
            {
                info[i] = CharacterProperties.Of(text[i]);
            }

            boundaries[0] = true;
            boundaries[count] = true;
            int regionalRun = 0;
            for (int i = 1; i < count; i++)
            {
                GraphemeBreakClass l = info[i - 1].GraphemeBreak;
                GraphemeBreakClass r = info[i].GraphemeBreak;
                regionalRun = l == GraphemeBreakClass.RegionalIndicator ? regionalRun + 1 : 0;
                boundaries[i] = IsBoundary(info, i, l, r, regionalRun);
            }
        }
        finally
        {
            ScratchArray.Return(info);
        }
    }

    private static bool IsBoundary(CharacterInfo[] info, int i, GraphemeBreakClass l, GraphemeBreakClass r, int regionalRun)
    {
        // GB3 to GB5
        if (l == GraphemeBreakClass.CR && r == GraphemeBreakClass.LF)
        {
            return false;
        }

        if (l is GraphemeBreakClass.Control or GraphemeBreakClass.CR or GraphemeBreakClass.LF
            || r is GraphemeBreakClass.Control or GraphemeBreakClass.CR or GraphemeBreakClass.LF)
        {
            return true;
        }

        // GB6 to GB8: Hangul syllable sequences
        if ((l == GraphemeBreakClass.L && r is GraphemeBreakClass.L or GraphemeBreakClass.V or GraphemeBreakClass.LV or GraphemeBreakClass.LVT)
            || (l is GraphemeBreakClass.LV or GraphemeBreakClass.V && r is GraphemeBreakClass.V or GraphemeBreakClass.T)
            || (l is GraphemeBreakClass.LVT or GraphemeBreakClass.T && r == GraphemeBreakClass.T))
        {
            return false;
        }

        // GB9, GB9a, GB9b
        if (r is GraphemeBreakClass.Extend or GraphemeBreakClass.ZWJ or GraphemeBreakClass.SpacingMark || l == GraphemeBreakClass.Prepend)
        {
            return false;
        }

        // GB9c: Linker Extend* x Consonant (a virama and the marks after it join the next consonant)
        if (info[i].Conjunct == ConjunctBreak.Consonant && IsConjunctBefore(info, i))
        {
            return false;
        }

        // GB11: an emoji ZWJ sequence
        if (l == GraphemeBreakClass.ZWJ && info[i].ExtendedPictographic)
        {
            int j = i - 2;
            while (j >= 0 && info[j].GraphemeBreak == GraphemeBreakClass.Extend)
            {
                j--;
            }

            if (j >= 0 && info[j].ExtendedPictographic)
            {
                return false;
            }
        }

        // GB12, GB13: regional indicators pair up
        if (l == GraphemeBreakClass.RegionalIndicator && r == GraphemeBreakClass.RegionalIndicator && regionalRun % 2 == 1)
        {
            return false;
        }

        // GB999
        return true;
    }

    private static bool IsConjunctBefore(CharacterInfo[] info, int i)
    {
        int j = i - 1;
        while (j >= 0 && info[j].Conjunct == ConjunctBreak.Extend)
        {
            j--;
        }

        return j >= 0 && info[j].Conjunct == ConjunctBreak.Linker;
    }
}
