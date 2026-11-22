using System.Text;

namespace Pdfwright.Text;

/// <summary>
/// The one rule for what a string a caller gave may hold, applied wherever text is laid out (a paragraph, a table cell, a header, a measurement, a drawn string), so that no
/// string can make a layout throw. The caller's own strings are never changed: the rule is applied when the text is laid out.
/// </summary>
internal static class TextNormalizer
{
    /// <summary>U+2028 LINE SEPARATOR, the one hard line break the layout works with.</summary>
    internal const char LineSeparator = (char)0x2028;

    /// <summary>U+FFFD REPLACEMENT CHARACTER, what a surrogate without its pair becomes.</summary>
    internal const char Replacement = (char)0xFFFD;

    /// <summary>Whether <paramref name="c"/> ends a line: a line feed, a carriage return, a next line (U+0085), a vertical tab, a form feed, or a line or paragraph separator (U+2028, U+2029).</summary>
    internal static bool IsLineBreak(char c) => c is '\n' or '\r' or (char)0x0B or (char)0x0C or (char)0x85 or (char)0x2028 or (char)0x2029;

    /// <summary>Whether <paramref name="c"/> is a control character that is dropped: a C0 or C1 control (and U+007F DELETE) that is not a tab and not a line break.</summary>
    internal static bool IsDroppedControl(char c) => !IsLineBreak(c) && c != '\t' && (c < 0x20 || (c >= 0x7F && c <= 0x9F));

    /// <summary>
    /// Text as it is laid out. Every line break (U+000A, U+000D, a U+000D U+000A pair as one, U+0085, U+000B, U+000C, U+2028, U+2029) becomes one U+2028, a hard line break inside the
    /// same block (a paragraph separator does not start a new block). Every other control character (C0, U+007F, C1; U+0000 too) is removed; a tab is kept (the layout sets it as a space).
    /// A surrogate without its pair becomes U+FFFD. Everything else (including unassigned and private use code points, joiners and U+FEFF) is kept as it is.
    /// </summary>
    internal static string Normalize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        int first = 0;
        while (first < text.Length && IsPlain(text, first))
        {
            first++;
        }

        if (first == text.Length)
        {
            return text;
        }

        StringBuilder result = new(text.Length);
        result.Append(text, 0, first);
        for (int i = first; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                continue; // the line feed after it makes the break
            }

            if (IsLineBreak(c))
            {
                result.Append(LineSeparator);
            }
            else if (IsDroppedControl(c))
            {
                // removed
            }
            else if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                result.Append(c).Append(text[++i]);
            }
            else if (char.IsSurrogate(c))
            {
                result.Append(Replacement);
            }
            else
            {
                result.Append(c);
            }
        }

        return result.ToString();
    }

    /// <summary>
    /// Text for the metadata of the file (title, subject, author, creator, keywords, language): a line break or a tab becomes one space, every other control is removed, a surrogate
    /// without its pair becomes U+FFFD, and the characters XML 1.0 forbids (U+FFFE, U+FFFF) are removed, so that the Info dictionary and the XMP packet say the same and the packet is well formed.
    /// </summary>
    internal static string Metadata(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        StringBuilder result = new(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                continue;
            }

            if (IsLineBreak(c) || c == '\t')
            {
                result.Append(' ');
            }
            else if (IsDroppedControl(c) || c is (char)0xFFFE or (char)0xFFFF)
            {
                // removed
            }
            else if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                result.Append(c).Append(text[++i]);
            }
            else if (char.IsSurrogate(c))
            {
                result.Append(Replacement);
            }
            else
            {
                result.Append(c);
            }
        }

        return result.ToString();
    }

    private static bool IsPlain(string text, int index)
    {
        char c = text[index];
        return (c >= 0x20 && c < 0x7F) || (c >= 0xA0 && !char.IsSurrogate(c) && c != (char)0x2029) || c == '\t';
    }
}
