using Pdfwright.Text;

namespace Pdfwright.Tests.Text;

/// <summary>The measurer and the layout are one engine: what one reports the other draws.</summary>
public class TextMeasurerTests
{
    private const double Tolerance = 1e-6;

    [Theory]
    [InlineData("latin-de", null)]
    [InlineData("latin-de", 120)]
    [InlineData("mixed-en-in-ar", 150)]
    [InlineData("mixed-ar-he-en", 80)]
    [InlineData("thai-sentence", 100)]
    public void Measuring_agrees_with_laying_out(string id, int? width)
    {
        CorpusTests.Case c = LayoutTestKit.Case(id);
        ShapingFont[] fonts = LayoutTestKit.Fonts(c.Fonts);

        (double measuredWidth, double height, int lines) = TextMeasurer.Measure(c.Text, fonts, 11, width);

        ParagraphLayout layout = LayoutTestKit.Layout(c.Text, fonts, width, size: 11);
        Assert.Equal(layout.Width, measuredWidth);
        Assert.Equal(layout.Height, height);
        Assert.Equal(layout.LineCount, lines);
        Assert.Equal(layout.Lines.Max(l => l.Width), measuredWidth, Tolerance);
        Assert.Equal(layout.Lines.Sum(l => l.Pitch), height, Tolerance);
    }

    [Fact]
    public void The_width_of_an_unlimited_latin_string_is_the_sum_of_its_glyph_advances()
    {
        ShapingFont[] fonts = LayoutTestKit.Fonts(LayoutTestKit.Latin);
        const string text = "Total: 1,234 EUR AVATAR office";

        (double width, _, int lines) = TextMeasurer.Measure(text, fonts, 12);

        // The advances come from a shaping of their own, in font units, scaled by hand.
        ShapedParagraph shaped = TextShaper.Shape(text, fonts);
        double advances = shaped.Runs.SelectMany(r => r.Glyphs).Sum(g => (double)g.XAdvance) * 12 / fonts[0].Font.UnitsPerEm;
        Assert.Equal(1, lines);
        Assert.Equal(advances, width, Tolerance);
        Assert.True(width > 0);
    }

    [Fact]
    public void The_width_grows_with_the_size_and_the_height_with_the_line_count()
    {
        ShapingFont[] fonts = LayoutTestKit.Fonts(LayoutTestKit.Latin);
        const string text = "Quarterly report for the whole organisation";

        (double small, double smallHeight, _) = TextMeasurer.Measure(text, fonts, 10);
        (double large, double largeHeight, _) = TextMeasurer.Measure(text, fonts, 20);
        (_, double wrappedHeight, int wrappedLines) = TextMeasurer.Measure(text, fonts, 10, 100);

        Assert.Equal(small * 2, large, Tolerance);
        Assert.Equal(smallHeight * 2, largeHeight, Tolerance);
        Assert.True(wrappedLines >= 3);
        Assert.Equal(smallHeight * wrappedLines, wrappedHeight, Tolerance);
    }

    [Fact]
    public void Text_with_a_paragraph_separator_inside_is_refused()
    {
        ShapingFont[] fonts = LayoutTestKit.Fonts(LayoutTestKit.Latin);

        Assert.Throws<ArgumentException>(() => TextMeasurer.Measure("one\ntwo", fonts, 12));
    }
}
