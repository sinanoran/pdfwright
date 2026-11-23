using System.Globalization;
using Pdfwright.Unicode;

namespace Pdfwright.Tests.Unicode;

/// <summary>
/// The tables for printable ASCII (U+0020 to U+007E) that the line and grapheme breakers have beside the general code give exactly what the general code gives. The line breaker's version runs the same rules on the same classes
/// (<c>Decide</c> reads only the class and the properties of the characters), so by construction two characters of one kind are interchangeable; the tests hold it by brute force as well: every text of up to three characters,
/// every text of up to five characters over one representative of each distinct kind of character, and many longer random ones.
/// </summary>
public class AsciiBreakTests
{
    private const int Low = ' ';
    private const int Count = '~' - ' ' + 1;

    private static readonly int[] All = [.. Enumerable.Range(Low, Count)];

    private static string Show(ReadOnlySpan<int> text) => string.Concat(text.ToArray().Select(c => (char)c));

    private static void CheckLine(int[] text, int length)
    {
        ReadOnlySpan<int> span = text.AsSpan(0, length);
        LineBreakKind[] general = UnicodeLineBreaker.Breaks(span);

        // The array that is written to is longer than the text and holds something else, as a rented one does.
        LineBreakKind[] fast = new LineBreakKind[length + 7];
        Array.Fill(fast, LineBreakKind.Mandatory);
        UnicodeLineBreaker.BreaksOfPrintableAscii(span, fast);
        for (int i = 0; i <= length; i++)
        {
            if (general[i] != fast[i])
            {
                Assert.Fail($"Line breaks differ at {i} of \"{Show(span)}\": general {general[i]}, ascii {fast[i]}");
            }
        }
    }

    private static void CheckGrapheme(int[] text, int length)
    {
        ReadOnlySpan<int> span = text.AsSpan(0, length);
        bool[] general = UnicodeGraphemeBreaker.Boundaries(span);
        bool[] fast = new bool[length + 7];
        Array.Fill(fast, true);
        UnicodeGraphemeBreaker.BoundariesOfPrintableAscii(length, fast);
        for (int i = 0; i <= length; i++)
        {
            if (general[i] != fast[i])
            {
                Assert.Fail($"Grapheme boundaries differ at {i} of \"{Show(span)}\": general {general[i]}, ascii {fast[i]}");
            }
        }
    }

    /// <summary>Calls <paramref name="check"/> for every text of 0 to <paramref name="maxLength"/> characters over the given alphabet.</summary>
    private static void Enumerate(int[] alphabet, int maxLength, Action<int[], int> check)
    {
        int[] text = new int[maxLength];
        check(text, 0);
        for (int length = 1; length <= maxLength; length++)
        {
            int[] digits = new int[length];
            while (true)
            {
                for (int i = 0; i < length; i++)
                {
                    text[i] = alphabet[digits[i]];
                }

                check(text, length);
                int d = length - 1;
                while (d >= 0 && ++digits[d] == alphabet.Length)
                {
                    digits[d--] = 0;
                }

                if (d < 0)
                {
                    break;
                }
            }
        }
    }

    /// <summary>One character of each distinct kind: the same line break class and the same flags that any rule reads, so that the rules cannot tell two of a kind apart.</summary>
    private static int[] Representatives()
    {
        Dictionary<string, int> kinds = [];
        foreach (int c in All)
        {
            CharacterInfo i = CharacterProperties.Of(c);
            string key = $"{i.LineBreak}/{i.GraphemeBreak}/{i.EastAsian}/{i.InitialQuote}/{i.FinalQuote}/{i.IsMark}/{i.IsUnassigned}/{i.ExtendedPictographic}/{i.Conjunct}";
            kinds.TryAdd(key, c);
        }

        return [.. kinds.Values];
    }

    [Fact]
    public void Every_printable_ASCII_text_of_up_to_three_characters_breaks_as_the_general_code_breaks_it()
    {
        Enumerate(All, 3, CheckLine);
    }

    [Fact]
    public void Every_text_of_up_to_five_characters_over_one_character_of_each_kind_breaks_as_the_general_code_breaks_it()
    {
        int[] kinds = Representatives();
        Assert.InRange(kinds.Length, 10, 40);

        // Five characters over the kinds show every rule that reaches up to two characters back and two ahead the characters of all kinds in every place; the rules that look back over a run (the spaces of LB14 to LB17, the numbers of LB25) are held by the long random texts below.
        Enumerate(kinds, 5, CheckLine);
    }

