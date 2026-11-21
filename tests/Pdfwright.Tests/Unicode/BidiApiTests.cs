using System.Globalization;
using Pdfwright.Unicode;

namespace Pdfwright.Tests.Unicode;

/// <summary>What the conformance files do not reach: paragraphs, lines, the levels of removed characters, and the whole code space.</summary>
public class BidiApiTests
{
    private const int Alef = 0x05D0;
    private const int Bet = 0x05D1;

    private static int[] Text(params object[] parts) =>
        [.. parts.SelectMany<object, int>(p => p is string s ? s.EnumerateRunes().Select(r => r.Value) : [(int)p])];

    [Fact]
    public void A_paragraph_separator_before_the_last_character_is_refused_and_the_last_one_is_allowed()
    {
        Assert.Throws<ArgumentException>(() => UnicodeBidi.Resolve(Text("a", 0x2029, Alef)));
        Assert.Throws<ArgumentException>(() => UnicodeBidi.Resolve(Text("a", 0x0A, "b")));

        BidiResult last = UnicodeBidi.Resolve(Text(Alef, 0x2029));
        Assert.Equal(1, last.ParagraphLevel);
    }

    [Fact]
    public void Text_is_split_into_paragraphs_after_each_separator_and_a_carriage_return_line_feed_pair_stays_together()
    {
        int[] text = Text("ab", 0x0D, 0x0A, "c", 0x2029, "d", 0x0A);

        List<(int Start, int Length)> paragraphs = UnicodeBidi.Paragraphs(text);

        Assert.Equal([(0, 4), (4, 2), (6, 2)], paragraphs);
        Assert.Empty(UnicodeBidi.Paragraphs([]));
        Assert.Equal([(0, 3)], UnicodeBidi.Paragraphs(Text("abc")));
    }

