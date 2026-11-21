using System.Globalization;
using System.Text;
using UglyToad.PdfPig.Content;

namespace Pdfwright.Tests.Conformance;

/// <summary>
/// What a reader found on a page and how it is compared with what the document was given.
/// <para>
/// <b>The text.</b> <see cref="Read(Page)"/> makes the text of a page from PdfPig's letters in content-stream order and puts a marker between two letters where the
/// geometry shows a break: <see cref="Line"/> where the next letter is on another baseline (the layout broke a line, or a new block starts), and <see cref="Gap"/>
/// where it is on the same baseline but a visible space away (the next table cell, or the jump between two directional runs).
/// </para>
/// <para>
/// <b>The comparison.</b> <see cref="Normalize(string)"/> removes U+034F COMBINING GRAPHEME JOINER and turns every run of white space into one space; nothing else (the
/// right-to-left mark U+200F and the zero-width non-joiner are kept, and so is the order of combining marks). Each expected string must then be found <i>whole</i>,
/// in order, one after the other: a space of the string is a space, a line break or a gap of the page; a character other than a space may be followed by a line break
/// only where a grapheme cluster ends (the line of a Thai text breaks between clusters, not inside one), and by a gap only inside a string (the jump between runs);
/// a plain extra space inside a string, a missing space and a character that moved into the next string or the next table cell all fail. Before and after a string there must be a
/// space, a line break, a gap or the end of the page, which is what makes a moved character visible. A table's header, drawn again at the top of a later page, stands between
/// strings and is not one of them.
/// </para>
/// </summary>
internal static class Extraction
{
    /// <summary>The next letter is on another line (a private-use character, which no corpus text contains).</summary>
    internal const char Line = (char)0xE000;

    /// <summary>The next letter is on the same line, a visible space away from the one before (a private-use character).</summary>
    internal const char Gap = (char)0xE001;

    internal sealed record Result(int Total, int Passed, int HeaderRepeats, string Trailing, List<string> Failures);

    /// <summary>The text of a page in content-stream order with <see cref="Line"/> and <see cref="Gap"/> where the geometry has them.</summary>
    internal static string Read(Page page)
    {
        StringBuilder text = new();
        Letter? previous = null;
        foreach (Letter letter in page.Letters)
        {
            bool mark = IsMark(letter);
            if (!mark && previous is not null)
            {
                double size = Math.Max(letter.PointSize, 1);
                if (Math.Abs(letter.StartBaseLine.Y - previous.StartBaseLine.Y) > 0.25 * size)
                {
                    _ = text.Append(Line);
                }
                else if (Distance(previous, letter) > 0.45 * size)
                {
                    _ = text.Append(Gap);
                }
            }

            _ = text.Append(letter.Value);
            previous = mark ? previous : letter;
        }

        return text.ToString();
    }

    /// <summary>A mark, or any letter that advances nothing: it sits above or below its base, so it says nothing about the line or the gap.</summary>
    internal static bool IsMark(Letter letter) =>
        letter.EndBaseLine.X - letter.StartBaseLine.X < 0.01 || (letter.Value.Length > 0 && Rune.TryGetRuneAt(letter.Value, 0, out Rune rune) && Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark);

    /// <summary>The empty space between the extents of two letters on a line, whichever is on the left.</summary>
    private static double Distance(Letter a, Letter b)
    {
        double aLeft = Math.Min(a.StartBaseLine.X, a.EndBaseLine.X);
        double aRight = Math.Max(a.StartBaseLine.X, a.EndBaseLine.X);
        double bLeft = Math.Min(b.StartBaseLine.X, b.EndBaseLine.X);
        double bRight = Math.Max(b.StartBaseLine.X, b.EndBaseLine.X);
        return bLeft >= aRight ? bLeft - aRight : bRight <= aLeft ? aLeft - bRight : 0;
    }

    private static bool IsSeparator(char c) => char.IsWhiteSpace(c) || c is Line or Gap;

