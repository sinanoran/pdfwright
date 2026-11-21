using Pdfwright.Text;
using Pdfwright.Unicode;
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
        using UglyToad.PdfPig.PdfDocument document = UglyToad.PdfPig.PdfDocument.Open(LayoutTestKit.Draw(layout, fonts));
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
            Assert.True(ParagraphMeasurer.Measure(withNext, fonts, new ParagraphOptions { Size = 14 }).Width > 60, $"line {i} could have taken one more letter");
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
    public void A_single_glyph_cluster_wider_than_the_box_stays_whole_on_its_own_line()
    {
        ShapingFont[] fonts = LayoutTestKit.Fonts(LayoutTestKit.Latin);

        // e + combining acute is one glyph cluster of two characters: never split, even by a box narrower than one letter.
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
        double bb = ParagraphMeasurer.Measure("bb", fonts, new ParagraphOptions { Size = 14 }).Width;

        ParagraphLayout layout = LayoutTestKit.Layout("aa bbbbbbbbbbbb aa", fonts, bb + 0.5);

        // "aa " first, then the long word in pieces of at most two letters, then "aa": the short words are whole.
        Assert.Equal(0, layout.Lines[0].Start);
        Assert.Equal(3, layout.Lines[0].Length);
        Assert.Equal(2, layout.Lines[^1].Length);
        Assert.Equal(16, layout.Lines[^1].Start);
        AssertTiles("aa bbbbbbbbbbbb aa", layout);
    }

    [Fact]
    public void A_soft_hyphen_is_never_a_place_to_break()
    {
        ShapingFont[] fonts = LayoutTestKit.Fonts(LayoutTestKit.Latin);
        char shy = (char)0x00AD;
        string text = "aaaa" + shy + "bbbb cc";

        // Room for the first half only: with a break at the soft hyphen the first line would end there (and show no hyphen).
        double half = ParagraphMeasurer.Measure("aaaa", fonts, new ParagraphOptions { Size = 14 }).Width;
        ParagraphLayout narrow = LayoutTestKit.Layout(text, fonts, half + 1);

        AssertTiles(text, narrow);
        Assert.DoesNotContain(narrow.Lines, l => l.Start is 4 or 5);

        // Room for the whole word: it stays on one line, with the soft hyphen inside it.
        double word = ParagraphMeasurer.Measure("aaaabbbb", fonts, new ParagraphOptions { Size = 14 }).Width;
        ParagraphLayout wide = LayoutTestKit.Layout(text, fonts, word + 0.5);

        Assert.Equal([0, 10], wide.Lines.Select(l => l.Start).ToArray());
    }

    [Fact]
    public void A_break_opportunity_inside_a_glyph_cluster_is_not_used()
    {
        ShapingFont[] fonts = LayoutTestKit.Fonts(LayoutTestKit.Latin);

        // A combining mark after a space: UAX #14 allows a break between them (the mark is a letter after a space, rule LB10), but the space and the mark are one
        // grapheme cluster and the shaper gives them one glyph cluster, so the line must not end between them.
        string text = "aaa " + (char)0x0301 + "bbb";
        ShapedParagraph shaped = TextShaper.Shape(text, fonts);
        Assert.DoesNotContain(shaped.Runs.SelectMany(r => r.Glyphs), g => g.Cluster == 4);
        int[] codePoints = UnicodeText.CodePoints(text, out _);
        Assert.NotEqual(LineBreakKind.None, UnicodeLineBreaker.Breaks(codePoints)[4]);

        double aaa = ParagraphMeasurer.Measure("aaa", fonts, new ParagraphOptions { Size = 14 }).Width;
        ParagraphLayout layout = LayoutTestKit.Layout(text, fonts, aaa + 0.5);

        AssertTiles(text, layout);
        Assert.DoesNotContain(layout.Lines, l => l.Start == 4);
        Assert.True(layout.LineCount > 1);
    }

    [Fact]
    public void A_break_opportunity_between_a_space_and_a_mark_in_a_right_to_left_run_is_not_used()
    {
        ShapingFont[] fonts = LayoutTestKit.Fonts(LayoutTestKit.Hebrew);

        // Right-to-left runs are shaped at cluster level 1, so the mark after the space has a glyph cluster of its own and the break opportunity
        // of UAX #14 (rule LB10) in front of it is a cluster boundary: only the grapheme cluster rule keeps the line from ending there.
        string word = new([(char)0x05E9, (char)0x05DC, (char)0x05D5, (char)0x05DD]);
        string text = word + " " + (char)0x05B8 + word;
        ShapedParagraph shaped = TextShaper.Shape(text, fonts);
        Assert.Contains(shaped.Runs.SelectMany(r => r.Glyphs), g => g.Cluster == 5);
        int[] codePoints = UnicodeText.CodePoints(text, out _);
        Assert.NotEqual(LineBreakKind.None, UnicodeLineBreaker.Breaks(codePoints)[5]);

        double width = ParagraphMeasurer.Measure(word, fonts, new ParagraphOptions { Size = 14 }).Width;
        ParagraphLayout layout = LayoutTestKit.Layout(text, fonts, width + 0.5);

        AssertTiles(text, layout);
        Assert.Equal([0, 4, 9], layout.Lines.Select(l => l.Start).ToArray());
    }

    [Fact]
    public void A_text_that_ends_in_a_soft_hyphen_is_still_cut_to_the_width()
    {
        ShapingFont[] fonts = LayoutTestKit.Fonts(LayoutTestKit.Latin);
        string text = "xx abcdefghijklmnopqrstuvwxyz";
        string withShy = text + (char)0x00AD;

        ParagraphLayout plain = LayoutTestKit.Layout(text, fonts, 60);
        ParagraphLayout shy = LayoutTestKit.Layout(withShy, fonts, 60);

        // The soft hyphen is a character with no width of its own and no place to break: it must not switch the cutting of the long word off.
        Assert.True(plain.LineCount > 2);
        Assert.Equal(plain.LineCount, shy.LineCount);
        AssertTiles(withShy, shy);
        Assert.All(shy.Lines, l => Assert.True(l.Width <= 60 + Tolerance));
    }

    [Fact]
    public void A_cut_never_falls_between_a_base_and_a_mark_that_shape_in_different_runs()
    {
        // The Latin font covers the base but not the Hebrew point, which only the second font has, and the point is a Hebrew character: the run starts again at it, a glyph cluster boundary
        // between base and mark that is no grapheme boundary, so only the grapheme cluster rule keeps the cut from falling there.
        ShapingFont[] fonts = LayoutTestKit.Fonts(LayoutTestKit.Latin, LayoutTestKit.Hebrew);
        char mark = (char)0x05B0;
        Assert.Equal(0, fonts[0].Font.GlyphForCodePoint(mark));
        Assert.NotEqual(0, fonts[1].Font.GlyphForCodePoint(mark));
        Assert.NotEqual(0, fonts[0].Font.GlyphForCodePoint('a'));

        string text = string.Concat(Enumerable.Repeat("a" + mark, 12));
        ShapedParagraph shaped = TextShaper.Shape(text, fonts);
        Assert.Contains(shaped.Runs, r => r.Utf16Start % 2 == 1);

        double unit = ParagraphMeasurer.Measure("a" + mark, fonts, new ParagraphOptions { Size = 14 }).Width;
        ParagraphLayout layout = LayoutTestKit.Layout(text, fonts, unit * 2.5);

        Assert.True(layout.LineCount > 3);
        AssertTiles(text, layout);
        Assert.All(layout.Lines, l => Assert.NotEqual(mark, text[l.Start]));
    }
}
