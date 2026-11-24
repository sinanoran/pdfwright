using System.Text;
using Pdfwright.Pdf;

namespace Pdfwright.Tests.Pdf;

/// <summary>
/// The writer's plumbing for tagged PDF, behind the internal <see cref="PdfWriterOptions.Tagged"/>: marked content with an <c>MCID</c> per page,
/// structure elements written as they close, marked-content references for content on another page, artifacts, a language span, the parent tree,
/// <c>/StructParents</c>, <c>/Tabs</c> and <c>/MarkInfo</c>. The file is read back by <see cref="StructureTreeReader"/> and checked by qpdf.
/// </summary>
public class TaggedWriterTests
{
    /// <summary>
    /// Three pages: a heading, a section with two paragraphs (the second continues on page 2, the first has a span in German), a figure with
    /// alternative text and attributes on page 2, a paragraph and an empty element on page 3; a fill as an artifact on page 1; pages 1 and 2 have a
    /// footer, a pagination artifact drawn when the number of pages is known, as the document renderer draws it.
    /// </summary>
    private static byte[] Tagged(PdfCompression compression)
    {
        MemoryStream memory = new();
        using (PdfDocumentWriter document = new(memory, new PdfWriterOptions
        {
            Compression = compression,
            Title = "Tagged",
            Language = "en",
            Created = PdfTestKit.Created,
            Tagged = true,
        }))
        {
            PdfStructureTree structure = document.Structure!;
            PdfReference helvetica = document.AddObject(new PdfDictionary().Set("Type", "Font").Set("Subtype", "Type1").Set("BaseFont", "Helvetica"));

            PdfPageWriter first = document.BeginPage(595, 842);
            string font = first.UseFont(helvetica);
            first.Content.BeginArtifact().SaveState().FillGray(0.9).Rectangle(40, 700, 300, 60).Fill().RestoreState().EndMarkedContent();

            structure.BeginElement("H1");
            first.BeginMarkedContent();
            first.Content.BeginText().Font(font, 18).TextMove(50, 760).ShowText("Heading").EndText();
            first.EndMarkedContent();
            structure.EndElement();

            PdfStructureElement section = structure.BeginElement("Div");
            PdfStructureElement opening = structure.Create(section, "P");
            PdfStructureElement continued = structure.Create(section, "P");
            first.BeginMarkedContent(opening);
            first.Content.BeginText().Font(font, 11).TextMove(50, 720).ShowText("An opening paragraph, ");
            first.Content.BeginLanguageSpan("de").ShowText("mit Deutsch").EndMarkedContent();
            first.Content.EndText();
            first.EndMarkedContent();

            // The second paragraph starts: the first one is closed and written now.
            first.BeginMarkedContent(continued);
            first.Content.BeginText().Font(font, 11).TextMove(50, 100).ShowText("A paragraph that goes on").EndText();
            first.EndMarkedContent();
            first.EndBody(header: false, footer: true);

            PdfPageWriter second = document.BeginPage(595, 842);
            string font2 = second.UseFont(helvetica);
            second.BeginMarkedContent(continued);
            second.Content.BeginText().Font(font2, 11).TextMove(50, 780).ShowText("on the next page.").EndText();
            second.EndMarkedContent();
            structure.EndElement();

            PdfStructureElement figure = structure.BeginElement(
                "Figure",
                alt: "A grey box",
                attributes: new PdfDictionary().Set("O", "Layout").Set("BBox", PdfArray.Of(50, 600, 150, 700)));
            second.BeginMarkedContent();
            second.Content.SaveState().FillGray(0.5).Rectangle(50, 600, 100, 100).Fill().RestoreState();
            second.EndMarkedContent();
            structure.EndElement();
            Assert.True(figure.IsClosed);
            second.EndBody(header: false, footer: true);

            PdfPageWriter third = document.BeginPage(300, 300);
            string font3 = third.UseFont(helvetica);
            PdfStructureElement last = structure.Create(structure.Document, "P", language: "fr");
            third.BeginMarkedContent(last);
            third.Content.BeginText().Font(font3, 9).TextMove(10, 280).ShowText("Fin.").EndText();
            third.EndMarkedContent();
            structure.Open(structure.Create(structure.Document, "Div"));
            third.End();

            foreach ((PdfPageWriter page, int number) in new[] { (first, 1), (second, 2) })
            {
                page.UseFooter();
                page.Content.BeginPaginationArtifact(PdfPaginationArtifact.Footer)
                    .BeginText().Font(page.UseFont(helvetica), 8).TextMove(50, 20).ShowText($"Page {number} of 3").EndText()
                    .EndMarkedContent();
                page.Finish();
            }

            document.Complete();
        }

        return memory.ToArray();
    }

