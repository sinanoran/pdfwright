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
        using PdfDocument document = PdfDocument.Open(LayoutTestKit.Draw(layout, text, fonts));

        Assert.Equal(text, document.GetPage(1).Text);
    }

    [Fact]
    public void A_tab_is_a_place_where_a_line_may_break_like_the_space_it_stands_for()
    {
        ShapingFont[] fonts = LayoutTestKit.Fonts(LayoutTestKit.Latin);
        double aaa = TextMeasurer.Measure("aaa", fonts, new ParagraphOptions { Size = 14 }).Width;
        double bbb = TextMeasurer.Measure("bbb", fonts, new ParagraphOptions { Size = 14 }).Width;

        ParagraphLayout layout = LayoutTestKit.Layout("aaa\tbbb", fonts, bbb + 1);

        Assert.Equal([0, 4], layout.Lines.Select(l => l.Start).ToArray());
        Assert.Equal(aaa, layout.Lines[0].Width, 1e-6);
    }
}
