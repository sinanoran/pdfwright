using Pdfwright.Text;
using UglyToad.PdfPig;

namespace Pdfwright.Tests.Text;

/// <summary>A tab has no glyph: it is laid out as one space and extracted as the tab it is.</summary>
public class ParagraphTabTests
{
    [Fact]
    public void A_tab_is_as_wide_as_a_space_and_is_not_drawn_as_a_missing_glyph()
    {
        ShapingFont[] fonts = LayoutTestKit.Fonts(LayoutTestKit.Latin);

        ParagraphLayout tab = LayoutTestKit.Layout("a\tb", fonts, null);
        ParagraphLayout space = LayoutTestKit.Layout("a b", fonts, null);

        Assert.Equal(space.Width, tab.Width, 1e-9);
        Assert.Equal(3, tab.Lines[0].Length);
        Assert.Equal(space.Lines[0].Runs.SelectMany(r => r.Run.Glyphs), tab.Lines[0].Runs.SelectMany(r => r.Run.Glyphs));
        Assert.DoesNotContain(tab.Lines[0].Runs.SelectMany(r => r.Run.Glyphs), g => g.Glyph == 0);
    }

    [Fact]
    public void A_tab_stays_a_tab_in_the_text_a_reader_extracts()
    {
        ShapingFont[] fonts = LayoutTestKit.Fonts(LayoutTestKit.Latin);
        const string text = "a\tb";

        ParagraphLayout layout = LayoutTestKit.Layout(text, fonts, null);
        using UglyToad.PdfPig.PdfDocument document = UglyToad.PdfPig.PdfDocument.Open(LayoutTestKit.Draw(layout, fonts));

        Assert.Equal(text, document.GetPage(1).Text);
    }

    [Fact]
    public void A_tab_is_a_place_where_a_line_may_break_like_the_space_it_stands_for()
    {
        ShapingFont[] fonts = LayoutTestKit.Fonts(LayoutTestKit.Latin);
        double aaa = ParagraphMeasurer.Measure("aaa", fonts, new ParagraphOptions { Size = 14 }).Width;
        double bbb = ParagraphMeasurer.Measure("bbb", fonts, new ParagraphOptions { Size = 14 }).Width;

        ParagraphLayout layout = LayoutTestKit.Layout("aaa\tbbb", fonts, bbb + 1);

        Assert.Equal([0, 4], layout.Lines.Select(l => l.Start).ToArray());
        Assert.Equal(aaa, layout.Lines[0].Width, 1e-6);
    }

    [Fact]
    public void A_tab_in_a_right_to_left_paragraph_is_a_segment_separator_at_the_paragraph_level()
    {
        ShapingFont[] fonts = LayoutTestKit.Fonts(LayoutTestKit.Latin);

        // Rule L1 returns the tab to the paragraph level, so the two words are separate left-to-right runs on either side of it, in right-to-left order.
        ParagraphLayout layout = LayoutTestKit.Layout("abc\tdef", fonts, null, paragraphLevel: 1);

        PlacedRun Run(int index) => layout.Lines[0].Runs.Single(r => r.Run.Utf16Start <= index && index < r.Run.Utf16Start + r.Run.Utf16Length);
        PlacedRun abc = Run(0);
        PlacedRun tab = Run(3);
        PlacedRun def = Run(4);
        Assert.Equal(2, abc.Run.Level);
        Assert.Equal(1, tab.Run.Level);
        Assert.Equal(2, def.Run.Level);
        Assert.True(def.X < tab.X && tab.X < abc.X);

        // The same text with a space in place of the tab is one left-to-right stretch.
        ParagraphLayout space = LayoutTestKit.Layout("abc def", fonts, null, paragraphLevel: 1);
        Assert.Equal(space.Width, layout.Width, 1e-9);
        Assert.Single(space.Lines[0].Runs);
    }
}
