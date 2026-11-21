using Pdfwright.Text;
using UglyToad.PdfPig;

namespace Pdfwright.Tests.Text;

/// <summary>A word wider than the width is cut between grapheme clusters, with no hyphen, as much as fits on each line (CSS <c>overflow-wrap: anywhere</c>).</summary>
public class ParagraphWordBreakTests
{
    private const double Tolerance = 1e-6;

    private static string Strip(string text) =>
        string.Concat(text.Where(c => !char.IsWhiteSpace(c) && c != (char)0x034F));

    private static string ExtractedText(ParagraphLayout layout, string text, ShapingFont[] fonts)
    {
        using PdfDocument document = PdfDocument.Open(LayoutTestKit.Draw(layout, text, fonts));
        return Strip(document.GetPage(1).Text);
    }

    private static int ContentClusters(LaidOutLine line) =>
        line.Runs.Where(r => !r.IsTrailingSpace).SelectMany(r => r.Run.Glyphs).Select(g => g.Cluster).Distinct().Count();

    private static void AssertTiles(string text, ParagraphLayout layout)
    {
        int next = 0;
        foreach (LaidOutLine line in layout.Lines)
        {
            Assert.Equal(next, line.Start);
            Assert.True(line.Length > 0);
            next += line.Length;
        }

        Assert.Equal(text.Length, next);
    }

    [Fact]
    public void Thai_text_without_spaces_is_cut_into_lines_that_fit()
    {
        ShapingFont[] fonts = LayoutTestKit.Fonts(LayoutTestKit.Case("thai-sentence").Fonts);
        string sentence = Strip(LayoutTestKit.Case("thai-sentence").Text);
        string text = sentence;
        while (text.Length < 174)
        {
            text += sentence;
        }

        ParagraphLayout layout = LayoutTestKit.Layout(text, fonts, 100);

        Assert.True(layout.LineCount > 3);
        AssertTiles(text, layout);
        Assert.All(layout.Lines, l => Assert.True(l.Width <= 100 + Tolerance || ContentClusters(l) == 1));
        Assert.Equal(text, ExtractedText(layout, text, fonts));
    }

    [Fact]
    public void A_long_latin_word_is_cut_between_letters_and_a_line_takes_as_many_as_fit()
    {
        ShapingFont[] fonts = LayoutTestKit.Fonts(LayoutTestKit.Latin);
        string word = LayoutTestKit.Case("latin-long-unbreakable").Text.Split(' ')[0];

        ParagraphLayout layout = LayoutTestKit.Layout(word, fonts, 60);

        Assert.True(layout.LineCount > 2);
        AssertTiles(word, layout);
        Assert.All(layout.Lines, l => Assert.True(l.Width <= 60 + Tolerance));
        for (int i = 0; i < layout.LineCount - 1; i++)
        {
            // One letter more would not have fitted (no ligature in this word can make the line shorter than its letters).
            string withNext = word.Substring(layout.Lines[i].Start, layout.Lines[i].Length + 1);
            Assert.True(TextMeasurer.Measure(withNext, fonts, new ParagraphOptions { Size = 14 }).Width > 60, $"line {i} could have taken one more letter");
        }

        Assert.Equal(word, ExtractedText(layout, word, fonts));
    }

    [Fact]
    public void A_long_arabic_word_split_between_lines_keeps_its_text_in_order()
    {
        CorpusTests.Case c = LayoutTestKit.Case("long-unbreakable-ar");
        ShapingFont[] fonts = LayoutTestKit.Fonts(c.Fonts);

        ParagraphLayout layout = LayoutTestKit.Layout(c.Text, fonts, 40);

        Assert.True(layout.LineCount > 2);
        AssertTiles(c.Text, layout);
        Assert.All(layout.Lines, l => Assert.True(l.Width <= 40 + Tolerance || ContentClusters(l) == 1));
        Assert.Equal(c.Text, ExtractedText(layout, c.Text, fonts));
    }

    [Fact]
    public void A_single_cluster_wider_than_the_box_stays_whole_on_its_own_line()
    {
        ShapingFont[] fonts = LayoutTestKit.Fonts(LayoutTestKit.Latin);

        // e + combining acute is one cluster of two characters: never split, even by a box narrower than one letter.
        string text = "e" + (char)0x0301 + "e";
        ParagraphLayout layout = LayoutTestKit.Layout(text, fonts, 1);

        Assert.Equal([0, 2], layout.Lines.Select(l => l.Start).ToArray());
        Assert.Equal([2, 1], layout.Lines.Select(l => l.Length).ToArray());
        Assert.All(layout.Lines, l => Assert.True(l.Width > 1));
    }

    [Fact]
    public void A_word_that_fits_after_a_space_is_not_cut_and_only_the_word_that_does_not_fit_is()
    {
        ShapingFont[] fonts = LayoutTestKit.Fonts(LayoutTestKit.Latin);
        double bb = TextMeasurer.Measure("bb", fonts, new ParagraphOptions { Size = 14 }).Width;

        ParagraphLayout layout = LayoutTestKit.Layout("aa bbbbbbbbbbbb aa", fonts, bb + 0.5);

        // "aa " first, then the long word in pieces of at most two letters, then "aa": the short words are whole.
        Assert.Equal(0, layout.Lines[0].Start);
        Assert.Equal(3, layout.Lines[0].Length);
        Assert.Equal(2, layout.Lines[^1].Length);
        Assert.Equal(16, layout.Lines[^1].Start);
        AssertTiles("aa bbbbbbbbbbbb aa", layout);
    }
}