    [Fact]
    public void The_structure_tree_reads_back_in_drawing_order_with_its_content()
    {        StructureTreeReader tree = StructureTreeReader.Read(Tagged(PdfCompression.None));

        tree.AssertConsistent();
        Assert.Equal("Document[H1,Div[P,P],Figure,P,Div]", tree.Root.Outline());

        List<StructureTreeReader.Element> elements = [.. tree.Root.Descendants()];
        StructureTreeReader.Element heading = elements[1];
        Assert.Equal<StructureTreeReader.Kid>([new StructureTreeReader.ContentKid(0, 0, false)], heading.Kids);

        StructureTreeReader.Element opening = elements[3];
        Assert.Equal<StructureTreeReader.Kid>([new StructureTreeReader.ContentKid(0, 1, false)], opening.Kids);

        // The paragraph that continues: an MCID on its own page, a marked-content reference on the next.
        StructureTreeReader.Element continued = elements[4];
        Assert.Equal(0, continued.Page);
        Assert.Equal<StructureTreeReader.Kid>([new StructureTreeReader.ContentKid(0, 2, false), new StructureTreeReader.ContentKid(1, 0, true)], continued.Kids);

        StructureTreeReader.Element figure = elements[5];
        Assert.Equal("A grey box", figure.Alt);
        Assert.Equal(1, figure.Page);
        Assert.Equal("Layout", ((UglyToad.PdfPig.Tokens.NameToken)figure.Attributes!.Data["O"]).Data);
        Assert.Equal<StructureTreeReader.Kid>([new StructureTreeReader.ContentKid(1, 1, false)], figure.Kids);

        Assert.Equal("fr", elements[6].Language);
        Assert.Empty(elements[7].Kids);
        Assert.Null(elements[7].Page);

        Assert.Equal([0, 1, 2], tree.StructParents.Select(k => k!.Value));
        Assert.Equal(3, tree.ParentTreeNextKey);
        Assert.Equal([3, 2, 1], tree.ContentMcids.Select(m => m.Count));
    }

