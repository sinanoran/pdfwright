using System.Security.Cryptography;
using Pdfwright.Unicode;
using Pdfwright.UnicodeGen;

namespace Pdfwright.Tests.Unicode;

/// <summary>The pinned Unicode data is the data that was downloaded, the generated tables are what the generator makes of it, and the lookups are right.</summary>
public class BidiDataTests
{
    private static string DataDirectory => Path.Combine(TestPaths.RepositoryRoot, "data", "unicode", "18.0.0");

    [Fact]
    public void Every_data_file_has_the_hash_that_was_recorded_when_it_was_downloaded()
    {
        string[] lines = File.ReadAllLines(Path.Combine(DataDirectory, "SHA256SUMS"));
        Assert.True(lines.Length >= 5);
        foreach (string line in lines)
        {
            string[] parts = line.Split(" *");
            string actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(DataDirectory, parts[1])))).ToLowerInvariant();
            Assert.True(actual == parts[0], $"{parts[1]} is not the file that was downloaded (a line-ending conversion changes it)");
        }

        Assert.True(File.Exists(Path.Combine(TestPaths.RepositoryRoot, "data", "unicode", "LICENSE-UNICODE.txt")));
    }

    [Fact]
    public void The_generated_tables_are_what_the_generator_makes_of_the_data()
    {
        string committed = File.ReadAllText(Path.Combine(TestPaths.RepositoryRoot, "src", "Pdfwright", "Unicode", "BidiTables.g.cs")).Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Equal(BidiDataGenerator.Generate(DataDirectory), committed);
        Assert.Equal("18.0.0", BidiTables.UnicodeVersion);
    }

    [Theory]
    [InlineData(0x0061, (int)BidiClass.L)]
    [InlineData(0x05D0, (int)BidiClass.R)]
    [InlineData(0x0627, (int)BidiClass.AL)]
    [InlineData(0x0030, (int)BidiClass.EN)]
    [InlineData(0x002B, (int)BidiClass.ES)]
    [InlineData(0x0024, (int)BidiClass.ET)]
    [InlineData(0x0660, (int)BidiClass.AN)]
    [InlineData(0x002C, (int)BidiClass.CS)]
    [InlineData(0x0300, (int)BidiClass.NSM)]
    [InlineData(0x00AD, (int)BidiClass.BN)]
    [InlineData(0x2029, (int)BidiClass.B)]
    [InlineData(0x0009, (int)BidiClass.S)]
    [InlineData(0x0020, (int)BidiClass.WS)]
    [InlineData(0x0021, (int)BidiClass.ON)]
    [InlineData(0x202A, (int)BidiClass.LRE)]
    [InlineData(0x202D, (int)BidiClass.LRO)]
    [InlineData(0x202B, (int)BidiClass.RLE)]
    [InlineData(0x202E, (int)BidiClass.RLO)]
    [InlineData(0x202C, (int)BidiClass.PDF)]
    [InlineData(0x2066, (int)BidiClass.LRI)]
    [InlineData(0x2067, (int)BidiClass.RLI)]
    [InlineData(0x2068, (int)BidiClass.FSI)]
    [InlineData(0x2069, (int)BidiClass.PDI)]
    [InlineData(0x0590, (int)BidiClass.R)]
    [InlineData(0x20CF, (int)BidiClass.ET)]
    [InlineData(0x10FFFF, (int)BidiClass.BN)]
    public void A_character_has_the_class_the_standard_gives_it(int codePoint, int expected) =>
        Assert.Equal((BidiClass)expected, BidiData.ClassOf(codePoint));

    [Fact]
    public void Brackets_pair_with_their_partners_and_the_angle_brackets_are_canonically_equivalent()
    {
        Assert.Equal(BracketType.Open, BidiData.BracketTypeOf('('));
        Assert.Equal(BracketType.Close, BidiData.BracketTypeOf(')'));
        Assert.Equal(BracketType.None, BidiData.BracketTypeOf('a'));
        Assert.Equal(')', BidiData.PairedBracket('('));
        Assert.Equal(0x3008, BidiData.CanonicalBracket(0x2329));
        Assert.Equal(0x3009, BidiData.CanonicalBracket(0x232A));
    }

    [Fact]
    public void A_value_outside_the_code_space_is_left_to_right()
    {
        Assert.Equal(BidiClass.L, BidiData.ClassOf(-1));
        Assert.Equal(BidiClass.L, BidiData.ClassOf(0x110000));
    }

    [Fact]
    public void The_levels_and_the_visual_order_of_a_short_mixed_line_are_the_ones_the_standard_shows()
    {
        // "abc " followed by Hebrew alef bet gimel, in a left-to-right paragraph: the Hebrew is one right-to-left run.
        int[] text = [.. "abc ".Select(c => (int)c), 0x05D0, 0x05D1, 0x05D2];

        BidiResult result = UnicodeBidi.Resolve(text, 0);

        Assert.Equal(new byte[] { 0, 0, 0, 0, 1, 1, 1 }, result.Levels);
        Assert.Equal(new[] { 0, 1, 2, 3, 6, 5, 4 }, UnicodeBidi.VisualOrder(result.Levels, 0, result.Levels.Length));
        Assert.Equal(1, UnicodeBidi.Resolve([0x05D0, 0x0061], UnicodeBidi.AutoLevel).ParagraphLevel);
    }
}
