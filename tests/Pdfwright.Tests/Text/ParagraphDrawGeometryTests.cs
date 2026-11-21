using Pdfwright.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace Pdfwright.Tests.Text;

/// <summary>Where <see cref="ParagraphLayout.Draw"/> puts the glyphs: read back from the file, with PdfPig, not from the layout.</summary>
public class ParagraphDrawGeometryTests
{
    private const double Tolerance = 0.01;
    private const string Sentence = "The quick brown fox jumps over the lazy dog and runs on";

    private static string Strip(string text) =>
        string.Concat(text.Where(c => !char.IsWhiteSpace(c) && c != (char)0x034F));

    /// <summary>The letters of the page cut into one list for each line of the layout, by counting glyphs (one letter for each glyph code shown).</summary>
    private static List<Letter>[] LettersByLine(IReadOnlyList<Letter> letters, ParagraphLayout layout, int firstGlyph)
    {
        List<Letter>[] byLine = new List<Letter>[layout.LineCount];
        int next = firstGlyph;
        for (int i = 0; i < layout.LineCount; i++)
        {
            int count = layout.Lines[i].Runs.Sum(r => r.Run.Glyphs.Length);
            byLine[i] = [.. letters.Skip(next).Take(count)];
            next += count;
        }

        return byLine;
    }

    private static int GlyphCount(ParagraphLayout layout) => layout.Lines.Sum(l => l.Runs.Sum(r => r.Run.Glyphs.Length));

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    public void Lines_are_drawn_an_ascent_below_their_top_and_a_pitch_apart_and_at_their_x(double spacing)
    {
        ShapingFont[] fonts = LayoutTestKit.Fonts(LayoutTestKit.Latin);
        ParagraphLayout layout = LayoutTestKit.Layout(Sentence, fonts, 150, TextAlignment.Center, lineSpacing: spacing);
        Assert.True(layout.LineCount >= 3);
        Assert.True(layout.Lines.All(l => l.X > 0), "centred lines start away from the left edge");

        using UglyToad.PdfPig.PdfDocument document = UglyToad.PdfPig.PdfDocument.Open(LayoutTestKit.Draw(layout, fonts));
        IReadOnlyList<Letter> letters = document.GetPage(1).Letters;
        Assert.Equal(GlyphCount(layout), letters.Count);
        List<Letter>[] byLine = LettersByLine(letters, layout, 0);

        double top = LayoutTestKit.PageHeight(layout) - LayoutTestKit.TopMargin;
        double lineTop = top;
        for (int i = 0; i < layout.LineCount; i++)
        {
            LaidOutLine line = layout.Lines[i];
            Assert.All(byLine[i], l => Assert.Equal(lineTop - line.Ascent, l.StartBaseLine.Y, Tolerance));
            Assert.Equal(LayoutTestKit.Left + line.Runs[0].X, byLine[i][0].StartBaseLine.X, Tolerance);
            lineTop -= line.Pitch;
        }

        // One line below the other by the pitch (the lines are in one font, so the ascents cancel).
        double pitch = layout.Lines[0].Pitch;
        Assert.Equal(pitch, byLine[0][0].StartBaseLine.Y - byLine[1][0].StartBaseLine.Y, Tolerance);
        Assert.Equal(spacing * (layout.Lines[0].Ascent + layout.Lines[0].Descent), pitch, Tolerance);
        Assert.Equal(Strip(Sentence), Strip(document.GetPage(1).Text));
    }

    [Fact]
    public void A_right_to_left_line_is_drawn_against_the_right_edge_of_its_box()
    {
        CorpusTests.Case c = LayoutTestKit.Case("arabic-hello");
        ShapingFont[] fonts = LayoutTestKit.Fonts(c.Fonts);
        ParagraphLayout layout = LayoutTestKit.Layout(c.Text, fonts, 200);
        LaidOutLine line = Assert.Single(layout.Lines);

        using UglyToad.PdfPig.PdfDocument document = UglyToad.PdfPig.PdfDocument.Open(LayoutTestKit.Draw(layout, fonts));
        IReadOnlyList<Letter> letters = document.GetPage(1).Letters;

        // Start in a right-to-left paragraph is the right edge: the last pixel of the content is at the box's right edge.
        Assert.Equal(LayoutTestKit.Left + 200, letters.Max(l => l.EndBaseLine.X), 0.1);
        Assert.Equal(LayoutTestKit.Left + line.X, letters.Min(l => l.StartBaseLine.X), 0.1);
    }

    [Fact]
    public void Paragraphs_drawn_one_under_the_other_keep_their_order_and_their_distance()
    {
        ShapingFont[] fonts = LayoutTestKit.Fonts(LayoutTestKit.Latin);
        ParagraphLayout first = LayoutTestKit.Layout("First paragraph that wraps onto a second line", fonts, 130);
        ParagraphLayout second = LayoutTestKit.Layout("Second paragraph, also wrapping around", fonts, 130, size: 10);

        using UglyToad.PdfPig.PdfDocument document = UglyToad.PdfPig.PdfDocument.Open(LayoutTestKit.DrawStack([first, second], fonts));
        IReadOnlyList<Letter> letters = document.GetPage(1).Letters;

        Assert.Equal(Strip("First paragraph that wraps onto a second line" + "Second paragraph, also wrapping around"), Strip(document.GetPage(1).Text));
        List<Letter>[] firstLines = LettersByLine(letters, first, 0);
        List<Letter>[] secondLines = LettersByLine(letters, second, GlyphCount(first));
        double top = LayoutTestKit.PageHeight(first, second) - LayoutTestKit.TopMargin;
        Assert.Equal(top - first.Lines[0].Ascent, firstLines[0][0].StartBaseLine.Y, Tolerance);
        Assert.Equal(top - first.Height - second.Lines[0].Ascent, secondLines[0][0].StartBaseLine.Y, Tolerance);
        Assert.True(secondLines[0][0].StartBaseLine.Y < firstLines[^1][0].StartBaseLine.Y);
    }
}
