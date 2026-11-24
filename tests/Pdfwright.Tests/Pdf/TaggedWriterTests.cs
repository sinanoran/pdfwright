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
    {
        StructureTreeReader tree = StructureTreeReader.Read(Tagged(PdfCompression.None));

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

    /// <summary>
    /// Attributes may be set late, while the element is open (a figure's bounding box when it is drawn), and not once its object is written: then
    /// neither the setter nor the getter can be used, so a change to the dictionary cannot be lost without a word.
    /// </summary>
    [Fact]
    public void The_attributes_are_set_while_the_element_is_open_and_refused_once_it_is_closed()
    {
        MemoryStream memory = new();
        using (PdfDocumentWriter document = new(memory, new PdfWriterOptions { Compression = PdfCompression.None, Tagged = true }))
        {
            PdfStructureTree structure = document.Structure!;
            PdfStructureElement figure = structure.Create(structure.Document, "Figure", alt: "A box");
            PdfPageWriter page = document.BeginPage(300, 300);
            page.BeginMarkedContent(figure);
            page.Content.FillGray(0.5).Rectangle(10, 20, 30, 40).Fill();
            figure.Attributes = new PdfDictionary().Set("O", "Layout").Set("BBox", new PdfArray(new PdfInteger(10), new PdfInteger(20), new PdfInteger(40), new PdfInteger(60)));
            page.EndMarkedContent();
            structure.Close(figure);

            Assert.True(figure.IsClosed);
            Assert.Throws<InvalidOperationException>(() => figure.Attributes = new PdfDictionary());
            Assert.Throws<InvalidOperationException>(() => figure.Attributes!.Set("Placement", "Block"));
            page.End();
            document.Complete();
        }

        StructureTreeReader tree = StructureTreeReader.Read(memory.ToArray());
        tree.AssertConsistent();
        StructureTreeReader.Element written = Assert.Single(tree.Root.Children);
        Assert.Equal("Layout", ((UglyToad.PdfPig.Tokens.NameToken)written.Attributes!.Data["O"]).Data);
        Assert.False(written.Attributes.Data.ContainsKey("Placement"));
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
            .BeginLanguageSpan("x(y)\\").EndMarkedContent()
            .BeginLanguageSpan("xé").EndMarkedContent()
            .BeginArtifact().EndMarkedContent()
            .BeginPaginationArtifact(PdfPaginationArtifact.Header).EndMarkedContent()
            .BeginPaginationArtifact(PdfPaginationArtifact.Repeated).EndMarkedContent();

        Assert.Equal(
            "/P <</MCID 12>> BDC\nEMC\n"
            + "/Span <</Lang (tr-TR)>> BDC\nEMC\n"
            + "/Span <</Lang (x\\(y\\)\\\\)>> BDC\nEMC\n"
            + "/Span <</Lang <FEFF007800E9>>> BDC\nEMC\n"
            + "/Artifact BMC\nEMC\n"
            + "/Artifact <</Type /Pagination /Subtype /Header>> BDC\nEMC\n"
            + "/Artifact <</Type /Pagination>> BDC\nEMC\n",
            Encoding.ASCII.GetString(content.ToMemory().Span));
        Assert.Throws<ArgumentOutOfRangeException>(() => content.BeginMarkedContent("P", -1));
        Assert.Throws<ArgumentException>(() => PdfStructureTree.CheckType("Not a name"));
    }

    [Fact]
    public void The_language_span_is_written_as_the_serializer_writes_the_same_text_string()
    {
        foreach (string language in new[] { "en", "x(y)\\z", "sr-Latn-RS", "xé", "ال" })
        {
            ContentStreamBuilder content = new();
            content.BeginLanguageSpan(language);
            using MemoryStream serialized = new();
            PdfSerializer.Write(PdfString.Text(language), serialized);

            Assert.Equal(
                "/Span <</Lang " + Encoding.Latin1.GetString(serialized.ToArray()) + ">> BDC\n",
                Encoding.Latin1.GetString(content.ToMemory().Span));
        }
    }

    // ---- the order of the tree

    [Fact]
    public void Ancestors_are_materialised_several_levels_deep_under_a_sibling_drawn_before_them()
    {
        MemoryStream memory = new();
        PdfStructureElement[] made;
        using (PdfDocumentWriter document = new(memory, new PdfWriterOptions { Compression = PdfCompression.None, Tagged = true }))
        {
            PdfStructureTree structure = document.Structure!;
            PdfStructureElement before = structure.Create(structure.Document, "P");
            PdfStructureElement table = structure.Create(structure.Document, "Table");
            PdfStructureElement row1 = structure.Create(table, "TR");
            PdfStructureElement cell1 = structure.Create(row1, "TD");
            PdfStructureElement empty = structure.Create(row1, "TD");
            PdfStructureElement row2 = structure.Create(table, "TR");
            PdfStructureElement cell3 = structure.Create(row2, "TD");
            PdfStructureElement after = structure.Create(structure.Document, "P");
            made = [before, table, row1, cell1, empty, row2, cell3, after];

            PdfPageWriter first = document.BeginPage(300, 300);
            Mark(first, before);
            Mark(first, cell1);
            Assert.True(before.IsClosed);
            Assert.True(table.IsOpen && row1.IsOpen && cell1.IsOpen);
            structure.Open(empty);
            Assert.True(cell1.IsClosed);
            first.End();

            PdfPageWriter second = document.BeginPage(300, 300);
            Mark(second, cell3);
            Assert.True(row1.IsClosed && empty.IsClosed);
            Mark(second, table);
            Assert.True(row2.IsClosed && cell3.IsClosed);
            Mark(second, after);
            Assert.True(table.IsClosed);
            second.End();
            document.Complete();
        }

        Assert.All(made, e => Assert.True(e.IsClosed));
        StructureTreeReader tree = StructureTreeReader.Read(memory.ToArray());
        tree.AssertConsistent();
        Assert.Equal("Document[P,Table[TR[TD,TD],TR[TD]],P]", tree.Root.Outline());

        List<StructureTreeReader.Element> elements = [.. tree.Root.Descendants()];
        StructureTreeReader.Element table2 = elements[2];
        Assert.Equal(1, table2.Page);
        Assert.Equal(3, table2.Kids.Count);
        Assert.IsType<StructureTreeReader.ChildKid>(table2.Kids[0]);
        Assert.IsType<StructureTreeReader.ChildKid>(table2.Kids[1]);
        Assert.Equal(new StructureTreeReader.ContentKid(1, 1, false), table2.Kids[2]);
        Assert.Null(elements[3].Page);
        Assert.Equal<StructureTreeReader.Kid>([new StructureTreeReader.ContentKid(0, 1, false)], elements[4].Kids);
        Assert.Empty(elements[5].Kids);
        Assert.Equal<StructureTreeReader.Kid>([new StructureTreeReader.ContentKid(1, 0, false)], elements[7].Kids);
        Assert.Equal<StructureTreeReader.Kid>([new StructureTreeReader.ContentKid(1, 2, false)], elements[8].Kids);
        PdfTestKit.AssertQpdfAccepts(memory.ToArray(), "tagged-table");
    }

    /// <summary>
    /// A row that keeps its cells open, as a table row split between pages does: the first cell takes content on the next page after the second cell's
    /// on this one, a marked-content reference under the same element; the cells close with the row, and the next row opens as a sibling does.
    /// </summary>
    [Fact]
    public void A_row_that_keeps_its_cells_open_returns_to_a_cell_on_the_next_page_and_closes_them_with_it()
    {
        MemoryStream memory = new();
        using (PdfDocumentWriter document = new(memory, new PdfWriterOptions { Compression = PdfCompression.None, Tagged = true }))
        {
            PdfStructureTree structure = document.Structure!;
            PdfStructureElement table = structure.Create(structure.Document, "Table");
            PdfStructureElement row = structure.Create(table, "TR");
            row.KeepsChildrenOpen = true;
            PdfStructureElement first = structure.Create(row, "TD");
            PdfStructureElement empty = structure.Create(row, "TD");
            PdfStructureElement last = structure.Create(row, "TD");
            PdfStructureElement next = structure.Create(table, "TR");
            PdfStructureElement nextCell = structure.Create(next, "TD");

            PdfPageWriter one = document.BeginPage(300, 300);
            structure.Open(first);
            structure.Open(empty);
            structure.Open(last);
            Mark(one, first);
            Mark(one, last);
            Assert.True(first.IsOpen && empty.IsOpen && last.IsOpen);
            one.End();

            PdfPageWriter two = document.BeginPage(300, 300);
            Mark(two, first);
            Mark(two, last);
            structure.Close(row);
            Assert.True(first.IsClosed && empty.IsClosed && last.IsClosed && row.IsClosed);
            Assert.Throws<InvalidOperationException>(() => two.BeginMarkedContent(first));
            Mark(two, nextCell);
            two.End();
            document.Complete();
        }

        StructureTreeReader tree = StructureTreeReader.Read(memory.ToArray());
        tree.AssertConsistent();
        Assert.Equal("Document[Table[TR[TD,TD,TD],TR[TD]]]", tree.Root.Outline());
        List<StructureTreeReader.Element> cells = [.. tree.Root.Descendants().Where(e => e.Type == "TD")];
        Assert.Equal<StructureTreeReader.Kid>([new StructureTreeReader.ContentKid(0, 0, false), new StructureTreeReader.ContentKid(1, 0, true)], cells[0].Kids);
        Assert.Empty(cells[1].Kids);
        Assert.Equal<StructureTreeReader.Kid>([new StructureTreeReader.ContentKid(0, 1, false), new StructureTreeReader.ContentKid(1, 1, true)], cells[2].Kids);
        Assert.Equal<StructureTreeReader.Kid>([new StructureTreeReader.ContentKid(1, 2, false)], cells[3].Kids);
        PdfTestKit.AssertQpdfAccepts(memory.ToArray(), "tagged-row-kept-open");
    }

    [Fact]
    public void Content_of_an_element_after_its_child_closes_the_child_and_keeps_the_order()
    {
        MemoryStream memory = new();
        using (PdfDocumentWriter document = new(memory, new PdfWriterOptions { Compression = PdfCompression.None, Tagged = true }))
        {
            PdfStructureTree structure = document.Structure!;
            PdfStructureElement division = structure.Create(structure.Document, "Div");
            PdfStructureElement inner = structure.Create(division, "P");
            PdfPageWriter page = document.BeginPage(300, 300);
            Mark(page, division);
            Mark(page, inner);
            Mark(page, division);
            Assert.True(inner.IsClosed);
            Assert.Throws<InvalidOperationException>(() => page.BeginMarkedContent(inner));
            page.End();
            document.Complete();
        }

        StructureTreeReader tree = StructureTreeReader.Read(memory.ToArray());
        tree.AssertConsistent();
        StructureTreeReader.Element division2 = tree.Root.Children.Single();
        Assert.Equal(3, division2.Kids.Count);
        Assert.Equal(new StructureTreeReader.ContentKid(0, 0, false), division2.Kids[0]);
        Assert.Equal("P", Assert.IsType<StructureTreeReader.ChildKid>(division2.Kids[1]).Element.Type);
        Assert.Equal(new StructureTreeReader.ContentKid(0, 2, false), division2.Kids[2]);
    }

    private static void Mark(PdfPageWriter page, PdfStructureElement element)
    {
        page.BeginMarkedContent(element);
        page.Content.FillGray(0).Rectangle(10, 10, 5, 5).Fill();
        page.EndMarkedContent();
    }

    // ---- misuse that would leave a broken file

    [Fact]
    public void The_document_element_is_closed_only_by_completing_and_a_refused_close_writes_nothing()
    {
        MemoryStream memory = new();
        using (PdfDocumentWriter document = new(memory, new PdfWriterOptions { Compression = PdfCompression.None, Tagged = true }))
        {
            PdfStructureTree structure = document.Structure!;
            PdfPageWriter page = document.BeginPage(300, 300);
            Mark(page, structure.BeginElement("P"));
            long written = memory.Length;

            Assert.Throws<InvalidOperationException>(() => structure.Close(structure.Document));
            Assert.Equal(written, memory.Length);
            Assert.True(structure.Document.IsOpen);

            page.End();
            document.Complete();
        }

        StructureTreeReader tree = StructureTreeReader.Read(memory.ToArray());
        tree.AssertConsistent();
        Assert.Equal("Document[P]", tree.Root.Outline());
    }

    [Fact]
    public void Parent_tree_keys_are_refused_out_of_order()
    {
        using PdfDocumentWriter document = new(new MemoryStream(), new PdfWriterOptions { Tagged = true });
        PdfStructureTree structure = document.Structure!;
        PdfReference entry = document.AllocateObject();
        Assert.Equal(0, structure.TakeKey());
        Assert.Equal(1, structure.TakeKey());

        structure.AddEntry(1, entry);
        Assert.Throws<InvalidOperationException>(() => structure.AddEntry(0, entry));
        Assert.Throws<InvalidOperationException>(() => structure.AddEntry(1, entry));
        Assert.Throws<InvalidOperationException>(() => structure.AddEntry(2, entry));
    }

    [Fact]
    public void An_EMC_without_its_BDC_is_refused()
    {
        ContentStreamBuilder content = new();
        Assert.Throws<InvalidOperationException>(() => content.EndMarkedContent());
        content.BeginArtifact().EndMarkedContent();
        Assert.Throws<InvalidOperationException>(() => content.EndMarkedContent());
        Assert.Equal("/Artifact BMC\nEMC\n", Encoding.ASCII.GetString(content.ToMemory().Span));
    }

    [Fact]
    public void Marked_content_does_not_nest_in_marked_content_and_artifacts_do_not_mix_with_it()
    {
        ContentStreamBuilder content = new();
        content.BeginMarkedContent("P", 0);
        Assert.Throws<InvalidOperationException>(() => content.BeginMarkedContent("P", 1));
        Assert.Throws<InvalidOperationException>(() => content.BeginArtifact());
        Assert.Throws<InvalidOperationException>(() => content.BeginPaginationArtifact(PdfPaginationArtifact.Footer));

        // A language span or an ActualText span inside the element's content is what they are for; inside them the rule still holds.
        content.BeginLanguageSpan("de").BeginActualText("x");
        Assert.Throws<InvalidOperationException>(() => content.BeginMarkedContent("P", 1));
        content.EndMarkedContent().EndMarkedContent().EndMarkedContent();
        Assert.Equal(0, content.MarkedContentDepth);

        content.BeginArtifact().BeginArtifact().BeginLanguageSpan("de");
        Assert.Throws<InvalidOperationException>(() => content.BeginMarkedContent("P", 1));
        content.EndMarkedContent().EndMarkedContent();
        Assert.Throws<InvalidOperationException>(() => content.BeginMarkedContent("P", 1));
        content.EndMarkedContent();

        // Both closed: marked content may begin again.
        content.BeginMarkedContent("P", 1).EndMarkedContent();
        Assert.Equal(0, content.MarkedContentDepth);
    }

    [Fact]
    public void A_refused_nested_MCID_leaves_the_page_and_the_tree_as_they_were()
    {
        MemoryStream memory = new();
        using (PdfDocumentWriter document = new(memory, new PdfWriterOptions { Compression = PdfCompression.None, Tagged = true }))
        {
            PdfStructureTree structure = document.Structure!;
            PdfStructureElement first = structure.Create(structure.Document, "P");
            PdfStructureElement second = structure.Create(structure.Document, "P");
            PdfPageWriter page = document.BeginPage(300, 300);
            page.BeginMarkedContent(first);
            Assert.Throws<InvalidOperationException>(() => page.BeginMarkedContent(second));
            Assert.Null(second.Reference);
            Assert.True(first.IsOpen);
            page.EndMarkedContent();

            page.Content.BeginArtifact();
            Assert.Throws<InvalidOperationException>(() => page.BeginMarkedContent(second));
            page.EndMarkedContent();

            Assert.Equal(1, page.BeginMarkedContent(second));
            page.EndMarkedContent();
            page.End();
            document.Complete();
        }

        StructureTreeReader tree = StructureTreeReader.Read(memory.ToArray());
        tree.AssertConsistent();
        Assert.Equal("Document[P,P]", tree.Root.Outline());
    }

    [Fact]
    public void A_page_part_with_marked_content_left_open_is_not_written()
    {
        using PdfDocumentWriter document = new(new MemoryStream(), new PdfWriterOptions { Tagged = true });
        PdfPageWriter page = document.BeginPage(300, 300);
        page.Content.BeginArtifact();
        Assert.Throws<InvalidOperationException>(() => page.End());
        Assert.Throws<InvalidOperationException>(() => page.EndBody(header: false, footer: true));
        page.EndMarkedContent();

        page.EndBody(header: false, footer: true);
        page.UseFooter();
        page.Content.BeginPaginationArtifact(PdfPaginationArtifact.Footer);
        Assert.Throws<InvalidOperationException>(() => page.Finish());
        page.EndMarkedContent();
        page.Finish();
        document.Complete();
    }

    [Fact]
    public void An_untagged_page_with_marked_content_left_open_is_not_written_either()
    {
        using PdfDocumentWriter document = new(new MemoryStream(), new PdfWriterOptions());
        PdfPageWriter page = document.BeginPage(300, 300);
        page.Content.BeginActualText("x");
        Assert.Throws<InvalidOperationException>(() => page.End());
        page.Content.EndMarkedContent();
        page.End();
        document.Complete();
    }
}
