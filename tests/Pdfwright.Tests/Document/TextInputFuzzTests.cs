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

    private static Block RandomBlock(Random random)
    {
        string T(int max = 6) => RandomText(random, max);
        switch (random.Next(11))
        {
            case 0:
                return new Paragraph(T(12));
            case 1:
                return new Paragraph([new TextRun(T()), new TextRun(T(), new TextStyle { Weight = 700, Language = random.Next(2) == 0 ? null : T(2) }), new TextRun(T(), new TextStyle { Italic = true })]);
            case 2:
                return new Heading(random.Next(1, 7), T());
            case 3:
                return new Table(Columns, new TableRow(T(3), T(3), T(3)), [new TableRow(T(4), T(4), T(4)), new TableRow(T(4), T(4), T(4))]);
            case 4:
                return new Table(Columns, [new TableRow([new TableCell(T()), new TableCell([new Paragraph(T()), new Paragraph(T())]), new TableCell(T())])]);
            case 5:
                return new Columns([[new Paragraph(T())], [new Paragraph(T()), new Heading(2, T())]]);
            case 6:
                return new Callout([new Paragraph(T()), new ItemList([T(3), T(3)])]);
            case 7:
                return new ItemList([new ListItem(T()), new ListItem(new Paragraph(T()))]) { Marker = random.Next(2) == 0 ? ListMarker.Decimal : ListMarker.Bullet };
            case 8:
                string drawn = T(3);
                TextAlignment align = (TextAlignment)random.Next(0, 3);
                return new DrawingBlock(Length.Pt(300), Length.Pt(60), canvas => canvas.DrawText(drawn, 150, 20, null, align));
            case 9:
                return new Paragraph([new TextRun("x", new TextStyle { Family = T(2) })]);
            default:
                return new Paragraph(T(12)) { Direction = random.Next(3) switch { 0 => TextDirection.LeftToRight, 1 => TextDirection.RightToLeft, _ => TextDirection.Auto } };
        }
    }

    private static PdfDocument RandomDocument(int seed)
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
            _ = document.Add(RandomBlock(random));
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
            try
            {
                pdf = DocumentTestKit.Save(RandomDocument(seed));
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
        }
    }

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
}
