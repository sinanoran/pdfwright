using DrawingBlock = Pdfwright.Drawing;
using System.Text;
using UglyToad.PdfPig.Content;

namespace Pdfwright.Tests.Document;

/// <summary>What a letter of a file may not do: leave the page.</summary>
internal static class FuzzGeometry
{
    internal static void AssertOnPage(Page page, string name)
    {
        foreach (Letter letter in page.Letters)
        {
            Assert.True(
                letter.BoundingBox.Left >= -1 && letter.BoundingBox.Right <= page.Width + 1 && letter.BoundingBox.Bottom >= -1 && letter.BoundingBox.Top <= page.Height + 1,
                $"{name}: a letter ({letter.Value}) is outside the page: {letter.BoundingBox}");
        }
    }
}

/// <summary>
/// A seeded property test (fixed seeds, bounded size): documents built from strings that mix text in several scripts with every character that has caused trouble (line breaks of every kind,
/// controls, broken surrogates, unassigned and private-use code points), through every text entry point. A save always succeeds and every glyph is on the page.
/// </summary>
public class TextInputFuzzTests
{
    private static readonly string[] Words =
    [
        "Revenue", "KPI", "Zażółć", "naïve", "42,5%", "Привет", "مرحبا", "שלום", "नमस्ते", "สวัสดี", "বাংলা", "தமிழ்", "漢字", "한글", "😀", "é", "‍", "‏", "‮", " ", "  ", "\t", "-",
    ];

    private static readonly string[] Specials = [.. TextInputKit.Specials.Select(s => s.Text).Where(s => s.Length > 0)];

    private static string RandomText(Random random, int maxTokens)
    {
        StringBuilder text = new();
        int tokens = random.Next(0, maxTokens + 1);
        for (int i = 0; i < tokens; i++)
        {
            switch (random.Next(5))
            {
                case 0:
                case 1:
                    _ = text.Append(Words[random.Next(Words.Length)]);
                    break;
                case 2:
                    _ = text.Append(Specials[random.Next(Specials.Length)]);
                    break;
                case 3:
                    _ = text.Append((char)random.Next(0, 0x10000)); // any UTF-16 unit: lone surrogates, controls, private use, unassigned
                    break;
                default:
                    _ = text.Append(char.ConvertFromUtf32(random.Next(0x20, 0x110000) is var cp && cp is >= 0xD800 and <= 0xDFFF ? 0x41 : cp));
                    break;
            }

            if (random.Next(3) == 0)
            {
                _ = text.Append(' ');
            }
        }

        return text.ToString();
    }

    private static readonly TableColumn[] Columns = [new TableColumn(), new TableColumn(), new TableColumn()];

    /// <summary>A random block. The strings that end up as text on the page are added to <paramref name="body"/> in the order the file draws them; the strings that only style the text (a family, a language) are not.</summary>
    private static Block RandomBlock(Random random, List<string> body)
    {
        string T(int max = 6) => RandomText(random, max);
        string B(int max = 6)
        {
            string text = RandomText(random, max);
            body.Add(text);
            return text;
        }

        TextStyle? Styled() => random.Next(2) == 0 ? null : new TextStyle { Family = random.Next(2) == 0 ? null : T(2), Language = random.Next(2) == 0 ? null : T(2), Weight = random.Next(2) == 0 ? null : 700 };

        switch (random.Next(13))
        {
            case 0:
                return new Paragraph(B(12));
            case 1:
                return new Paragraph([new TextRun(B()), new TextRun(B(), new TextStyle { Weight = 700, Language = random.Next(2) == 0 ? null : T(2) }), new TextRun(B(), new TextStyle { Italic = true })]);
            case 2:
                return new Heading(random.Next(1, 7), B());
            case 3:
                return new Table(Columns, new TableRow(B(3), B(3), B(3)), [new TableRow(B(4), B(4), B(4)), new TableRow(B(4), B(4), B(4))]);
            case 4:
                return new Table(Columns, [new TableRow([new TableCell(B()), new TableCell([new Paragraph(B()), new Paragraph(B())]), new TableCell(B())])]);
            case 5:
                return new Columns([[new Paragraph(B())], [new Paragraph(B()), new Heading(2, B())]]);
            case 6:
                return new Callout([new Paragraph(B()), new ItemList([B(3), B(3)])]);
            case 7:
                return new ItemList([new ListItem(B()), new ListItem(new Paragraph(B()))]) { Marker = random.Next(2) == 0 ? ListMarker.Decimal : ListMarker.Bullet };
            case 8:
                string drawn = B(3);
                TextAlignment align = (TextAlignment)random.Next(0, 3);
                return new DrawingBlock(Length.Pt(300), Length.Pt(60), canvas => canvas.DrawText(drawn, 150, 20, null, align));
            case 9:
                body.Add("x");
                return new Paragraph([new TextRun("x", new TextStyle { Family = T(2) })]);
            case 10:
                string styledDrawn = B(3);
                TextStyle? drawStyle = Styled();
                return new DrawingBlock(Length.Pt(300), Length.Pt(60), canvas => canvas.DrawText(styledDrawn, 20, 20, drawStyle));
            case 11:
                return new ItemList([B(3), B(3)]) { Marker = random.Next(2) == 0 ? ListMarker.Decimal : ListMarker.Bullet, MarkerStyle = Styled() ?? new TextStyle { Family = T(2) } };
            default:
                return new Paragraph(B(12)) { Direction = random.Next(3) switch { 0 => TextDirection.LeftToRight, 1 => TextDirection.RightToLeft, _ => TextDirection.Auto } };
        }
    }