    [Fact]
    public void Artifacts_and_the_language_span_are_in_the_content_streams()
    {
        StructureTreeReader tree = StructureTreeReader.Read(Tagged(PdfCompression.None));

        Assert.Contains("/Artifact BMC\n", tree.ContentText[0], StringComparison.Ordinal);
        Assert.Contains("/Span <</Lang (de)>> BDC\n", tree.ContentText[0], StringComparison.Ordinal);
        Assert.Contains("/Artifact <</Type /Pagination /Subtype /Footer>> BDC\n", tree.ContentText[0], StringComparison.Ordinal);
        Assert.Contains("/Artifact <</Type /Pagination /Subtype /Footer>> BDC\n", tree.ContentText[1], StringComparison.Ordinal);
        Assert.DoesNotContain("/Artifact", tree.ContentText[2], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Qpdf_accepts_the_tagged_file(bool flate) =>
        PdfTestKit.AssertQpdfAccepts(Tagged(flate ? PdfCompression.Flate : PdfCompression.None), flate ? "tagged-flate" : "tagged-plain");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_same_tagged_document_is_the_same_file_twice(bool flate)
    {
        PdfCompression compression = flate ? PdfCompression.Flate : PdfCompression.None;
        Assert.Equal(Tagged(compression), Tagged(compression));
    }

    [Fact]
    public void An_untagged_document_has_no_structure()
    {
        string file = Encoding.Latin1.GetString(PdfTestKit.ThreePages(PdfCompression.None));

        foreach (string key in new[] { "/StructTreeRoot", "/MarkInfo", "/StructParents", "/Tabs", "/MCID", "BDC" })
        {
            Assert.DoesNotContain(key, file, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_footer_after_the_body_takes_no_marked_content_of_the_structure()
    {
        using PdfDocumentWriter document = new(new MemoryStream(), new PdfWriterOptions { Tagged = true });
        PdfPageWriter page = document.BeginPage(300, 300);
        page.BeginMarkedContent(document.Structure!.BeginElement("P"));
        page.EndMarkedContent();
        page.EndBody(header: false, footer: true);
        page.UseFooter();

        Assert.Throws<InvalidOperationException>(() => page.BeginMarkedContent());
    }

    [Fact]
    public void An_element_closed_by_a_sibling_takes_no_more_content()
    {
        using PdfDocumentWriter document = new(new MemoryStream(), new PdfWriterOptions { Tagged = true });
        PdfStructureTree structure = document.Structure!;
        PdfStructureElement one = structure.Create(structure.Document, "P");
        PdfStructureElement two = structure.Create(structure.Document, "P");
        PdfPageWriter page = document.BeginPage(300, 300);
        page.BeginMarkedContent(one);
        page.EndMarkedContent();
        page.BeginMarkedContent(two);
        page.EndMarkedContent();

        Assert.True(one.IsClosed);
        Assert.Throws<InvalidOperationException>(() => page.BeginMarkedContent(one));
    }

    [Fact]
    public void Marked_content_needs_a_tagged_document()
    {
        using PdfDocumentWriter document = new(new MemoryStream(), new PdfWriterOptions());
        PdfPageWriter page = document.BeginPage(300, 300);

        Assert.Null(document.Structure);
        Assert.Throws<InvalidOperationException>(() => page.BeginMarkedContent());
    }

    [Fact]
    public void A_tagged_document_with_nothing_marked_has_an_empty_document_element()
    {
        MemoryStream memory = new();
        using (PdfDocumentWriter document = new(memory, new PdfWriterOptions { Compression = PdfCompression.None, Tagged = true }))
        {
            document.BeginPage(300, 300).End();
            document.Complete();
        }

        StructureTreeReader tree = StructureTreeReader.Read(memory.ToArray());
        tree.AssertConsistent();
        Assert.Equal("Document", tree.Root.Outline());
        Assert.Empty(tree.Root.Kids);
        PdfTestKit.AssertQpdfAccepts(memory.ToArray(), "tagged-empty");
    }

    [Fact]
    public void The_marked_content_operators_are_written_as_the_format_has_them()
    {
        ContentStreamBuilder content = new();
        content.BeginMarkedContent("P", 12).EndMarkedContent()
            .BeginLanguageSpan("tr-TR").EndMarkedContent()
            .BeginLanguageSpan("x(y)").EndMarkedContent()
            .BeginArtifact().EndMarkedContent()
            .BeginPaginationArtifact(PdfPaginationArtifact.Header).EndMarkedContent();

        Assert.Equal(
            "/P <</MCID 12>> BDC\nEMC\n"
            + "/Span <</Lang (tr-TR)>> BDC\nEMC\n"
            + "/Span <</Lang (x\\(y\\))>> BDC\nEMC\n"
            + "/Artifact BMC\nEMC\n"
            + "/Artifact <</Type /Pagination /Subtype /Header>> BDC\nEMC\n",
            Encoding.ASCII.GetString(content.ToMemory().Span));
        Assert.Throws<ArgumentOutOfRangeException>(() => content.BeginMarkedContent("P", -1));
        Assert.Throws<ArgumentException>(() => PdfStructureTree.CheckType("Not a name"));
    }
}
