using DrawingBlock = Pdfwright.Drawing;
using System.Diagnostics;
using System.Text;
using System.Xml;
using Pdfwright.Pdf;

namespace Pdfwright.Tests.Document;

/// <summary>Empty lines in linear time, metadata that is trimmed and left out when empty, a family and a language that are read by the rule before they are used or reported, and the line breaks U+001C to U+001E.</summary>
public class TextInputFollowUpTests
{
    private static TextMeasurer Measurer => new(DocumentTestKit.Fonts);

    private static string Unassigned => char.ConvertFromUtf32(0x378);

    [Theory]
    [InlineData(0x000A)]
    [InlineData(0x000C)]
    public void A_hundred_thousand_empty_lines_are_laid_out_in_linear_time(int lineBreak)
    {
        const int Count = 100_000;
        TextMeasurer measurer = Measurer;
        double line = measurer.Measure("a").Height.Points;
        TextMeasurement small = measurer.Measure(new string((char)lineBreak, 3));
        Stopwatch watch = Stopwatch.StartNew();
        TextMeasurement large = measurer.Measure(new string((char)lineBreak, Count));
        watch.Stop();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"{Count} empty lines took {watch.Elapsed.TotalSeconds:F1} s");
        Assert.Equal(small.LineCount + (Count - 3), large.LineCount);
        Assert.Equal(large.LineCount * line, large.Height.Points, 3);
    }

    [Fact]
    public void An_empty_line_is_as_tall_as_a_line_of_text_and_a_break_at_the_end_adds_no_line()
    {
        double line = Measurer.Measure("a").Height.Points;
        Assert.Equal(1, Measurer.Measure("abc\n").LineCount);
        Assert.Equal(1, Measurer.Measure("abc\r\n").LineCount);
        Assert.Equal(2, Measurer.Measure("\nabc").LineCount);
        Assert.Equal(4, Measurer.Measure("a\n\n\nb").LineCount);
        Assert.Equal(4 * line, Measurer.Measure("a\n\n\nb").Height.Points, 3);
        Assert.Equal(4 * line, Measurer.Measure("\n\n\nb").Height.Points, 3);
        Assert.Equal(Measurer.Measure("a\n\n\n").Height.Points, Measurer.Measure("a\n\n\n\n").Height.Points - line, 3);
    }

    [Fact]
    public void The_information_separators_are_line_breaks_in_every_place_text_is_laid_out()
    {
        foreach (int separator in new[] { 0x1C, 0x1D, 0x1E })
        {
            string text = "a" + (char)separator + "b";
            Assert.Equal(2, Measurer.Measure(text).LineCount);
            Assert.Equal(Measurer.Measure("a\nb").Height, Measurer.Measure(text).Height);

            PdfDocument document = new(TextInputKit.Tall());
            document.Add(new Paragraph(text));
            document.Add(new DrawingBlock(Length.Pt(100), Length.Pt(40), canvas => canvas.DrawText(text, 5, 20)));
            byte[] pdf = DocumentTestKit.Save(document);
            Assert.Equal("abab", DocumentTestKit.Strip(DocumentTestKit.PageTexts(pdf)[0]));
        }
    }

    private static (string File, string Packet) Write(PdfWriterOptions options)
    {
        using MemoryStream stream = new();
        using (PdfDocumentWriter writer = new(stream, options))
        {
            writer.BeginPage(100, 100).End();
            writer.Complete();
        }

        byte[] bytes = stream.ToArray();
        string packet = Encoding.UTF8.GetString(bytes);
        int start = packet.IndexOf("<x:xmpmeta", StringComparison.Ordinal);
        int end = packet.IndexOf("</x:xmpmeta>", StringComparison.Ordinal) + "</x:xmpmeta>".Length;
        XmlDocument xmp = new();
        xmp.LoadXml(packet[start..end]); // well formed
        return (Encoding.Latin1.GetString(bytes), packet[start..end]);
    }

    [Fact]
    public void Metadata_is_trimmed_and_the_language_is_a_clean_tag()
    {
        (string file, string packet) = Write(new PdfWriterOptions
        {
            Compression = PdfCompression.None,
            Language = "en-US\n",
            Title = "  \tA title\r\n",
            Author = "\u0001 Ann \u0002",
            Keywords = " one, two\n",
        });

        Assert.Contains("/Lang (en-US)", file);
        Assert.Contains("<rdf:li>en-US</rdf:li>", packet);
        Assert.Contains("/Title (A title)", file);
        Assert.Contains(">A title</rdf:li>", packet);
        Assert.Contains("/Author (Ann)", file);
        Assert.Contains("/Keywords (one, two)", file);
        Assert.Contains("<pdf:Keywords>one, two</pdf:Keywords>", packet);
    }

    [Fact]
    public void An_entry_that_is_empty_after_it_is_read_is_left_out_of_the_info_dictionary_and_the_packet()
    {
        (string file, string packet) = Write(new PdfWriterOptions
        {
            Compression = PdfCompression.None,
            Language = "\n",
            Title = "\u0001\u0002",
            Subject = " \t ",
            Author = "\n",
            Keywords = "\u0000",
            Creator = "\u0085",
        });

        foreach (string key in new[] { "/Title", "/Subject", "/Author", "/Keywords", "/Creator", "/Lang", "/ViewerPreferences" })
        {
            Assert.DoesNotContain(key, file);
        }

        foreach (string element in new[] { "<dc:title>", "<dc:description>", "<dc:creator>", "<dc:language>", "<pdf:Keywords>", "<xmp:CreatorTool>" })
        {
            Assert.DoesNotContain(element, packet);
        }
    }

    [Fact]
    public void The_options_of_a_document_get_the_same_rule()
    {
        PdfDocument document = new(TextInputKit.Tall(b => { b.Language = "en-US\n"; b.Title = "\u0001"; b.Subject = " S "; }));
        document.Add(new Paragraph("x"));
        string file = Encoding.Latin1.GetString(DocumentTestKit.Save(document));

        Assert.Contains("/Lang (en-US)", file);
        Assert.DoesNotContain("/Title", file);
        Assert.DoesNotContain("<dc:title>", file);
        Assert.Contains("/Subject (S)", file);
    }

    [Fact]
    public void The_language_of_a_style_is_read_by_the_rule_before_it_is_reported_and_the_callers_style_is_not_changed()
    {
        TextStyle style = new() { Language = "en-US\n" };
        PdfDocument document = new(TextInputKit.Tall());
        document.Add(new Paragraph([new TextRun("x" + Unassigned, style)]));
        _ = DocumentTestKit.Save(document);

        Assert.Equal("en-US", Assert.Single(document.Diagnostics.MissingGlyphs).Language);
        Assert.Equal("en-US\n", style.Language);

        PdfDocument controls = new(TextInputKit.Tall());
        controls.Add(new Paragraph([new TextRun("x" + Unassigned, new TextStyle { Language = "d\u0000e" + (char)0xD800 })]));
        _ = DocumentTestKit.Save(controls);
        Assert.Equal("de" + (char)0xFFFD, Assert.Single(controls.Diagnostics.MissingGlyphs).Language);

        PdfDocument empty = new(TextInputKit.Tall());
        empty.Add(new Paragraph([new TextRun("x" + Unassigned, new TextStyle { Language = "\n\u0001" })]));
        _ = DocumentTestKit.Save(empty);
        Assert.Null(Assert.Single(empty.Diagnostics.MissingGlyphs).Language);
    }

    [Fact]
    public void The_language_of_the_document_and_of_a_drawn_string_are_read_by_the_rule_too()
    {
        PdfDocument document = new(TextInputKit.Tall(b => b.Language = "fr\u0000-FR\n"));
        document.Add(new Paragraph("x" + Unassigned));
        _ = DocumentTestKit.Save(document);
        Assert.Equal("fr-FR", Assert.Single(document.Diagnostics.MissingGlyphs).Language);

        PdfDocument drawn = new(TextInputKit.Tall());
        drawn.Add(new DrawingBlock(Length.Pt(100), Length.Pt(40), canvas => canvas.DrawText("x" + Unassigned, 5, 20, new TextStyle { Language = " de\n" })));
        _ = DocumentTestKit.Save(drawn);
        Assert.Equal("de", Assert.Single(drawn.Diagnostics.MissingGlyphs).Language);
    }

    [Fact]
    public void The_family_of_a_style_is_read_by_the_rule_before_it_is_reported_and_the_callers_style_is_not_changed()
    {
        TextStyle style = new() { Family = "No\u0000Such\n" };
        PdfDocument document = new(TextInputKit.Tall());
        document.Add(new Paragraph([new TextRun("x", style)]));
        _ = DocumentTestKit.Save(document);

        Assert.Equal("NoSuch", Assert.Single(document.Diagnostics.MissingFamilies));
        Assert.Equal("No\u0000Such\n", style.Family);

        PdfDocument onlyControls = new(TextInputKit.Tall());
        onlyControls.Add(new Paragraph([new TextRun("x", new TextStyle { Family = "\u0001\n" })]));
        _ = DocumentTestKit.Save(onlyControls);
        Assert.Empty(onlyControls.Diagnostics.MissingFamilies);

        PdfDocument drawn = new(TextInputKit.Tall());
        drawn.Add(new DrawingBlock(Length.Pt(100), Length.Pt(40), canvas => canvas.DrawText("x", 5, 20, new TextStyle { Family = "Gone\u0007" })));
        _ = DocumentTestKit.Save(drawn);
        Assert.Equal("Gone", Assert.Single(drawn.Diagnostics.MissingFamilies));
    }

    [Fact]
    public void A_family_with_a_control_in_it_still_finds_the_family_of_the_collection()
    {
        string family = DocumentTestKit.Fonts.Families[0];
        PdfDocument document = new(TextInputKit.Tall());
        document.Add(new Paragraph([new TextRun("x", new TextStyle { Family = family + "\n" })]));
        _ = DocumentTestKit.Save(document);

        Assert.Empty(document.Diagnostics.MissingFamilies);
    }
}