    private static PdfDocument RandomDocument(int seed) => RandomDocument(seed, []);

    private static PdfDocument RandomDocument(int seed, List<string> body)
    {
        Random random = new(seed);
        string title = RandomText(random, 4);
        string subject = RandomText(random, 4);
        string author = RandomText(random, 4);
        string creator = RandomText(random, 4);
        string language = random.Next(2) == 0 ? "en" : RandomText(random, 2);
        string header = RandomText(random, 4);
        string footer = RandomText(random, 4);
        PdfDocument document = new(TextInputKit.Tall(b =>
        {
            b.Title = title;
            b.Subject = subject;
            b.Author = author;
            b.Creator = creator;
            b.Language = language;
            b.Header = random.Next(2) == 0 ? null : _ => new Paragraph(header);
            b.Footer = random.Next(2) == 0 ? null : page => new Paragraph(footer + page.Number);
        }));
        int blocks = random.Next(1, 8);
        for (int i = 0; i < blocks; i++)
        {
            _ = document.Add(RandomBlock(random, body));
        }

        return document;
    }

    private const int Documents = 120;

    [Fact]
    public void Random_awkward_strings_in_every_entry_point_always_save_with_every_glyph_on_the_page()
    {
        for (int seed = 1; seed <= Documents; seed++)
        {
            byte[] pdf;
            List<string> body = [];
            try
            {
                pdf = DocumentTestKit.Save(RandomDocument(seed, body));
            }
            catch (Exception e)
            {
                Assert.Fail($"seed {seed}: {e.GetType().Name}: {e.Message}");
                return;
            }

            using UglyToad.PdfPig.PdfDocument read = UglyToad.PdfPig.PdfDocument.Open(pdf);
            foreach (Page page in read.GetPages())
            {
                FuzzGeometry.AssertOnPage(page, $"seed {seed}");
            }

            AssertBodyTextOnPages(read, body, $"seed {seed}");
        }
    }

    /// <summary>
    /// The text layer holds what was laid out: with white space and the combining grapheme joiner left out, the text of the pages contains each body string (as the one rule reads it) in the order
    /// the strings were given, so that a character the rule keeps is not lost on the way to the file and one it removes does not come back.
    /// </summary>
    private static void AssertBodyTextOnPages(UglyToad.PdfPig.PdfDocument read, List<string> body, string name)
    {
        string text = Comparable(string.Concat(read.GetPages().Select(p => p.Text)));
        int at = 0;
        foreach (string input in body)
        {
            string expected = Comparable(Pdfwright.Text.TextNormalizer.Normalize(input));
            int found = text.IndexOf(expected, at, StringComparison.Ordinal);
            Assert.True(found >= 0, $"{name}: the text layer does not have, in order, the text of {Hex(input)} (expected {Hex(expected)})");
            at = found + expected.Length;
        }
    }

    /// <summary>The text without white space and the combining grapheme joiner, as the other extraction tests compare it.</summary>
    private static string Comparable(string text) => DocumentTestKit.Strip(text);

    private static string Hex(string text) => string.Join(' ', text.Select(c => ((int)c).ToString("X4")));

    [Fact]
    public void Random_measured_strings_never_throw_and_never_measure_negative()
    {
        TextMeasurer measurer = new(DocumentTestKit.Fonts);
        Random random = new(7);
        for (int i = 0; i < 400; i++)
        {
            string text = RandomText(random, 10);
            TextMeasurement m = measurer.Measure(text, null, random.Next(2) == 0 ? null : Length.Pt(random.Next(0, 200)));
            Assert.True(m.Width.Points >= 0 && m.Height.Points >= 0 && m.LineCount >= 1, $"measure {i}");
        }
    }

    [Fact]
    public void A_few_random_documents_are_accepted_by_qpdf()
    {
        for (int seed = 1; seed <= 6; seed++)
        {
            DocumentTestKit.AssertValid(DocumentTestKit.Save(RandomDocument(seed * 17)), $"fuzz-{seed}");
        }
    }

    [Fact]
    public void Random_metadata_strings_including_keywords_give_a_well_formed_packet_that_says_what_the_info_dictionary_says()
    {
        Random random = new(11);
        for (int i = 0; i < 80; i++)
        {
            string keywords = RandomText(random, 6);
            string title = RandomText(random, 4);
            using MemoryStream stream = new();
            using (Pdfwright.Pdf.PdfDocumentWriter writer = new(stream, new Pdfwright.Pdf.PdfWriterOptions { Compression = Pdfwright.Pdf.PdfCompression.None, Keywords = keywords, Title = title }))
            {
                writer.BeginPage(100, 100).End();
                writer.Complete();
            }

            string packet = Encoding.UTF8.GetString(stream.ToArray());
            System.Xml.XmlDocument xmp = new();
            xmp.LoadXml(packet[packet.IndexOf("<x:xmpmeta", StringComparison.Ordinal)..(packet.IndexOf("</x:xmpmeta>", StringComparison.Ordinal) + 12)]);
            using UglyToad.PdfPig.PdfDocument read = UglyToad.PdfPig.PdfDocument.Open(stream.ToArray());
            string expectedKeywords = Pdfwright.Text.TextNormalizer.Metadata(keywords);
            Assert.Equal(expectedKeywords.Length == 0 ? null : expectedKeywords, read.Information.Keywords);
            Assert.Equal(expectedKeywords.Length > 0, xmp.InnerXml.Contains("<pdf:Keywords>", StringComparison.Ordinal));
        }
    }
}
