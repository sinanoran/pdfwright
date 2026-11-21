using System.Diagnostics;
using Pdfwright.Text;

namespace Pdfwright.Tests.Text;

/// <summary>A long paragraph costs time in proportion to its length, not to its length times its number of lines.</summary>
public class ParagraphLayoutScaleTests
{
    private const double Width = 300;
    private const string Sentence = "The quick brown fox jumps over the lazy dog, then keeps running through the quiet valley until night falls. ";

    private static string Repeated(int minimumLength)
    {
        System.Text.StringBuilder text = new();
        while (text.Length < minimumLength)
        {
            text.Append(Sentence);
        }

        return text.ToString();
    }

    [Fact]
    public void A_hundred_thousand_characters_are_laid_out_in_linear_time_and_agree_with_a_short_layout_of_their_start()
    {
        ShapingFont[] fonts = LayoutTestKit.Fonts(LayoutTestKit.Latin);
        string text = Repeated(100_000);
        _ = LayoutTestKit.Layout(Repeated(2_000), fonts, Width); // the fonts and the code are warm

        Stopwatch clock = Stopwatch.StartNew();
        ParagraphLayout layout = LayoutTestKit.Layout(text, fonts, Width);
        clock.Stop();

        // Cutting the runs of every line used to scan the whole paragraph: 9.8 s for this text, against 0.3 s for shaping it.
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), $"laying out {text.Length} characters took {clock.Elapsed}");
        Assert.True(layout.LineCount > 1_500);

        int next = 0;
        foreach (LaidOutLine line in layout.Lines)
        {
            Assert.Equal(next, line.Start);
            Assert.True(line.Width <= Width + 1e-6);
            next += line.Length;
        }

        Assert.Equal(text.Length, next);

        // Greedy filling, checked a second way: every line but the last could not have taken the first word of the next one.
        for (int i = 0; i < layout.LineCount - 1; i += 37)
        {
            LaidOutLine line = layout.Lines[i];
            int wordStart = layout.Lines[i + 1].Start;
            int wordEnd = text.IndexOf(' ', wordStart);
            string tooWide = text.Substring(line.Start, line.Length).TrimEnd() + " " + text[wordStart..wordEnd];
            Assert.True(TextMeasurer.Measure(tooWide, fonts, new ParagraphOptions { Size = 14 }).Width > Width, $"line {i} could have taken the next word");
        }

        // The first lines of a short text that is a prefix of the long one are the first lines of the long one.
        string prefix = Repeated(5_000);
        ParagraphLayout head = LayoutTestKit.Layout(prefix, fonts, Width);
        for (int i = 0; i < head.LineCount - 1; i++)
        {
            Assert.Equal((head.Lines[i].Start, head.Lines[i].Length), (layout.Lines[i].Start, layout.Lines[i].Length));
        }
    }
}