    /// <summary>The whole printable ASCII range at four characters is 81 million texts, too slow to run each time (it takes minutes in Debug); it is run by hand, by name.</summary>
    [Fact(Explicit = true)]
    public void Every_printable_ASCII_text_of_four_characters_breaks_as_the_general_code_breaks_it()
    {
        Enumerate(All, 4, CheckLine);
    }

    [Fact]
    public void Long_random_texts_heavy_in_digits_punctuation_quotes_and_spaces_break_as_the_general_code_breaks_them()
    {
        string[] alphabets =
        [
            "0123456789 .,",
            "0123456789$%()[]{}-+/ .,:;",
            "'\"`  ()<>\"'.,;!?",
            "  ab-/&*_~^|\\@#=<>",
            string.Concat(All.Select(c => (char)c)),
            "aA1 -(\"'$%",
        ];
        Random random = new(20261123);
        int[] text = new int[200];
        for (int n = 0; n < 30_000; n++)
        {
            string alphabet = alphabets[random.Next(alphabets.Length)];
            int length = random.Next(0, 201);
            for (int i = 0; i < length; i++)
            {
                text[i] = alphabet[random.Next(alphabet.Length)];
            }

            CheckLine(text, length);
        }
    }

    [Fact]
    public void Every_printable_ASCII_text_of_up_to_three_characters_has_the_grapheme_boundaries_of_the_general_rules()
    {
        Enumerate(All, 3, CheckGrapheme);
    }

    [Fact]
    public void Long_random_printable_ASCII_texts_have_the_grapheme_boundaries_of_the_general_rules()
    {
        Random random = new(7);
        int[] text = new int[300];
        for (int n = 0; n < 50_000; n++)
        {
            int length = random.Next(0, 301);
            for (int i = 0; i < length; i++)
            {
                text[i] = random.Next(Low, '~' + 1);
            }

            CheckGrapheme(text, length);
        }
    }

    [Fact]
    public void The_rows_of_the_conformance_files_that_are_printable_ASCII_are_broken_as_the_files_say()
    {
        int lineRows = 0;
        int graphemeRows = 0;
        foreach (string file in new[] { "LineBreakTest.txt", "GraphemeBreakTest.txt" })
        {
            foreach (string raw in File.ReadLines(Path.Combine(TestPaths.RepositoryRoot, "data", "unicode", "18.0.0", file)))
            {
                if (raw.Length == 0 || raw[0] == '#')
                {
                    continue;
                }

                List<int> text = [];
                List<bool> expected = [];
                foreach (string token in raw.Split('#')[0].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (token is "÷" or "×")
                    {
                        expected.Add(token == "÷");
                    }
                    else
                    {
                        text.Add(int.Parse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                    }
                }

                if (text.Count == 0 || text.Any(c => c is < Low or > '~'))
                {
                    continue;
                }

                if (file == "LineBreakTest.txt")
                {
                    lineRows++;
                    LineBreakKind[] breaks = new LineBreakKind[text.Count + 1];
                    UnicodeLineBreaker.BreaksOfPrintableAscii(text.ToArray(), breaks);
                    Assert.Equal(expected, breaks.Select(b => b != LineBreakKind.None).ToList());
                }
                else
                {
                    graphemeRows++;
                    bool[] boundaries = new bool[text.Count + 1];
                    UnicodeGraphemeBreaker.BoundariesOfPrintableAscii(text.Count, boundaries);
                    Assert.Equal(expected, boundaries.ToList());
                }
            }
        }

        Assert.True(lineRows > 0 && graphemeRows > 0, "The files hold rows of printable ASCII.");
    }

    [Fact]
    public void A_character_outside_printable_ASCII_is_refused_and_a_short_array_too()
    {
        Assert.Throws<ArgumentException>(() => UnicodeLineBreaker.BreaksOfPrintableAscii([0x41, 0xE9], new LineBreakKind[3]));
        Assert.Throws<ArgumentException>(() => UnicodeLineBreaker.BreaksOfPrintableAscii([0x41, 0x0A], new LineBreakKind[3]));
        Assert.Throws<ArgumentException>(() => UnicodeLineBreaker.BreaksOfPrintableAscii([0x41, 0x42], new LineBreakKind[2]));
        Assert.Throws<ArgumentException>(() => UnicodeGraphemeBreaker.BoundariesOfPrintableAscii(2, new bool[2]));
    }
}
