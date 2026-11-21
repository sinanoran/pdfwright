using System.Globalization;
using Pdfwright.Unicode;
using Pdfwright.UnicodeGen;

namespace Pdfwright.Tests.Unicode;

/// <summary>
/// The Unicode Consortium's own conformance files for line breaking (UAX #14) and grapheme clusters (UAX #29), run in full: every row,
/// none skipped, and the number of rows asserted exactly so that a parser that drops some cannot pass.
/// </summary>
public class BreakConformanceTests
{
    private static string DataFile(string name) => Path.Combine(TestPaths.RepositoryRoot, "data", "unicode", "18.0.0", name);

    /// <summary>A test row: the code points and, for each boundary (one more than the characters), whether the file says a break lies there.</summary>
    private static IEnumerable<(int[] Text, bool[] Break, string Line)> Rows(string file)
    {
        foreach (string raw in File.ReadLines(DataFile(file)))
        {
            if (raw.Length == 0 || raw[0] == '#')
            {
                continue;
            }

            string line = raw.Split('#')[0].Trim();
            List<int> text = [];
            List<bool> breaks = [];
            foreach (string token in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (token == "÷")
                {
                    breaks.Add(true);
                }
                else if (token == "×")
                {
                    breaks.Add(false);
                }
                else
                {
                    text.Add(int.Parse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                }
            }

            yield return ([.. text], [.. breaks], line);
        }
    }

    [Fact]
    public void LineBreakTest_txt_passes_on_every_row()
    {
        int rows = 0;
        List<string> failures = [];
        foreach ((int[] text, bool[] expected, string line) in Rows("LineBreakTest.txt"))
        {
            rows++;
            LineBreakKind[] breaks = UnicodeLineBreaker.Breaks(text);
            bool[] actual = [.. breaks.Select(b => b != LineBreakKind.None)];
            if (!actual.SequenceEqual(expected))
            {
                failures.Add($"{line} (got {string.Join(' ', actual.Select(b => b ? "÷" : "×"))})");
            }
        }

        Assert.Equal(19346, rows);
        Assert.True(failures.Count == 0, $"{failures.Count} of {rows} rows failed; the first: {string.Join(" | ", failures.Take(8))}");
    }

    [Fact]
    public void GraphemeBreakTest_txt_passes_on_every_row()
    {
        int rows = 0;
        List<string> failures = [];
        foreach ((int[] text, bool[] expected, string line) in Rows("GraphemeBreakTest.txt"))
        {
            rows++;
            bool[] actual = UnicodeGraphemeBreaker.Boundaries(text);
            if (!actual.SequenceEqual(expected))
            {
                failures.Add($"{line} (got {string.Join(' ', actual.Select(b => b ? "÷" : "×"))})");
            }
        }

        Assert.Equal(853, rows);
        Assert.True(failures.Count == 0, $"{failures.Count} of {rows} rows failed; the first: {string.Join(" | ", failures.Take(8))}");
    }

    [Fact]
    public void The_generated_character_tables_are_what_the_generator_makes_of_the_data()
    {
        string committed = File.ReadAllText(Path.Combine(TestPaths.RepositoryRoot, "src", "Pdfwright", "Unicode", "CharacterTables.g.cs")).Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Equal(PropertyDataGenerator.Generate(Path.Combine(TestPaths.RepositoryRoot, "data", "unicode", "18.0.0")), committed);
    }

    [Theory]
    [InlineData(0x000A, (int)LineBreakClass.LF)]
    [InlineData(0x0020, (int)LineBreakClass.SP)]
    [InlineData(0x0028, (int)LineBreakClass.OP)]
    [InlineData(0x0029, (int)LineBreakClass.CP)]
    [InlineData(0x0041, (int)LineBreakClass.AL)]
    [InlineData(0x0030, (int)LineBreakClass.NU)]
    [InlineData(0x200D, (int)LineBreakClass.ZWJ)]
    [InlineData(0x1F1E6, (int)LineBreakClass.RI)]
    [InlineData(0x4E00, (int)LineBreakClass.ID)]
    [InlineData(0x2010, (int)LineBreakClass.HH)]
    [InlineData(0x0E01, (int)LineBreakClass.SA)]
    [InlineData(0x10FFFF, (int)LineBreakClass.XX)]
    public void A_character_has_the_line_break_class_the_data_gives_it(int codePoint, int expected) =>
        Assert.Equal((LineBreakClass)expected, CharacterProperties.Of(codePoint).LineBreak);

    [Theory]
    [InlineData(0x000D, (int)GraphemeBreakClass.CR)]
    [InlineData(0x0300, (int)GraphemeBreakClass.Extend)]
    [InlineData(0x200D, (int)GraphemeBreakClass.ZWJ)]
    [InlineData(0x1F1E6, (int)GraphemeBreakClass.RegionalIndicator)]
    [InlineData(0x0600, (int)GraphemeBreakClass.Prepend)]
    [InlineData(0x0903, (int)GraphemeBreakClass.SpacingMark)]
    [InlineData(0x1100, (int)GraphemeBreakClass.L)]
    [InlineData(0xAC00, (int)GraphemeBreakClass.LV)]
    [InlineData(0xAC01, (int)GraphemeBreakClass.LVT)]
    [InlineData(0x0041, (int)GraphemeBreakClass.Other)]
    public void A_character_has_the_grapheme_break_class_the_data_gives_it(int codePoint, int expected) =>
        Assert.Equal((GraphemeBreakClass)expected, CharacterProperties.Of(codePoint).GraphemeBreak);

    [Fact]
    public void The_flags_of_a_character_are_the_ones_the_data_gives_it()
    {
        Assert.True(CharacterProperties.Of(0x4E00).EastAsian);
        Assert.False(CharacterProperties.Of(0x0041).EastAsian);
        Assert.True(CharacterProperties.Of(0x00AB).InitialQuote);
        Assert.True(CharacterProperties.Of(0x00BB).FinalQuote);
        Assert.True(CharacterProperties.Of(0x0300).IsMark);
        Assert.True(CharacterProperties.Of(0x0378).IsUnassigned);
        Assert.False(CharacterProperties.Of(0x0041).IsUnassigned);
        Assert.True(CharacterProperties.Of(0x1F600).ExtendedPictographic);
        Assert.Equal(ConjunctBreak.Linker, CharacterProperties.Of(0x094D).Conjunct);
        Assert.Equal(ConjunctBreak.Consonant, CharacterProperties.Of(0x0915).Conjunct);
        Assert.Equal(ConjunctBreak.None, CharacterProperties.Of(0x0041).Conjunct);
    }

    [Fact]
    public void A_line_of_text_breaks_where_a_person_would_expect()
    {
        int[] text = [.. "Hello, world now\nnext".Select(c => (int)c)];

        LineBreakKind[] breaks = UnicodeLineBreaker.Breaks(text);

        Assert.Equal(LineBreakKind.None, breaks[0]);
        Assert.Equal(LineBreakKind.Allowed, breaks[7]);     // after "Hello, "
        Assert.Equal(LineBreakKind.None, breaks[12]);       // not around the no-break space
        Assert.Equal(LineBreakKind.None, breaks[13]);
        Assert.Equal(LineBreakKind.Mandatory, breaks[17]);  // after the line feed
        Assert.Equal(LineBreakKind.Mandatory, breaks[^1]);
        Assert.Equal([LineBreakKind.None], UnicodeLineBreaker.Breaks([]));
    }
}
