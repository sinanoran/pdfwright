using Pdfwright.Drawing;
using Pdfwright.Pdf;
using Pdfwright.Text;
using Pdfwright.Tests.Pdf;
using Pdfwright.Tests.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace Pdfwright.Tests.Drawing;

/// <summary>Text drawn on the canvas, read back from the file with PdfPig: where it is, and that it is not mirrored by the canvas's flip.</summary>
public class CanvasTextTests
{
    private const double PageHeight = 300;
    private const double Tolerance = 0.25;

    private static byte[] Page(Action<Canvas, PdfPageWriter> draw, double width = 400)
    {
        using MemoryStream stream = new();
        using (PdfDocumentWriter document = new(stream, new PdfWriterOptions { Compression = PdfCompression.None, Created = PdfTestKit.Created }))
        {
            PdfPageWriter page = document.BeginPage(width, PageHeight);
            using (Canvas canvas = new(page, 0, PageHeight, width, PageHeight))
            {
                draw(canvas, page);
            }

            page.End();
            document.Complete();
        }

        return stream.ToArray();
    }

    private static IReadOnlyList<Letter> LettersOf(byte[] pdf)
    {
        using UglyToad.PdfPig.PdfDocument document = UglyToad.PdfPig.PdfDocument.Open(pdf);
        return [.. document.GetPage(1).Letters];
    }

    [Fact]
    public void Only_the_fonts_a_text_uses_are_embedded_and_named_in_the_page()
    {
        ShapingFont[] fonts = LayoutTestKit.Fonts(LayoutTestKit.Latin, LayoutTestKit.Arabic);
        int fontEntries = -1;
        byte[] pdf = Page((c, page) =>
        {
            c.DrawText("Hello", fonts, 20, new Point(50, 100));
            fontEntries = ((PdfDictionary)page.BuildResources()["Font"]!).Entries.Count;
        });

        Assert.Equal(1, fontEntries);
        Assert.Equal(1, Count(pdf, "/FontFile2"));

        // With a letter of the fallback in the text, both are used.
        byte[] both = Page((c, page) =>
        {
            c.DrawText("Hello \u0645\u0631\u062D\u0628\u0627", fonts, 20, new Point(50, 100));
            fontEntries = ((PdfDictionary)page.BuildResources()["Font"]!).Entries.Count;
        });
        Assert.Equal(2, fontEntries);
        Assert.Equal(2, Count(both, "/FontFile2"));
    }

