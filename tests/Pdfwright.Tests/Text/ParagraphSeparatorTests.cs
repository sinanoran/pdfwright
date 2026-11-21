using Pdfwright.Text;
using UglyToad.PdfPig;

namespace Pdfwright.Tests.Text;

/// <summary>The separator that ends a paragraph belongs to its line but is never drawn and adds no line.</summary>
public class ParagraphSeparatorTests
{
    public static TheoryData<string> Separators() =>
        ["\r\n", "\r", "\n", ((char)0x0085).ToString(), ((char)0x2029).ToString()];

    [Theory]
    [MemberData(nameof(Separators))]
    public void A_separator_at_the_end_is_in_the_line_but_adds_no_line_and_is_not_drawn(string separator)
    {
        ShapingFont[] fonts = LayoutTestKit.Fonts(LayoutTestKit.Latin);
        string text = "one" + separator;

        ParagraphLayout layout = LayoutTestKit.Layout(text, fonts, null);

        LaidOutLine line = Assert.Single(layout.Lines);
        Assert.Equal(text.Length, line.Length);
        Assert.Equal(LayoutTestKit.Layout("one", fonts, null).Width, line.Width, 1e-6);
        Assert.All(line.Runs, r => Assert.True(r.Run.Utf16Start + r.Run.Utf16Length <= 3));
        Assert.All(line.Runs.SelectMany(r => r.Run.Glyphs), g => Assert.InRange(g.Cluster, 0, 2));

        byte[] pdf = LayoutTestKit.Draw(layout, text, fonts);
        using PdfDocument document = PdfDocument.Open(pdf);
        Assert.Equal("one", document.GetPage(1).Text);
    }

    [Fact]
    public void A_carriage_return_line_feed_pair_in_the_middle_of_a_text_is_refused_like_any_separator()
    {
        ShapingFont[] fonts = LayoutTestKit.Fonts(LayoutTestKit.Latin);

        Assert.Throws<ArgumentException>(() => LayoutTestKit.Layout("one\r\ntwo", fonts, null));
    }
}
