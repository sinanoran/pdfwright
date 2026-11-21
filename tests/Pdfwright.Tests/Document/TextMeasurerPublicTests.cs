using Pdfwright.Tests.Text;
using Pdfwright.Text;

namespace Pdfwright.Tests.Document;

/// <summary>The public measurer agrees with the engine that draws, to the last bit, and with a document's own layout.</summary>
public class TextMeasurerPublicTests
{
    private static TextMeasurer Measurer(TextStyle? style = null) => new(DocumentTestKit.Fonts, style ?? new TextStyle { Family = "Noto Sans", Size = Length.Pt(10) });

    [Fact]
    public void An_unlimited_string_is_one_line_as_wide_as_the_internal_engine_says()
    {
        const string text = "Total: 1,234 EUR AVATAR office";
        TextMeasurement m = Measurer().Measure(text);

        ShapingFont font = LayoutTestKit.Font(LayoutTestKit.Latin);
        (double width, double height, int lines) = ParagraphMeasurer.Measure(text, [font], new ParagraphOptions { Size = 10 });

        Assert.Equal(1, m.LineCount);
        Assert.Equal(width, m.Width.Points);
        Assert.Equal(height, m.Height.Points);
        Assert.Equal(lines, m.LineCount);
    }

    [Fact]
    public void A_width_makes_lines_and_the_height_is_their_sum()
    {
        TextMeasurer measurer = Measurer();
        string text = DocumentTestKit.LongText(100);

        TextMeasurement one = measurer.Measure("word0");
        TextMeasurement wrapped = measurer.Measure(text, null, Length.Mm(60));

        Assert.True(wrapped.LineCount > 3);
        Assert.True(wrapped.Width <= Length.Mm(60));
        Assert.Equal(one.Height.Points * wrapped.LineCount, wrapped.Height.Points, 6);
    }

    [Fact]
    public void A_larger_size_or_a_bold_weight_changes_the_measure()
    {
        TextMeasurer measurer = Measurer();

        double regular = measurer.Measure("Measure me").Width.Points;
        double big = measurer.Measure("Measure me", new TextStyle { Size = Length.Pt(20) }).Width.Points;
        double bold = measurer.Measure("Measure me", new TextStyle { Weight = 700 }).Width.Points;

        Assert.Equal(2 * regular, big, 6);
        Assert.True(bold > regular);
    }

    [Fact]
    public void The_measure_is_what_the_document_draws()
    {
        // A paragraph of one line in the document spans exactly the measured width.
        const string text = "Measured and drawn are one computation";
        TextMeasurement m = Measurer().Measure(text);
        PdfDocument document = new(DocumentTestKit.Options());
        document.Add(new Paragraph(text));

        using UglyToad.PdfPig.PdfDocument read = UglyToad.PdfPig.PdfDocument.Open(DocumentTestKit.Save(document));
        UglyToad.PdfPig.Content.Letter[] letters = [.. read.GetPage(1).Letters];
        double drawn = letters.Max(l => l.EndBaseLine.X) - letters.Min(l => l.StartBaseLine.X);

        Assert.Equal(m.Width.Points, drawn, 2);
    }

    [Fact]
    public void Right_to_left_text_and_an_empty_string_are_measured()
    {
        TextMeasurer measurer = Measurer();

        TextMeasurement arabic = measurer.Measure("مرحبا بالعالم", new TextStyle { Language = "ar" }, null, TextDirection.RightToLeft);
        TextMeasurement empty = measurer.Measure(string.Empty);

        Assert.True(arabic.Width.Points > 20);
        Assert.Equal(1, empty.LineCount);
        Assert.Equal(0, empty.Width.Points);
        Assert.True(empty.Height.Points > 0);
    }

    [Fact]
    public void A_line_break_in_the_text_makes_a_line()
    {
        Assert.Equal(3, Measurer().Measure("a\nb\nc").LineCount);
    }

    [Fact]
    public void Bad_arguments_are_refused()
    {
        TextMeasurer measurer = Measurer();

        Assert.Throws<ArgumentNullException>(() => measurer.Measure(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => measurer.Measure("x", null, Length.Pt(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => measurer.Measure("x", null, Length.Pt(double.MaxValue) * 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => measurer.Measure("x", new TextStyle { Size = Length.Pt(0) }));
        Assert.Throws<ArgumentException>(() => new TextMeasurer(new FontCollection()));
        Assert.Throws<ArgumentNullException>(() => new TextMeasurer(null!));
    }
}
