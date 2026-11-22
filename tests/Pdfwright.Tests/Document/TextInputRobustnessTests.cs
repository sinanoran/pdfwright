using DrawingBlock = Pdfwright.Drawing;
using System.Text;
using UglyToad.PdfPig.Content;

namespace Pdfwright.Tests.Document;

/// <summary>No string may make a save throw: every text entry point takes every awkward string (line breaks of every kind, controls, broken surrogates, unassigned code points), and the rule that reads them is the same everywhere.</summary>
public class TextInputRobustnessTests
{
    private static readonly string[] LineBreaks =
        ["\n", "\r\n", "\r", ((char)0x2028).ToString(), ((char)0x2029).ToString(), ((char)0x85).ToString(), ((char)0x0B).ToString(), ((char)0x0C).ToString()];

    private static TextMeasurer Measurer => new(DocumentTestKit.Fonts);

    [Fact]
    public void No_entry_point_throws_for_any_awkward_string()
    {
        List<string> failures = [];
        foreach (TextInputKit.EntryPoint entry in TextInputKit.EntryPoints)
        {
            foreach ((string name, string text) in TextInputKit.Cases())
            {
                try
                {
                    entry.Run(text);
                }
                catch (Exception e)
                {
                    failures.Add($"{entry.Name} / {name}: {e.GetType().Name}: {e.Message}");
                }
            }
        }

        Assert.True(failures.Count == 0, $"{failures.Count} failures:\n" + string.Join('\n', failures.Take(80)));
    }

    [Fact]
    public void Every_kind_of_line_break_is_one_hard_line_break_in_the_same_block()
    {
        TextMeasurement one = Measurer.Measure("KPI label");
        Assert.Equal(1, one.LineCount);
        foreach (string lineBreak in LineBreaks)
        {
            TextMeasurement two = Measurer.Measure("KPI" + lineBreak + "label");
            Assert.Equal(2, two.LineCount);
            Assert.Equal(Measurer.Measure("KPI\nlabel").Height, two.Height);
            Assert.Equal(3, Measurer.Measure("a" + lineBreak + lineBreak + "b").LineCount);
        }

        Assert.Equal(2, Measurer.Measure("a\r\nb").LineCount);
        Assert.Equal(3, Measurer.Measure("a\r\n\r\nb").LineCount);
        Assert.Equal(1, Measurer.Measure("a\n").LineCount);
    }

    [Fact]
    public void A_paragraph_separator_does_not_start_a_new_block()
    {
        // One block: the same height as a hard line break, with no paragraph spacing between the lines.
        TextMeasurement separated = Measurer.Measure("one" + (char)0x2029 + "two");
        TextMeasurement broken = Measurer.Measure("one" + (char)0x2028 + "two");
        Assert.Equal(broken.Height, separated.Height);
        Assert.Equal(broken.Width, separated.Width);
    }

    [Fact]
    public void Other_controls_are_removed_and_take_no_room()
    {
        double plain = Measurer.Measure("ab").Width.Points;
        foreach (int control in new[] { 0x0000, 0x0001, 0x0007, 0x0008, 0x001B, 0x001C, 0x001D, 0x001E, 0x001F, 0x007F, 0x0080, 0x0090, 0x009F })
        {
            TextMeasurement m = Measurer.Measure("a" + (char)control + "b");
            Assert.Equal(1, m.LineCount);
            Assert.Equal(plain, m.Width.Points, 6);
        }
    }

    [Fact]
    public void A_surrogate_without_its_pair_is_measured_as_the_replacement_character()
    {
        double replacement = Measurer.Measure("a" + (char)0xFFFD + "b").Width.Points;
        double two = Measurer.Measure("a" + (char)0xFFFD + (char)0xFFFD + "b").Width.Points;
        foreach (string broken in new[] { ((char)0xD800).ToString(), ((char)0xDFFF).ToString(), new string([(char)0xDBFF, (char)0x41]).Substring(0, 1) })
        {
            Assert.Equal(replacement, Measurer.Measure("a" + broken + "b").Width.Points, 6);
        }

        // A reversed pair is two surrogates without their pair: two replacement characters.
        Assert.Equal(two, Measurer.Measure("a" + (char)0xDC00 + (char)0xD800 + "b").Width.Points, 6);
    }

    [Fact]
    public void The_strings_of_the_caller_are_not_changed_by_a_save()
    {
        string text = "a\r\nb" + (char)0x2029 + (char)0 + (char)0xD800;
        TextRun run = new(text);
        Paragraph paragraph = new([run]);
        PdfDocument document = new(TextInputKit.Tall());
        document.Add(paragraph);

        _ = DocumentTestKit.Save(document);

        Assert.Equal(text, run.Text);
        Assert.Equal(text, paragraph.Runs[0].Text);
        Assert.Equal(new Paragraph(text), paragraph);
    }

