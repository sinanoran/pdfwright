using Pdfwright.Text;

namespace Pdfwright.Tests.Text;

/// <summary>The measurer and the layout are one engine, and what it reports is checked here against computations that do not use the layout.</summary>
public class TextMeasurerTests
{
    private const double Tolerance = 1e-6;

    /// <summary>The advance of a string in points, from a shaping of its own: the sum of its glyph advances in font units, scaled by hand.</summary>
    private static double Advance(string text, ShapingFont font, double size) =>
        TextShaper.Shape(text, [font]).Runs.SelectMany(r => r.Glyphs).Sum(g => (double)g.XAdvance) * size / font.Font.UnitsPerEm;

    [Theory]
    [InlineData("latin-de", null)]
    [InlineData("latin-de", 120)]
    [InlineData("mixed-en-in-ar", 150)]
    [InlineData("mixed-ar-he-en", 80)]
    [InlineData("thai-sentence", 100)]
    public void Measuring_reports_what_laying_out_with_the_same_options_finds(string id, int? width)
    {
        CorpusTests.Case c = LayoutTestKit.Case(id);
        ShapingFont[] fonts = LayoutTestKit.Fonts(c.Fonts);
        ParagraphOptions options = new() { Size = 11, MaxWidth = width, LineSpacing = 1.25, Alignment = TextAlignment.Center };

        (double measuredWidth, double height, int lines) = TextMeasurer.Measure(c.Text, fonts, options);

        ParagraphLayout layout = ParagraphLayouter.Layout(c.Text, fonts, options);
        Assert.Equal(layout.Width, measuredWidth);
        Assert.Equal(layout.Height, height);
        Assert.Equal(layout.LineCount, lines);
    }

    [Fact]
    public void The_width_of_an_unlimited_latin_string_is_the_sum_of_its_glyph_advances()
    {
        ShapingFont font = LayoutTestKit.Font(LayoutTestKit.Latin);
        const string text = "Total: 1,234 EUR AVATAR office";

        (double width, _, int lines) = TextMeasurer.Measure(text, [font], new ParagraphOptions { Size = 12 });

        Assert.Equal(1, lines);
        Assert.Equal(Advance(text, font, 12), width, Tolerance);
        Assert.True(width > 0);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    public void The_height_of_a_line_is_the_font_s_ascent_plus_descent_times_the_spacing(double spacing)
    {
        ShapingFont font = LayoutTestKit.Font(LayoutTestKit.Latin);
        double expected = (font.Font.Ascender - font.Font.Descender) * 12.0 / font.Font.UnitsPerEm * spacing;

        (_, double height, int lines) = TextMeasurer.Measure("Quarterly report", [font], new ParagraphOptions { Size = 12, LineSpacing = spacing });

        Assert.Equal(1, lines);
        Assert.Equal(expected, height, Tolerance);
    }

    [Theory]
    [InlineData(40)]
    [InlineData(100)]
    [InlineData(150)]
    [InlineData(420)]
    public void The_line_count_is_that_of_a_greedy_fill_of_words_worked_out_from_their_advances(int width)
    {
        ShapingFont font = LayoutTestKit.Font(LayoutTestKit.Latin);
        const double size = 12;
        string[] words = "report for the whole organisation in the last quarter of this year".Split(' ');
        double space = Advance(" ", font, size);

        // Words are put on a line while the line stays within the width; the space after the last word of a line hangs.
        int lines = 1;
        double used = 0;
        foreach (string word in words)
        {
            double w = Advance(word, font, size);
            if (used > 0 && used + space + w > width + Tolerance)
            {
                lines++;
                used = w;
            }
            else
            {
                used = used > 0 ? used + space + w : w;
            }
        }

        (_, double height, int measured) = TextMeasurer.Measure(string.Join(' ', words), [font], new ParagraphOptions { Size = size, MaxWidth = width });

        Assert.Equal(lines, measured);
        Assert.Equal(lines * (font.Font.Ascender - font.Font.Descender) * size / font.Font.UnitsPerEm, height, Tolerance);
    }

    [Fact]
    public void The_width_grows_with_the_size()
    {
        ShapingFont font = LayoutTestKit.Font(LayoutTestKit.Latin);
        const string text = "Quarterly report for the whole organisation";

        (double small, double smallHeight, _) = TextMeasurer.Measure(text, [font], new ParagraphOptions { Size = 10 });
        (double large, double largeHeight, _) = TextMeasurer.Measure(text, [font], new ParagraphOptions { Size = 20 });

        Assert.Equal(small * 2, large, Tolerance);
        Assert.Equal(smallHeight * 2, largeHeight, Tolerance);
    }

    [Fact]
    public void Text_with_a_paragraph_separator_inside_is_refused()
    {
        ShapingFont[] fonts = LayoutTestKit.Fonts(LayoutTestKit.Latin);

        Assert.Throws<ArgumentException>(() => TextMeasurer.Measure("one\ntwo", fonts, new ParagraphOptions { Size = 12 }));
    }
}
