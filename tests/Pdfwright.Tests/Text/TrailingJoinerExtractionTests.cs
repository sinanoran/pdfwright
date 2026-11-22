using Pdfwright.Text;

namespace Pdfwright.Tests.Text;

/// <summary>A zero width joiner or non-joiner that is the last character of a line has no glyph, yet its text must be carried by the glyph before it so that extraction keeps it, in logical order.</summary>
public class TrailingJoinerExtractionTests
{
    private const char Zwj = (char)0x200D;
    private const char Zwnj = (char)0x200C;
    private const char LineSeparator = (char)0x2028;
    private const string Shalom = "שלום";
    private const string Salam = "سلام";

    private static string Extract(string text, double? width)
    {
        ShapingFont[] fonts = LayoutTestKit.Fonts(LayoutTestKit.Latin, LayoutTestKit.Hebrew, LayoutTestKit.Arabic);
        ParagraphLayout layout = LayoutTestKit.Layout(text, fonts, width);
        using UglyToad.PdfPig.PdfDocument document = UglyToad.PdfPig.PdfDocument.Open(LayoutTestKit.Draw(layout, fonts));
        return string.Concat(document.GetPage(1).Text.Where(c => !char.IsWhiteSpace(c) && c != (char)0x034F));
    }

    public static TheoryData<string, double?> Cases() => new()
    {
        { Shalom + Zwj + LineSeparator + "abc", null },
        { "abc" + Zwnj + LineSeparator + "def", null },
        { "abc" + Zwj + LineSeparator + "abc", null },
        { Shalom + Zwnj + LineSeparator + Shalom + " abc", null },
        { "abc" + Zwj, null },
        { "abc" + Zwj + "\n", null },
        { Shalom + Zwnj + "\n", null },
        { Shalom + " abc" + Zwj + LineSeparator, null },
        { Shalom + " abc" + Zwj + LineSeparator + " ", null },
        { Shalom + " abc" + Zwj + LineSeparator + "e", null },
        { Shalom + " abc" + Zwnj + LineSeparator + " e", null },
        { Shalom + " abc" + Zwj + LineSeparator + Shalom + " abc" + Zwnj + LineSeparator + "x", 40 },
        { Shalom + Zwj, null },
        { Shalom + " abc" + Zwnj, null },
        { "abc" + Zwnj + " def" + Zwj + " ghi", 30 },
        { Shalom + Zwj + " abc" + Zwnj + " " + Shalom + Zwj + " abc", 30 },
        { "abcdef" + Zwj + "ghi", 28 },
        { Shalom + " abc" + Zwj + "   def ghi jkl", 50 },
        { Shalom + " a" + char.ConvertFromUtf32(0x1D400) + Zwj + LineSeparator + "x", null },
        { Salam + " " + (char)0x0661 + (char)0x0662 + (char)0x0651 + Zwj + LineSeparator + "x", null },
        { (char)0x0663 + "" + (char)0x0664 + Zwj + Salam, 20 },
        { "abc " + Shalom + Zwj + LineSeparator + "def", null },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void A_joiner_at_the_end_of_a_line_is_in_the_extracted_text_in_order(string text, double? width)
    {
        string expected = string.Concat(text.Where(c => !char.IsWhiteSpace(c)));
        Assert.Equal(expected, Extract(text, width));
    }
}