    /// <summary>U+034F removed, every run of white space one space (the markers are kept, and a run that holds one is the marker, not a space).</summary>
    internal static string Normalize(string text)
    {
        StringBuilder result = new(text.Length);
        bool pending = false;
        foreach (char c in text)
        {
            if (c == (char)0x034F)
            {
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                pending = true;
                continue;
            }

            if (pending && result.Length > 0)
            {
                _ = result.Append(' ');
            }

            pending = false;
            _ = result.Append(c);
        }

        return result.ToString();
    }

    /// <summary>Whether the string <paramref name="e"/> (normalised) is the text at <paramref name="position"/>; on success the position is behind it and its separators.</summary>
    private static bool TryString(string text, ref int position, string e)
    {
        if (e.Length == 0)
        {
            return true;
        }

        int[] clusters = StringInfo.ParseCombiningCharacters(e);
        HashSet<int> boundaries = [.. clusters];
        int q = position;
        while (q < text.Length && IsSeparator(text[q]))
        {
            q++;
        }

        for (int i = 0; i < e.Length; i++)
        {
            int run = q;
            bool space = false;
            bool line = false;
            bool gap = false;
            while (run < text.Length && IsSeparator(text[run]))
            {
                space |= char.IsWhiteSpace(text[run]) && text[run] is not (Line or Gap);
                line |= text[run] == Line;
                gap |= text[run] == Gap;
                run++;
            }

            if (e[i] == ' ')
            {
                if (run == q)
                {
                    return false;
                }

                q = run;
                continue;
            }

            if (run != q)
            {
                // A separator where the string has none: only a break the layout made (between grapheme clusters) or a jump between runs.
                bool allowed = i > 0 && !space && (!line || boundaries.Contains(i)) && (line || gap);
                if (!allowed)
                {
                    return false;
                }

                q = run;
            }

            if (q >= text.Length || text[q] != e[i])
            {
                return false;
            }

            q++;
        }

        if (q < text.Length && !IsSeparator(text[q]))
        {
            return false;
        }

        while (q < text.Length && IsSeparator(text[q]))
        {
            q++;
        }

        position = q;
        return true;
    }

    internal static Result Match(string extracted, IReadOnlyList<string> expected, string[] header)
    {
        string text = Normalize(extracted);
        string[] headerCells = [.. header.Select(Normalize)];
        int position = 0;
        int passed = 0;
        int repeats = 0;
        List<string> failures = [];

        bool TryHeader(ref int at)
        {
            if (headerCells.Length == 0)
            {
                return false;
            }

            int p = at;
            foreach (string cell in headerCells)
            {
                if (!TryString(text, ref p, cell))
                {
                    return false;
                }
            }

            at = p;
            return true;
        }

        for (int index = 0; index < expected.Count; index++)
        {
            string e = Normalize(expected[index]);

            // The header of a table is drawn again at the top of a later page: it stands between rows and is not one of the strings.
            int probe = position;
            while (!TryString(text, ref probe, e) && TryHeader(ref position))
            {
                probe = position;
                repeats++;
            }

            probe = position;
            if (TryString(text, ref probe, e))
            {
                position = probe;
                passed++;
                continue;
            }

            string got = text.Substring(position, Math.Min(e.Length + 8, text.Length - position));
            failures.Add($"expected {Describe(e)} but read {Describe(got)}");

            // Stay in step: find the string further on, or the next one, so that one bad string is one failure.
            bool found = false;
            for (int start = position + 1; start < text.Length && !found; start++)
            {
                int at = start;
                if (TryString(text, ref at, e))
                {
                    position = at;
                    found = true;
                }
            }

            if (!found && index + 1 < expected.Count)
            {
                string next = Normalize(expected[index + 1]);
                for (int start = position; start < text.Length; start++)
                {
                    int at = start;
                    if (TryString(text, ref at, next))
                    {
                        position = start;
                        break;
                    }
                }
            }
        }

        while (TryHeader(ref position))
        {
            repeats++;
        }

        string trailing = string.Concat(text[Math.Min(position, text.Length)..].Where(c => c is not (Line or Gap))).Trim();
        return new Result(expected.Count, passed, repeats, trailing, failures);
    }

    internal static string Describe(string text) =>
        text.Length > 60 ? Describe(text[..60]) + "..." : string.Join(' ', text.EnumerateRunes().Select(r => r.Value.ToString("X4", CultureInfo.InvariantCulture))) + " \"" + text + "\"";
}