    [Fact]
    public void Each_paragraph_of_a_split_text_gets_its_own_level_where_one_call_would_have_found_the_wrong_one()
    {
        int[] text = Text("1 ", 0x2029, Alef);
        List<(int Start, int Length)> paragraphs = UnicodeBidi.Paragraphs(text);

        BidiResult first = UnicodeBidi.Resolve(text.AsSpan(paragraphs[0].Start, paragraphs[0].Length));
        BidiResult second = UnicodeBidi.Resolve(text.AsSpan(paragraphs[1].Start, paragraphs[1].Length));

        Assert.Equal(0, first.ParagraphLevel);
        Assert.Equal(1, second.ParagraphLevel);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(-2)]
    public void A_paragraph_level_other_than_zero_one_or_automatic_is_refused(int level) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => UnicodeBidi.Resolve(Text("a"), level));

    [Fact]
    public void Resolving_nothing_gives_nothing()
    {
        BidiResult empty = UnicodeBidi.Resolve([]);

        Assert.Empty(empty.Levels);
        Assert.Equal(0, empty.ParagraphLevel);
        Assert.Empty(UnicodeBidi.VisualOrder([], 0, 0));
    }

    [Fact]
    public void A_line_that_does_not_start_at_zero_is_ordered_among_its_own_characters()
    {
        byte[] levels = [0, 0, 1, 1, 1, 0];

        Assert.Equal([4, 3, 2], UnicodeBidi.VisualOrder(levels, 2, 5));
        Assert.Equal([0, 1, 4, 3, 2, 5], UnicodeBidi.VisualOrder(levels, 0, 6));
        Assert.Throws<ArgumentOutOfRangeException>(() => UnicodeBidi.VisualOrder(levels, 4, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => UnicodeBidi.VisualOrder(levels, 0, 7));
    }

    [Fact]
    public void The_whitespace_at_the_end_of_a_line_that_is_not_the_last_returns_to_the_paragraph_level()
    {
        // "a ", alef, space, bet, space, "c" in a left-to-right paragraph: the space between the two Hebrew letters is inside
        // a right-to-left run for the paragraph, and the last character of the line "a  alef space" for a layout that breaks there.
        int[] text = Text("a ", Alef, " ", Bet, " c");
        BidiResult paragraph = UnicodeBidi.Resolve(text, 0);

        Assert.Equal(1, paragraph.Levels[3]);
        byte[] line = UnicodeBidi.LineLevels(paragraph, 0, 4);
        Assert.Equal(0, line[3]);
        Assert.Equal(1, paragraph.Levels[3]);
        Assert.Equal([0, 1, 2, 3], UnicodeBidi.VisualOrder(line, 0, 4));

        // A second line starting at 4 is taken from the same levels, indexed from its own start.
        byte[] rest = UnicodeBidi.LineLevels(paragraph, 4, 7);
        Assert.Equal(3, rest.Length);
        Assert.Equal(paragraph.Levels[4], rest[0]);
    }

    [Fact]
    public void Applying_the_end_of_line_rule_twice_changes_nothing()
    {
        BidiResult paragraph = UnicodeBidi.Resolve(Text(Alef, " ", Bet, "  "), 0);
        byte[] once = UnicodeBidi.LineLevels(paragraph, 0, paragraph.Levels.Length);

        byte[] twice = (byte[])once.Clone();
        UnicodeBidi.ApplyL1(twice, paragraph.Classes, 0, twice.Length, paragraph.ParagraphLevel);

        Assert.Equal(once, twice);
        Assert.Equal(paragraph.Levels, once);
    }

    [Fact]
    public void A_character_that_rule_x9_removes_takes_the_level_of_the_character_before_it_or_the_paragraph_level()
    {
        // A soft hyphen (class BN) between two Hebrew letters is inside their run; at the start it is at the paragraph level.
        BidiResult inside = UnicodeBidi.Resolve(Text(Alef, 0x00AD, Bet), 0);
        BidiResult atStart = UnicodeBidi.Resolve(Text(0x00AD, Alef), 0);
        BidiResult atStartRtl = UnicodeBidi.Resolve(Text(0x00AD, "a"), 1);
        BidiResult embeddingMark = UnicodeBidi.Resolve(Text(Alef, 0x202A, Bet), 0);

        Assert.Equal([1, 1, 1], inside.Levels);
        Assert.Equal(0, atStart.Levels[0]);
        Assert.Equal(1, atStartRtl.Levels[0]);
        Assert.Equal(1, embeddingMark.Levels[1]);
    }

    [Fact]
    public void Surrogate_halves_and_values_outside_the_code_space_are_left_to_right_and_a_supplementary_rtl_letter_is_right_to_left()
    {
        Assert.Equal(BidiClass.L, BidiData.ClassOf(0xD800));
        Assert.Equal(BidiClass.L, BidiData.ClassOf(0xDFFF));
        Assert.Equal(1, UnicodeBidi.Resolve(Text(0x1E900), UnicodeBidi.AutoLevel).ParagraphLevel); // Adlam, one code point
        Assert.Equal(0, UnicodeBidi.Resolve([0xD83A, 0xDD00], UnicodeBidi.AutoLevel).ParagraphLevel); // the same two UTF-16 units: not code points
    }

    [Fact]
    public void The_class_table_agrees_with_every_code_point_the_data_file_lists()
    {
        // An independent read of DerivedBidiClass.txt: the explicit lines, not the @missing defaults the generator also uses.
        int checkedCodePoints = 0;
        foreach (string raw in File.ReadLines(Path.Combine(TestPaths.RepositoryRoot, "data", "unicode", "18.0.0", "DerivedBidiClass.txt")))
        {
            string line = raw.Split('#')[0].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            string[] fields = line.Split(';');
            string[] range = fields[0].Trim().Split("..");
            int first = int.Parse(range[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            int last = range.Length > 1 ? int.Parse(range[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture) : first;
            BidiClass expected = Enum.Parse<BidiClass>(fields[1].Trim());
            for (int cp = first; cp <= last; cp++)
            {
                Assert.True(BidiData.ClassOf(cp) == expected, $"U+{cp:X4} is {BidiData.ClassOf(cp)}, the file says {expected}");
                checkedCodePoints++;
            }
        }

        Assert.True(checkedCodePoints > 300_000, $"{checkedCodePoints} code points were checked");
    }
}
