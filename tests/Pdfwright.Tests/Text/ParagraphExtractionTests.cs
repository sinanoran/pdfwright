using Pdfwright.Text;
using Pdfwright.Tests.Pdf;
using UglyToad.PdfPig;

namespace Pdfwright.Tests.Text;

/// <summary>
/// Every corpus line, broken into lines at three widths and drawn line after line, reads back with PdfPig as the text that went in: lines are
/// written one after another and each in logical order, so cutting a line at a cluster must keep the text of the clusters at its edges.
/// </summary>
public class ParagraphExtractionTests
{
    /// <summary>No limit, a column about as wide as a table cell, and a column narrower than most words of the corpus's longest lines.</summary>
    private static readonly int?[] Widths = [null, 200, 90];

    public static TheoryData<string, int?> Cases()
    {
        TheoryData<string, int?> data = [];
        foreach (CorpusTests.Case c in CorpusTests.Load())
        {
            foreach (int? width in Widths)
            {
                data.Add(c.Id, width);
            }
        }

        return data;
    }

    private static string Strip(string text) =>
        string.Concat(text.Where(c => !char.IsWhiteSpace(c) && c != (char)0x034F));

    [Theory]
    [MemberData(nameof(Cases))]
    public void A_corpus_line_laid_out_in_a_column_reads_back_as_it_was_written(string id, int? width)
    {
        CorpusTests.Case c = CorpusTests.Load().Single(x => x.Id == id);
        ShapingFont[] fonts = LayoutTestKit.Fonts(c.Fonts);

        ParagraphLayout layout = LayoutTestKit.Layout(c.Text, fonts, width);
        byte[] pdf = LayoutTestKit.Draw(layout, c.Text, fonts);

        if (Environment.GetEnvironmentVariable("PDFWRIGHT_DUMP") is { Length: > 0 } dump)
        {
            Directory.CreateDirectory(dump);
            File.WriteAllBytes(Path.Combine(dump, $"layout-{id}-{width?.ToString() ?? "none"}.pdf"), pdf);
        }

        using PdfDocument document = PdfDocument.Open(pdf);
        Assert.Equal(Strip(c.Text), Strip(document.GetPage(1).Text));
        if (width == 90)
        {
            PdfTestKit.AssertQpdfAccepts(pdf, "layout-" + id);
        }
    }

    [Fact]
    public void A_column_narrower_than_the_text_makes_several_lines_in_the_corpus()
    {
        // Guards the test above against passing for the wrong reason: at 90 points the long lines really are broken.
        CorpusTests.Case c = LayoutTestKit.Case("latin-de");

        Assert.Equal(1, LayoutTestKit.Layout(c.Text, LayoutTestKit.Fonts(c.Fonts), null).LineCount);
        Assert.True(LayoutTestKit.Layout(c.Text, LayoutTestKit.Fonts(c.Fonts), 90).LineCount >= 4);
    }
}
