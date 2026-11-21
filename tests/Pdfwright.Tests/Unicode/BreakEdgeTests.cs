using Pdfwright.Unicode;
using Pdfwright.UnicodeGen;

namespace Pdfwright.Tests.Unicode;

/// <summary>What the conformance files do not reach: leftover marks, rules a mutation survived, long runs, and the UTF-16 bridge.</summary>
public class BreakEdgeTests
{
    private static LineBreakKind[] Breaks(params int[] text) => UnicodeLineBreaker.Breaks(text);

    [Fact]
    public void A_mark_that_attached_to_nothing_is_a_letter_with_the_properties_of_a_plain_a()
    {
        // U+302A is a wide combining mark (East Asian Width W) and U+201C a quotation mark: without a base the mark is a letter
        // of width Na (LB10), so rule LB19a keeps the quotation mark with it. LB9 also lets a mark attach to a leftover one.
        Assert.Equal(LineBreakKind.None, Breaks(0x302A, 0x201C, 0x4E00)[1]);
        Assert.Equal(LineBreakKind.None, Breaks(0x0020, 0x302A, 0x201C, 0x4E00)[2]);
        Assert.Equal(LineBreakKind.None, Breaks(0x0020, 0x0300, 0x302A, 0x201D, 0x4E00)[4]);
    }

    [Fact]
    public void Rules_that_the_test_file_cannot_tell_from_their_neighbours_still_hold()
    {
        Assert.Equal(LineBreakKind.None, Breaks(0x0022, 0x00AB, 0x0020, 0x0061)[3]);   // LB15a: a quotation mark, an opening quote, spaces
        Assert.Equal(LineBreakKind.None, Breaks(0x0024, 0x0028, 0x002E, 0x0035)[1]);   // LB25: PR x OP IS NU
        Assert.Equal(LineBreakKind.Allowed, Breaks(0x0061, 0x200D, 0x0300, 0x4E00)[3]); // LB8a is for a joiner that ends the unit, not a mark after it
        Assert.Equal(LineBreakKind.None, Breaks(0x0061, 0x200D, 0x4E00)[2]);
    }

    [Fact]
    public void A_very_long_run_of_regional_indicators_is_paired_in_linear_time()
    {
        int[] flags = Enumerable.Repeat(0x1F1E6, 100_001).ToArray();

        LineBreakKind[] breaks = UnicodeLineBreaker.Breaks(flags);

        Assert.Equal(LineBreakKind.None, breaks[1]);
        Assert.Equal(LineBreakKind.Allowed, breaks[2]);
        Assert.Equal(LineBreakKind.None, breaks[99_999]);
        Assert.Equal(LineBreakKind.Allowed, breaks[100_000]);
        Assert.Equal(LineBreakKind.Mandatory, breaks[100_001]);
        bool[] graphemes = UnicodeGraphemeBreaker.Boundaries(flags);
        Assert.False(graphemes[1]);
        Assert.True(graphemes[2]);
    }

    [Fact]
    public void The_enum_names_are_the_names_the_generator_packs_in_the_same_order()
    {
        Assert.Equal(PropertyDataGenerator.LineBreakNames, Enum.GetNames<LineBreakClass>());
        Assert.Equal(PropertyDataGenerator.GraphemeNames.Select(n => n.Replace("_", string.Empty, StringComparison.Ordinal)), Enum.GetNames<GraphemeBreakClass>());
    }

    [Fact]
    public void Empty_text_has_no_boundaries_to_speak_of()
    {
        Assert.Equal([LineBreakKind.None], UnicodeLineBreaker.Breaks([]));
        Assert.Equal([false], UnicodeGraphemeBreaker.Boundaries([]));
    }

    [Fact]
    public void A_string_becomes_code_points_with_the_utf16_index_of_each()
    {
        // "a", an emoji (a surrogate pair), a lone high surrogate, "b".
        int[] codePoints = UnicodeText.CodePoints("a😀\uD800b", out int[] index);

        Assert.Equal([0x61, 0x1F600, 0xFFFD, 0x62], codePoints);
        Assert.Equal([0, 1, 3, 4, 5], index);
        Assert.Empty(UnicodeText.CodePoints(string.Empty, out int[] none));
        Assert.Equal([0], none);
    }

    [Fact]
    public void An_emoji_in_a_string_is_one_grapheme_and_breaks_as_one_character()
    {
        int[] codePoints = UnicodeText.CodePoints("a😀‍😀 b", out int[] index);

        bool[] graphemes = UnicodeGraphemeBreaker.Boundaries(codePoints);

        Assert.Equal(6, codePoints.Length);
        Assert.Equal([true, true, false, false, true, true, true], graphemes); // a | emoji ZWJ emoji | space | b
        Assert.Equal(6, index[4]);
    }
}
