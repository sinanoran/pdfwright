namespace Pdfwright.Tests.Document;

/// <summary>
/// The acceptance test at the level of the public document: a multi-page document in Latin, Arabic and Devanagari, laid out in columns of text that
/// wrap and break between pages, reads back with PdfPig as the text that went in, in logical order.
/// </summary>
public class DocumentExtractionTests
{
    private static readonly string[] Ids =
    [
        "latin-de", "latin-tr-sentence", "latin-vi", "greek", "cyrillic-ru", "arabic-sentence", "arabic-diacritics", "arabic-lam-alef", "mixed-en-in-ar",
        "hebrew-sentence", "devanagari-sentence", "devanagari-conjuncts", "devanagari-mixed", "bengali-sentence", "tamil-greeting", "thai-sentence",
    ];

    private static string Language(CorpusTests.Case c) => c.Id.Split('-')[0] switch
    {
        "arabic" or "mixed" => "ar",
        "hebrew" => "he",
        "devanagari" => "hi",
        "bengali" => "bn",
        "tamil" => "ta",
        "thai" => "th",
        "greek" => "el",
        "cyrillic" => "ru",
        _ => c.Id.StartsWith("latin-tr", StringComparison.Ordinal) ? "tr" : "en",
    };

    [Fact]
    public void A_document_in_latin_arabic_and_devanagari_reads_back_in_logical_order_over_several_pages()
    {
        CorpusTests.Case[] cases = [.. Ids.Select(id => CorpusTests.Load().Single(c => c.Id == id))];
        PdfDocument document = new(DocumentTestKit.Options(o =>
        {
            o.Fonts = DocumentTestKit.Fonts;
            o.Margins = Margins.Mm(25, 25, 25, 25);
        }));
        List<string> expected = [];
        document.Add(new Heading(1, "Multi-script report"));
        expected.Add("Multi-script report");
        for (int round = 0; round < 6; round++)
        {
            document.Add(new Heading(2, $"Part {round}"));
            expected.Add($"Part {round}");
            foreach (CorpusTests.Case c in cases)
            {
                // Each case is repeated so that it wraps in the column and the pages fill.
                string text = string.Join(' ', Enumerable.Repeat(c.Text, 9));
                document.Add(new Paragraph(text) { Style = new TextStyle { Language = Language(c) } });
                expected.Add(text);
            }
        }

        byte[] pdf = DocumentTestKit.Save(document);

        string[] pages = DocumentTestKit.PageTexts(pdf);
        Assert.True(pages.Length >= 3, $"{pages.Length} pages");
        Assert.Empty(document.Diagnostics.MissingGlyphs);
        Assert.Equal(DocumentTestKit.Strip(string.Concat(expected)), DocumentTestKit.Strip(string.Concat(pages)));
        DocumentTestKit.AssertValid(pdf, "doc-scripts");
    }

    [Fact]
    public void Runs_in_different_scripts_inside_one_paragraph_read_back_in_order()
    {
        PdfDocument document = new(DocumentTestKit.Options());
        string[] parts = ["Total: ", "مجموع المبيعات ٢٠٢٦", " and ", "कुल बिक्री", " done."];
        document.Add(new Paragraph([.. parts.Select((p, i) => new TextRun(p, i % 2 == 1 ? new TextStyle { Weight = 700, Language = i == 1 ? "ar" : "hi" } : null))]));

        byte[] pdf = DocumentTestKit.Save(document);

        Assert.Empty(document.Diagnostics.MissingGlyphs);
        Assert.Equal(DocumentTestKit.Strip(string.Concat(parts)), DocumentTestKit.Strip(DocumentTestKit.PageTexts(pdf)[0]));
        DocumentTestKit.AssertValid(pdf, "doc-runs-scripts");
    }

    [Fact]
    public void A_right_to_left_document_flows_its_paragraphs_from_the_right()
    {
        PdfDocument document = new(DocumentTestKit.Options(o => o.Direction = TextDirection.RightToLeft));
        string text = string.Join(' ', Enumerable.Repeat("تقرير المبيعات الشهري للسنة المالية الحالية", 12));
        document.Add(new Paragraph(text) { Style = new TextStyle { Language = "ar" } });

        byte[] pdf = DocumentTestKit.Save(document);

        using UglyToad.PdfPig.PdfDocument read = UglyToad.PdfPig.PdfDocument.Open(pdf);
        Assert.Equal(DocumentTestKit.Strip(text), DocumentTestKit.Strip(read.GetPage(1).Text));
        Assert.True(read.GetPage(1).Letters.Select(l => Math.Round(l.StartBaseLine.Y, 1)).Distinct().Count() > 1, "the paragraph wrapped");
    }
}