    [Fact]
    public void A_line_break_in_a_short_label_breaks_the_line_in_a_cell_a_column_and_a_header()
    {
        foreach (string lineBreak in LineBreaks)
        {
            string label = "KPI" + lineBreak + "label";
            PdfDocument document = new(TextInputKit.Tall());
            document.Add(new Columns([[new Paragraph(label)], [new Paragraph("x")]]));
            document.Add(new Table([new TableColumn(), new TableColumn()], new TableRow(label, "h"), [new TableRow(label, "b")]));
            document.Add(new Callout(new Paragraph(label)));
            document.Add(new ItemList([label]));
            document.Add(new Heading(2, label));
            byte[] pdf = DocumentTestKit.Save(document);

            using UglyToad.PdfPig.PdfDocument read = UglyToad.PdfPig.PdfDocument.Open(pdf);
            Page page = read.GetPage(1);
            string text = DocumentTestKit.Strip(page.Text);
            Assert.Equal(6, Count(text, "KPIlabel"));
            Assert.Equal(6, KpiWordsAbove(page));
        }
    }

    private static int Count(string text, string word)
    {
        int count = 0;
        for (int i = text.IndexOf(word, StringComparison.Ordinal); i >= 0; i = text.IndexOf(word, i + word.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    /// <summary>How many of the "KPI" words have their "label" on a line below them (the break is a real break in the geometry).</summary>
    private static int KpiWordsAbove(Page page)
    {
        IReadOnlyList<Letter> letters = page.Letters;
        int count = 0;
        for (int i = 0; i + 5 < letters.Count; i++)
        {
            if (letters[i].Value == "K" && letters[i + 1].Value == "P" && letters[i + 2].Value == "I")
            {
                Letter below = letters.Skip(i + 3).First(l => l.Value == "l");
                if (below.StartBaseLine.Y < letters[i].StartBaseLine.Y - 1)
                {
                    count++;
                }
            }
        }

        return count;
    }

    [Fact]
    public void Metadata_is_written_without_controls_and_with_replaced_surrogates()
    {
        PdfDocument document = new(TextInputKit.Tall(b =>
        {
            b.Title = "Q3\nreport" + (char)0x2028 + "x" + (char)0 + (char)1 + (char)0xD800 + "y";
            b.Author = "A" + (char)0xDC00 + "\tB";
            b.Language = "en\nUS";
        }));
        document.Add(new Paragraph("x"));

        byte[] pdf = DocumentTestKit.Save(document);

        using UglyToad.PdfPig.PdfDocument read = UglyToad.PdfPig.PdfDocument.Open(pdf);
        Assert.Equal("Q3 report x" + (char)0xFFFD + "y", read.Information.Title);
        Assert.Equal("A" + (char)0xFFFD + " B", read.Information.Author);
        string file = Encoding.Latin1.GetString(pdf);
        Assert.DoesNotContain("\n</rdf:li>", file, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_awkward_string_gives_a_valid_file_with_every_glyph_on_the_page()
    {
        foreach ((string name, string text) in TextInputKit.Cases())
        {
            byte[] pdf = AllBlocks(text);
            using UglyToad.PdfPig.PdfDocument read = UglyToad.PdfPig.PdfDocument.Open(pdf);
            foreach (Page page in read.GetPages())
            {
                FuzzGeometry.AssertOnPage(page, name);
            }
        }
    }

    [Fact]
    public void A_document_with_every_awkward_string_is_accepted_by_qpdf()
    {
        foreach ((string name, string text) in TextInputKit.Specials)
        {
            DocumentTestKit.AssertValid(AllBlocks(text), "awkward-" + name.Replace(' ', '-'));
        }
    }

    private static byte[] AllBlocks(string text)
    {
        PdfDocument document = new(TextInputKit.Tall(b =>
        {
            b.Title = text;
            b.Header = _ => new Paragraph(text);
            b.Footer = _ => new Paragraph(text);
        }));
        document.Add(new Paragraph(text));
        document.Add(new Heading(1, text));
        document.Add(new Table([new TableColumn(), new TableColumn()], new TableRow(text, text), [new TableRow(text, "x")]));
        document.Add(new Columns([[new Paragraph(text)], [new Paragraph(text)]]));
        document.Add(new Callout(new Paragraph(text)));
        document.Add(new ItemList([text, text]));
        document.Add(new DrawingBlock(Length.Pt(200), Length.Pt(50), canvas => canvas.DrawText(text, 5, 20)));
        return DocumentTestKit.Save(document);
    }
}
