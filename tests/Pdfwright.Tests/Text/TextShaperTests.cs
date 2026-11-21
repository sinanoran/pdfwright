using Pdfwright.Fonts;
using Pdfwright.Text;

namespace Pdfwright.Tests.Text;

/// <summary>Font fallback and the contract of <see cref="TextShaper"/>, beyond what the corpus lines exercise.</summary>
public class TextShaperTests
{
    private static ShapingFont Load(string file) => new(TrueTypeFont.Parse(File.ReadAllBytes(TestPaths.Font(file))));

    [Fact]
    public void A_base_and_its_mark_are_never_split_between_fonts()
    {
        using ShapingFont latin = Load("NotoSans-Regular.ttf");
        using ShapingFont hebrew = Load("NotoSansHebrew-Regular.ttf");

        // Shin followed by a combining dot above (U+0307): Latin has the mark but not the letter, Hebrew the letter. One cluster, one font.
        ShapedParagraph paragraph = TextShaper.Shape("ש̇", [latin, hebrew]);

        ShapedRun run = Assert.Single(paragraph.Runs);
        Assert.Equal(1, run.FontIndex);
    }

    [Fact]
    public void A_letter_takes_the_first_font_that_has_it_and_a_space_stays_with_the_text_before_it()
    {
        using ShapingFont latin = Load("NotoSans-Regular.ttf");
        using ShapingFont arabic = Load("NotoSansArabic-Regular.ttf");

        ShapedParagraph paragraph = TextShaper.Shape("abc مرحبا xyz", [latin, arabic]);

        // "abc " in the Latin font (the space stays with it), the Arabic word in the Arabic font, then the space after it, which has no
        // script and stays in the font before it (at the paragraph's level, so a run of its own), and "xyz" back in the Latin font.
        Assert.Equal([0, 1, 1, 0], paragraph.Runs.Select(r => r.FontIndex).ToArray());
        Assert.Equal(1, paragraph.Runs[2].Utf16Length);
        Assert.Equal(0, paragraph.Runs[2].Level);
        Assert.Empty(paragraph.Missing);
    }

    [Fact]
    public void Characters_that_need_no_glyph_are_not_missing_and_do_not_cut_a_run()
    {
        using ShapingFont latin = Load("NotoSans-Regular.ttf");
        using ShapingFont hebrew = Load("NotoSansHebrew-Regular.ttf");

        ShapedParagraph paragraph = TextShaper.Shape("1️ ש‏לום\tend", [hebrew, latin]);

        Assert.Empty(paragraph.Missing);
        Assert.True(TextShaper.NeedsNoGlyph(0x200D));
        Assert.True(TextShaper.NeedsNoGlyph(0x0009));
        Assert.False(TextShaper.NeedsNoGlyph(0x0041));
    }

    [Fact]
    public void A_missing_glyph_stays_on_the_baseline_even_when_it_follows_a_base_of_another_script()
    {
        using ShapingFont latin = Load("NotoSans-Regular.ttf");
        using ShapingFont hebrew = Load("NotoSansHebrew-Regular.ttf");

        // A Hebrew point after a Latin digit: the cluster takes the digit's font, the two points have no glyph there, and HarfBuzz's fallback
        // positioning of marks (the font has no mark positioning for them) moves a .notdef glyph down by about its own height, off the line and,
        // on the last line of a page, off the page. A glyph that is drawn as .notdef is placed on the baseline.
        ShapedParagraph paragraph = TextShaper.Shape("0ִֶ x", [latin, hebrew]);

        Assert.Equal([1, 2], paragraph.Missing);
        ShapedGlyph[] notdef = [.. paragraph.Runs.SelectMany(r => r.Glyphs).Where(g => g.Glyph == 0)];
        Assert.Equal(2, notdef.Length);
        Assert.All(notdef, g => Assert.Equal(0, g.YOffset));
    }

    [Fact]
    public void A_character_no_font_has_is_reported_with_its_utf16_index()
    {
        using ShapingFont latin = Load("NotoSans-Regular.ttf");

        ShapedParagraph paragraph = TextShaper.Shape("abc😀", [latin]);

        Assert.Equal([2, 4], paragraph.Missing);
    }

    [Fact]
    public void Text_with_a_paragraph_separator_inside_is_refused_and_empty_text_keeps_the_requested_level()
    {
        using ShapingFont latin = Load("NotoSans-Regular.ttf");

        Assert.Throws<ArgumentException>(() => TextShaper.Shape("a\nb", [latin]));
        Assert.Throws<ArgumentException>(() => TextShaper.Shape("a", []));
        Assert.Equal(1, TextShaper.Shape(string.Empty, [latin], paragraphLevel: 1).ParagraphLevel);
        Assert.Empty(TextShaper.Shape(string.Empty, [latin]).Runs);
    }

    [Fact]
    public void Clusters_are_indices_into_the_whole_paragraph_and_a_right_to_left_run_lists_them_downwards()
    {
        using ShapingFont latin = Load("NotoSans-Regular.ttf");
        using ShapingFont arabic = Load("NotoSansArabic-Regular.ttf");

        ShapedParagraph paragraph = TextShaper.Shape("abc مرحبا xyz", [latin, arabic]);

        ShapedRun rtl = Assert.Single(paragraph.Runs, r => r.IsRightToLeft);
        int[] clusters = [.. rtl.Glyphs.Select(g => g.Cluster)];
        Assert.Equal(clusters.OrderByDescending(c => c), clusters);
        Assert.InRange(clusters.Min(), rtl.Utf16Start, rtl.Utf16Start + rtl.Utf16Length - 1);
        Assert.Equal(1, paragraph.Runs.Count(r => r.IsRightToLeft));
    }
}
