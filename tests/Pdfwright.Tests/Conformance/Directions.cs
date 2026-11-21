using System.Globalization;
using System.Text;
using UglyToad.PdfPig.Content;

namespace Pdfwright.Tests.Conformance;

/// <summary>
/// The geometry of text in both directions. The library writes the glyphs of a line in logical order (which is why extraction gives the text back in order) and puts
/// each at its visual place, so on a line the letters of a right-to-left run must go from right to left <i>in the order of the stream</i> and those of a left-to-right run from
/// left to right. A reader that only compares the text cannot see an x position that is mirrored inside a run, or a line that was not reordered (rule L2 of UAX 9 skipped): the text
/// would still be in logical order, and the page would show it reversed. This looks at the positions.
/// <para>
/// A letter has a direction of its own when it is a letter of a right-to-left script (Hebrew, Arabic) or of Latin, Greek or Cyrillic. Digits, punctuation and white space have none,
/// and neither have the letters of the Indic scripts and Thai, whose marks and vowels are drawn before or after the consonant they follow in the text. Marks and letters that advance
/// nothing are left out. For each two letters that follow each other in the stream (in the sequence of letters that have a direction), on one baseline, of the same direction and not
/// further apart than five em (a table cell is further), the x of the second must be less than the x of the first for right-to-left, more for left-to-right.
/// </para>
/// </summary>
internal static class Directions
{
    internal sealed record Report(int RightToLeftPairs, int LeftToRightPairs, List<string> Violations);

    /// <summary>-1 for a letter of a right-to-left script, +1 for a letter of Latin, Greek or Cyrillic, 0 for anything else (and for marks).</summary>
    internal static int Strong(string value)
    {
        if (value.Length == 0 || !Rune.TryGetRuneAt(value, 0, out Rune rune) || !Rune.IsLetter(rune))
        {
            return 0;
        }

        int cp = rune.Value;
        bool rightToLeft = cp is (>= 0x0590 and <= 0x08FF) or (>= 0xFB1D and <= 0xFDFF) or (>= 0xFE70 and <= 0xFEFF);
        bool leftToRight = cp is (>= 0x41 and <= 0x24F) or (>= 0x370 and <= 0x52F) or (>= 0x1E00 and <= 0x1FFF);
        return rightToLeft ? -1 : leftToRight ? 1 : 0;
    }

    internal static Report Check(Page page)
    {
        int rtl = 0;
        int ltr = 0;
        List<string> violations = [];
        Letter? previous = null;
        int previousDirection = 0;
        foreach (Letter letter in page.Letters)
        {
            int direction = Strong(letter.Value);
            if (direction == 0 || letter.EndBaseLine.X - letter.StartBaseLine.X < 0.01)
            {
                continue;
            }

            if (previous is not null && direction == previousDirection)
            {
                double size = Math.Max(letter.PointSize, 1);
                double step = letter.StartBaseLine.X - previous.StartBaseLine.X;
                bool sameLine = Math.Abs(letter.StartBaseLine.Y - previous.StartBaseLine.Y) <= 0.25 * size;
                if (sameLine && Math.Abs(step) <= 5 * size)
                {
                    if (direction < 0)
                    {
                        rtl++;
                    }
                    else
                    {
                        ltr++;
                    }

                    if (direction < 0 ? step >= -0.001 : step <= 0.001)
                    {
                        violations.Add(string.Create(CultureInfo.InvariantCulture, $"'{previous.Value}' (x {previous.StartBaseLine.X:0.##}) then '{letter.Value}' (x {letter.StartBaseLine.X:0.##}) on y {letter.StartBaseLine.Y:0.##}: {(direction < 0 ? "right-to-left" : "left-to-right")} letters in the wrong direction"));
                    }
                }
            }

            previous = letter;
            previousDirection = direction;
        }

        return new Report(rtl, ltr, violations);
    }
}
