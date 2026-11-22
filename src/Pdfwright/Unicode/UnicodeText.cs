namespace Pdfwright.Unicode;

/// <summary>Conversion between the UTF-16 strings of .NET (and the cluster indices HarfBuzz reports) and the code points the Unicode algorithms take.</summary>
internal static class UnicodeText
{
    /// <summary>
    /// The code points of <paramref name="text"/>, and for each the index in <paramref name="text"/> where its UTF-16 code units start;
    /// the array of indices has one more entry than the code points, holding <c>text.Length</c>, so that the span of a code point or of a
    /// run of them is always <c>[index[i], index[j])</c>. A surrogate that has no partner becomes U+FFFD, as .NET's own decoders do.
    /// </summary>
    internal static int[] CodePoints(ReadOnlySpan<char> text, out int[] utf16Index)
    {
        int pairs = 0;
        for (int i = 0; i + 1 < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && char.IsLowSurrogate(text[i + 1]))
            {
                pairs++;
                i++;
            }
        }

        int[] codePoints = new int[text.Length - pairs];
        utf16Index = new int[codePoints.Length + 1];
        int n = 0;
        for (int i = 0; i < text.Length; n++)
        {
            utf16Index[n] = i;
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                codePoints[n] = char.ConvertToUtf32(text[i], text[i + 1]);
                i += 2;
            }
            else
            {
                codePoints[n] = char.IsSurrogate(text[i]) ? 0xFFFD : text[i];
                i++;
            }
        }

        utf16Index[n] = text.Length;
        return codePoints;
    }
}
