using Pdfwright.Unicode;

namespace Pdfwright.Tests.Unicode;

/// <summary>A carriage return and line feed at the end of a paragraph are one separator, as <see cref="UnicodeBidi.Paragraphs"/> returns them.</summary>
public class BidiLineEndTests
{
    private const int Alef = 0x05D0;

    [Fact]
    public void A_carriage_return_line_feed_pair_ends_a_paragraph_and_takes_the_paragraph_level()
    {
        BidiResult rtl = UnicodeBidi.Resolve([Alef, 0x20, 0x0D, 0x0A]);
        BidiResult ltr = UnicodeBidi.Resolve(['a', Alef, 0x0D, 0x0A]);
        BidiResult forced = UnicodeBidi.Resolve(['a', 0x0D, 0x0A], 1);

        Assert.Equal(1, rtl.ParagraphLevel);
        Assert.Equal([1, 1, 1, 1], rtl.Levels);
        Assert.Equal(0, ltr.ParagraphLevel);
        Assert.Equal([0, 1, 0, 0], ltr.Levels);
        Assert.Equal([2, 1, 1], forced.Levels);
    }

    [Fact]
    public void A_carriage_return_line_feed_pair_anywhere_else_is_still_refused()
    {
        Assert.Throws<ArgumentException>(() => UnicodeBidi.Resolve(['a', 0x0D, 0x0A, 'b']));
        Assert.Throws<ArgumentException>(() => UnicodeBidi.Resolve(['a', 0x0D, 0x0D, 0x0A]));
        Assert.Throws<ArgumentException>(() => UnicodeBidi.Resolve(['a', 0x0A, 0x0D]));
    }

    [Fact]
    public void Every_paragraph_that_Paragraphs_returns_can_be_resolved()
    {
        int[] text = [.. "one\r\ntwo\r\n\r\nthree\n".Select(c => (int)c)];

        foreach ((int start, int length) in UnicodeBidi.Paragraphs(text))
        {
            BidiResult result = UnicodeBidi.Resolve(text.AsSpan(start, length));

            Assert.Equal(length, result.Levels.Length);
        }
    }
}