    private static int Count(byte[] pdf, string needle)
    {
        string text = System.Text.Encoding.Latin1.GetString(pdf);
        int count = 0;
        for (int at = text.IndexOf(needle, StringComparison.Ordinal); at >= 0; at = text.IndexOf(needle, at + 1, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    [Fact]
    public void Text_is_drawn_upright_with_its_first_baseline_at_the_point()
    {
        ShapingFont[] fonts = LayoutTestKit.Fonts(LayoutTestKit.Latin);
        IReadOnlyList<Letter> letters = LettersOf(Page((c, _) => c.DrawText("Hello", fonts, 20, new Point(50, 100))));

        Assert.Equal("Hello", string.Concat(letters.Select(l => l.Value)));

        // The canvas's y 100 is 200 up from the bottom of this page, and the text starts at x 50.
        Assert.All(letters, l => Assert.Equal(PageHeight - 100, l.StartBaseLine.Y, Tolerance));
        Assert.Equal(50, letters[0].StartBaseLine.X, Tolerance);
        Assert.All(letters, l => Assert.Equal(TextOrientation.Horizontal, l.TextOrientation));

        // Left to right, each glyph after the one before.
        for (int i = 1; i < letters.Count; i++)
        {
            Assert.True(letters[i].StartBaseLine.X > letters[i - 1].StartBaseLine.X);
        }

        // Not mirrored: a capital stands above its baseline (a flipped one would hang below it).
        Letter h = letters[0];
        double baseline = PageHeight - 100;
        Assert.True(h.BoundingBox.Top > baseline + 10, $"top {h.BoundingBox.Top}");
        Assert.True(h.BoundingBox.Bottom > baseline - 2, $"bottom {h.BoundingBox.Bottom}");
    }

    [Fact]
    public void The_box_origin_moves_the_text_with_it()
    {
        ShapingFont[] fonts = LayoutTestKit.Fonts(LayoutTestKit.Latin);
        using MemoryStream stream = new();
        using (PdfDocumentWriter document = new(stream, new PdfWriterOptions { Compression = PdfCompression.None }))
        {
            PdfPageWriter page = document.BeginPage(400, PageHeight);
            using (Canvas canvas = new(page, 40, 250, 200, 100))
            {
                canvas.DrawText("Box", fonts, 14, new Point(10, 30));
            }

            page.End();
            document.Complete();
        }

        IReadOnlyList<Letter> letters = LettersOf(stream.ToArray());
        Assert.Equal(50, letters[0].StartBaseLine.X, Tolerance);
        Assert.Equal(220, letters[0].StartBaseLine.Y, Tolerance);
    }

    [Fact]
    public void Text_under_a_translation_and_a_scale_follows_them_and_is_still_upright()
    {
        ShapingFont[] fonts = LayoutTestKit.Fonts(LayoutTestKit.Latin);
        IReadOnlyList<Letter> letters = LettersOf(Page((c, _) =>
        {
            c.Translate(100, 50);
            c.Scale(2, 2);
            c.DrawText("Ab", fonts, 10, new Point(5, 10));
        }));

        Assert.Equal(110, letters[0].StartBaseLine.X, Tolerance);
        Assert.Equal(PageHeight - 70, letters[0].StartBaseLine.Y, Tolerance);
        Assert.True(letters[0].BoundingBox.Top > letters[0].StartBaseLine.Y + 10);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Alignment_places_the_block_against_the_point_for_left_to_right_text(int alignmentValue)
    {
        TextAlignment alignment = (TextAlignment)alignmentValue;
        ShapingFont[] fonts = LayoutTestKit.Fonts(LayoutTestKit.Latin);
        const string text = "Alignment";
        IReadOnlyList<Letter> letters = LettersOf(Page((c, _) => c.DrawText(text, fonts, 18, new Point(200, 120), alignment)));
        double width = ParagraphMeasurer.Measure(text, fonts, new ParagraphOptions { Size = 18 }).Width;

        double left = letters.Min(l => l.StartBaseLine.X);
        double right = letters.Max(l => l.EndBaseLine.X);
        double expectedLeft = alignment switch
        {
            TextAlignment.Start => 200,
            TextAlignment.Center => 200 - (width / 2),
            _ => 200 - width,
        };

        Assert.Equal(expectedLeft, left, Tolerance);
        Assert.Equal(expectedLeft + width, right, 0.5);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Start_and_end_swap_sides_for_right_to_left_text(int alignmentValue)
    {
        TextAlignment alignment = (TextAlignment)alignmentValue;
        CorpusTests.Case c = LayoutTestKit.Case("arabic-hello");
        ShapingFont[] fonts = LayoutTestKit.Fonts(c.Fonts);
        IReadOnlyList<Letter> letters = LettersOf(Page((canvas, _) => canvas.DrawText(c.Text, fonts, 18, new Point(250, 120), alignment)));
        double width = ParagraphMeasurer.Measure(c.Text, fonts, new ParagraphOptions { Size = 18 }).Width;

        double left = letters.Min(l => l.StartBaseLine.X);
        double right = letters.Max(l => l.EndBaseLine.X);
        if (alignment == TextAlignment.Start)
        {
            Assert.Equal(250, right, 0.5); // the text begins at the right edge
            Assert.Equal(250 - width, left, 0.5);
        }
        else
        {
            Assert.Equal(250, left, 0.5);
            Assert.Equal(250 + width, right, 0.5);
        }
    }

    [Fact]
    public void A_stated_direction_overrides_the_text_s_own()
    {
        ShapingFont[] fonts = LayoutTestKit.Fonts(LayoutTestKit.Latin);
        const string text = "Left to right text";
        IReadOnlyList<Letter> letters = LettersOf(Page((c, _) => c.DrawText(text, fonts, 14, new Point(300, 100), TextAlignment.Start, TextDirection.RightToLeft)));

        // A right-to-left paragraph's start is its right edge, so the text ends at the point.
        Assert.Equal(300, letters.Max(l => l.EndBaseLine.X), 0.5);
    }

    [Fact]
    public void The_lines_of_a_text_with_a_line_break_go_down_the_canvas_by_the_pitch()
    {
        ShapingFont[] fonts = LayoutTestKit.Fonts(LayoutTestKit.Latin);
        string text = "One" + (char)0x2028 + "Two";
        ParagraphLayout layout = ParagraphLayouter.Layout(text, fonts, new ParagraphOptions { Size = 16 });
        IReadOnlyList<Letter> letters = LettersOf(Page((c, _) => c.DrawText(text, fonts, 16, new Point(20, 60))));

        Letter first = letters[0];
        Letter second = letters.First(l => l.Value == "T");
        Assert.Equal(PageHeight - 60, first.StartBaseLine.Y, Tolerance);
        Assert.Equal(layout.Lines[0].Pitch - layout.Lines[0].Ascent + layout.Lines[1].Ascent, first.StartBaseLine.Y - second.StartBaseLine.Y, Tolerance);
        Assert.Equal(20, second.StartBaseLine.X, Tolerance);
    }

    [Fact]
    public void What_is_measured_is_what_is_drawn()
    {
        ShapingFont[] fonts = LayoutTestKit.Fonts(LayoutTestKit.Latin);
        const string text = "Measured and drawn with one engine";
        (double width, _, _) = ParagraphMeasurer.Measure(text, fonts, new ParagraphOptions { Size = 12 });
        IReadOnlyList<Letter> letters = LettersOf(Page((c, _) => c.DrawText(text, fonts, 12, new Point(10, 40))));
        double drawn = letters.Max(l => l.EndBaseLine.X) - letters.Min(l => l.StartBaseLine.X);
        Assert.Equal(width, drawn, 0.5);
    }

    [Fact]
    public void A_brush_sets_the_colour_and_opacity_of_text_and_empty_text_draws_nothing()
    {
        ShapingFont[] fonts = LayoutTestKit.Fonts(LayoutTestKit.Latin);
        Drawn drawn = CanvasTestKit.Run(c =>
        {
            c.DrawText(string.Empty, fonts, 12, new Point(0, 0));
            c.DrawText("x", fonts, 12, new Point(0, 0), brush: new Brush(Color.FromRgb(1.0, 0.0, 0.0), opacity: 0.5));
        });

        Assert.Equal("q", drawn.Lines[0]);
        Assert.Equal("1 0 0 rg", drawn.Lines[2]);
        Assert.Equal("/G1 gs", drawn.Lines[3]);

        // The paragraph is drawn inside a q with the flip back, which the Q after it undoes.
        Assert.Equal("q", drawn.Lines[4]);
        Assert.Equal("1 0 0 -1 0 0 cm", drawn.Lines[5]);
        Assert.Equal("Q", drawn.Lines[^2]);
        Assert.Equal([("G1", 0.5, 1.0)], drawn.States.ToArray());
    }

    [Fact]
    public void The_same_font_is_embedded_once_for_all_the_text_of_a_document()
    {
        ShapingFont[] fonts = LayoutTestKit.Fonts(LayoutTestKit.Latin);
        byte[] pdf = Page((c, _) =>
        {
            c.DrawText("First", fonts, 12, new Point(10, 40));
            c.DrawText("Second", fonts, 12, new Point(10, 80));
        });

        using UglyToad.PdfPig.PdfDocument document = UglyToad.PdfPig.PdfDocument.Open(pdf);
        Assert.Single(document.GetPage(1).Letters.Select(l => l.FontName).Distinct());
        Assert.Equal("FirstSecond", string.Concat(document.GetPage(1).Letters.Select(l => l.Value)));
    }

    [Fact]
    public void A_text_that_is_not_finite_or_has_no_size_is_refused()
    {
        ShapingFont[] fonts = LayoutTestKit.Fonts(LayoutTestKit.Latin);
        Drawn ignored = CanvasTestKit.Run(c =>
        {
            Assert.Throws<ArgumentException>(() => c.DrawText("x", fonts, 12, new Point(double.NaN, 0)));
            Assert.Throws<ArgumentOutOfRangeException>(() => c.DrawText("x", fonts, 0, new Point(0, 0)));
            Assert.Throws<ArgumentOutOfRangeException>(() => c.DrawText("x", fonts, double.PositiveInfinity, new Point(0, 0)));
        });
        Assert.Equal(CanvasTestKit.Wrap(), ignored.Lines);
    }
}
