using System.Text;
using System.Text.RegularExpressions;
using Pdfwright.Tests.Pdf;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Tokens;

namespace Pdfwright.Tests.Document;

/// <summary>
/// The document layer of tagged PDF for tables, written with <see cref="PdfDocumentOptions.Tagged"/> (the default): a table is a <c>Table</c> with a
/// <c>THead</c> of <c>TR</c> rows of <c>TH</c> column headers (<c>/Scope /Column</c>) and a <c>TBody</c> of <c>TR</c> rows of <c>TD</c> cells, a
/// cell for each column in logical order (a header with no body has its <c>TR</c> rows straight under the <c>Table</c>); the fills and borders are artifacts outside the cells' marked content, a header row repeated on a later page
/// is a pagination artifact, and a row split between pages is one row whose cells have marked content on both pages. The documents are made through
/// the public API and read back by <see cref="StructureTreeReader"/>.
/// </summary>
public partial class TaggedTableTests
{
    private static PdfDocumentOptions Tagged(Action<DocumentTestKit.OptionsBuilder>? change = null) => TaggedFlowTests.Tagged(change);

    private static StructureTreeReader Read(byte[] pdf) => TaggedFlowTests.Read(pdf);

    private static List<(string Type, string Text)> ElementTexts(byte[] pdf, StructureTreeReader tree) => TaggedFlowTests.ElementTexts(pdf, tree);

    private static TableColumn[] Columns(int count) => [.. Enumerable.Range(0, count).Select(_ => new TableColumn())];

    private static string Name(StructureTreeReader.Element element, string key) => ((NameToken)element.Attributes!.Data[key]).Data;

    /// <summary>The outline of a table of <paramref name="columns"/> columns with header and body rows of a cell for each column, as <see cref="StructureTreeReader.Element.Outline"/> writes it.</summary>
    internal static string TableOutline(int columns, int headerRows, int bodyRows)
    {
        string Rows(int count, string cell) => string.Join(",", Enumerable.Repeat($"TR[{string.Join(",", Enumerable.Repeat(cell, columns))}]", count));
        if (bodyRows == 0)
        {
            // A header and no body: the header rows are straight under the Table, with no THead (PDF/UA-1 wants a TBody after a THead).
            return $"Table[{Rows(headerRows, "TH")}]";
        }

        List<string> parts = [];
        if (headerRows > 0)
        {
            parts.Add($"THead[{Rows(headerRows, "TH")}]");
        }

        if (bodyRows > 0)
        {
            parts.Add($"TBody[{Rows(bodyRows, "TD")}]");
        }

        return $"Table[{string.Join(",", parts)}]";
    }

    /// <summary>The cells of the tree (<c>TH</c> and <c>TD</c>), in the tree's order.</summary>
    private static List<StructureTreeReader.Element> Cells(StructureTreeReader tree) => [.. tree.Root.Descendants().Where(e => e.Type is "TH" or "TD")];

    /// <summary>The left edge of the letters of each marked content with an <c>MCID</c> of a page, by number.</summary>
    private static Dictionary<int, double> Lefts(byte[] pdf, int page)
    {
        using UglyToad.PdfPig.PdfDocument read = UglyToad.PdfPig.PdfDocument.Open(pdf);
        return DocumentTestKit.Pages(read)[page].GetMarkedContents()
            .Where(e => !e.IsArtifact && e.MarkedContentIdentifier >= 0 && e.Letters.Count > 0)
            .ToDictionary(e => e.MarkedContentIdentifier, e => e.Letters.Min(l => l.BoundingBox.Left));
    }

    [Theory]
    [InlineData(TextDirection.LeftToRight)]
    [InlineData(TextDirection.RightToLeft)]
    public void A_table_is_a_header_of_column_headers_and_a_body_of_rows_with_the_cells_in_logical_order(TextDirection direction)
    {
        PdfDocument document = new(Tagged());
        document.Add(new Paragraph("Before."));
        document.Add(new Table(Columns(3), new TableRow("Name", "Qty", "Price"), [new TableRow("Apple", "3", "1.20"), new TableRow("Pear", "5", "0.80")])
        {
            Direction = direction,
            Borders = Borders.All,
        });
        document.Add(new Paragraph("After."));

        byte[] pdf = DocumentTestKit.Save(document);

        StructureTreeReader tree = Read(pdf);
        Assert.Equal("Document[P," + TableOutline(3, 1, 2) + ",P]", tree.Root.Outline());
        Assert.Equal(
            [
                ("P", "Before."), ("Table", ""), ("THead", ""), ("TR", ""), ("TH", "Name"), ("TH", "Qty"), ("TH", "Price"),
                ("TBody", ""), ("TR", ""), ("TD", "Apple"), ("TD", "3"), ("TD", "1.20"), ("TR", ""), ("TD", "Pear"), ("TD", "5"), ("TD", "0.80"),
                ("P", "After."),
            ],
            ElementTexts(pdf, tree));
        foreach (StructureTreeReader.Element header in tree.Root.Descendants().Where(e => e.Type == "TH"))
        {
            Assert.Equal("Table", Name(header, "O"));
            Assert.Equal("Column", Name(header, "Scope"));
        }

        Assert.All(tree.Root.Descendants().Where(e => e.Type != "TH"), e => Assert.Null(e.Attributes));

        // The cells are drawn and read first column first; the first column stands on the right in a right-to-left table.
        Assert.Equal(["P", "TH", "TH", "TH", "TD", "TD", "TD", "TD", "TD", "TD", "P"], tree.ContentMcids[0].Select(m => m.Tag));
        Dictionary<int, double> lefts = Lefts(pdf, 0);
        if (direction == TextDirection.RightToLeft)
        {
            Assert.True(lefts[1] > lefts[2] && lefts[2] > lefts[3], "the first column is on the right");
        }
        else
        {
            Assert.True(lefts[1] < lefts[2] && lefts[2] < lefts[3], "the first column is on the left");
        }

        DocumentTestKit.AssertValid(pdf, $"tagged-table-simple-{direction}");
    }

    [Fact]
    public void A_cell_of_one_paragraph_holds_its_text_and_a_cell_of_several_has_a_p_for_each()
    {
        TextStyle french = new() { Language = "fr" };
        PdfDocument document = new(Tagged());
        document.Add(new Table(
            Columns(3),
            [
                new TableRow(
                    new TableCell("One"),
                    new TableCell([new Paragraph("First"), new Paragraph(" "), new Paragraph("Second") { Style = french }]),
                    new TableCell(new Paragraph("Bonjour") { Style = french })),
            ]));

        byte[] pdf = DocumentTestKit.Save(document);

        StructureTreeReader tree = Read(pdf);
        Assert.Equal("Document[Table[TBody[TR[TD,TD[P,P],TD]]]]", tree.Root.Outline());
        Assert.Equal(
            [("Table", ""), ("TBody", ""), ("TR", ""), ("TD", "One"), ("TD", ""), ("P", "First"), ("P", "Second"), ("TD", "Bonjour")],
            ElementTexts(pdf, tree));
        List<StructureTreeReader.Element> elements = [.. tree.Root.Descendants()];
        Assert.Null(elements[4].Language);
        Assert.Equal("fr", elements[7].Language);
        Assert.Equal("fr", elements[8].Language);
        Assert.Null(elements[5].Language);

        // A cell of several paragraphs has no content of its own: its paragraphs have it.
        Assert.All(elements[5].Kids, k => Assert.IsType<StructureTreeReader.ChildKid>(k));
        DocumentTestKit.AssertValid(pdf, "tagged-table-paragraphs");
    }

    /// <summary>
    /// Every row of the structure has a cell for each column, so that a cell's column header is the one at its place (a table has no spans): an empty or
    /// blank cell is an empty <c>TD</c>, and so is each cell a short row does not have; in the header they are <c>TH</c> with their scope.
    /// </summary>
    [Fact]
    public void Empty_cells_and_the_missing_cells_of_a_short_row_are_empty_cells()
    {
        PdfDocument document = new(Tagged());
        document.Add(new Table(
            Columns(3),
            new TableRow("A", "B"),
            [new TableRow("x", "", "z"), new TableRow("only"), new TableRow(new TableCell("   "), new TableCell("y"), new TableCell("w"))])
        {
            Borders = Borders.All,
        });

        byte[] pdf = DocumentTestKit.Save(document);

        StructureTreeReader tree = Read(pdf);
        Assert.Equal("Document[" + TableOutline(3, 1, 3) + "]", tree.Root.Outline());
        Assert.Equal(
            ["A", "B", "", "x", "", "z", "only", "", "", "", "y", "w"],
            ElementTexts(pdf, tree).Where(e => e.Type is "TH" or "TD").Select(e => e.Text));
        List<StructureTreeReader.Element> cells = Cells(tree);
        foreach (int empty in new[] { 2, 4, 7, 8, 9 })
        {
            Assert.Empty(cells[empty].Kids);
        }

        Assert.Equal("Column", Name(cells[2], "Scope"));
        Assert.Equal(7, tree.ContentMcids[0].Count);
        DocumentTestKit.AssertValid(pdf, "tagged-table-empty-cells");
    }

    /// <summary>
    /// The header rows repeated at the top of a later page are a pagination artifact (with their fills and borders): the table has one <c>THead</c>, and
    /// a screen reader reads the header once. Without the repetition there is no such artifact.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_header_repeated_on_a_later_page_is_a_pagination_artifact_and_the_table_has_one_header(bool repeat)
    {
        PdfDocument document = new(Tagged());
        document.Add(new Table(Columns(2), new TableRow("Name", "Value"), Enumerable.Range(0, 90).Select(i => new TableRow($"Row {i}", $"{i}")))
        {
            RepeatHeader = repeat,
            HeaderFill = Color.Gray(0.9),
            Borders = Borders.All,
        });

        byte[] pdf = DocumentTestKit.Save(document);

        StructureTreeReader tree = Read(pdf);
        Assert.True(tree.ContentStreams.Count >= 2);
        Assert.Equal("Document[" + TableOutline(2, 1, 90) + "]", tree.Root.Outline());
        Assert.Equal(["Name", "Value"], ElementTexts(pdf, tree).Where(e => e.Type == "TH").Select(e => e.Text));
        Assert.Equal(Enumerable.Range(0, 90).Select(i => $"Row{i}"), ElementTexts(pdf, tree).Where(e => e.Type == "TD").Where((_, i) => i % 2 == 0).Select(e => e.Text));
        Assert.DoesNotContain("/Type /Pagination>>", tree.ContentText[0], StringComparison.Ordinal);
        Assert.Equal("TD", tree.ContentMcids[1][0].Tag);
        string[] artifacts = TaggedFlowTests.ArtifactTexts(pdf);
        Assert.Equal("", artifacts[0]);
        for (int page = 1; page < tree.ContentStreams.Count; page++)
        {
            int repeated = Regex.Count(tree.ContentText[page], @"/Artifact <</Type /Pagination>> BDC");
            Assert.Equal(repeat ? 1 : 0, repeated);
            Assert.Equal(repeat ? "NameValue" : "", artifacts[page]);
            AssertRepeatedHeaderHasNoStructure(tree.ContentText[page], repeat ? 1 : 0);
        }

        DocumentTestKit.AssertValid(pdf, $"tagged-table-repeated-header-{repeat}");
    }

    /// <summary>
    /// The page begins with <paramref name="rows"/> pagination artifacts, one for each header row drawn again, then the body's cells; nothing inside
    /// such an artifact is marked content of the structure (no <c>MCID</c>, nested at any depth), so a reader reads the header once.
    /// </summary>
    private static void AssertRepeatedHeaderHasNoStructure(string content, int rows)
    {
        List<(string Begin, string Body)> repeated = [.. Sequences(content).Where(s => s.Begin.Contains("/Type /Pagination>>", StringComparison.Ordinal))];
        Assert.Equal(rows, repeated.Count);
        Assert.All(repeated, s => Assert.DoesNotContain("/MCID", s.Body, StringComparison.Ordinal));
        Assert.All(repeated, s => Assert.Matches(@"T[Jj]\n", s.Body));
        List<string> marks = Marks(content);
        Assert.Equal(Enumerable.Repeat("R", rows), marks.TakeWhile(m => m == "R"));
        Assert.Equal("TD", marks.SkipWhile(m => m is "R" or "A").First());
    }

    [Fact]
    public void A_header_taller_than_half_a_page_is_not_repeated_and_is_the_header_once()
    {
        PdfDocument document = new(Tagged());
        document.Add(new Table(
            [new TableColumn(ColumnWidth.Fixed(Length.Mm(25))), new TableColumn()],
            new TableRow(DocumentTestKit.LongText(70), "Value"),
            Enumerable.Range(0, 60).Select(i => new TableRow($"Row {i}", $"{i}"))));

        byte[] pdf = DocumentTestKit.Save(document);

        StructureTreeReader tree = Read(pdf);
        Assert.True(tree.ContentStreams.Count >= 2);
        Assert.Equal("Document[" + TableOutline(2, 1, 60) + "]", tree.Root.Outline());
        Assert.DoesNotContain(tree.ContentText, t => t.Contains("/Type /Pagination>>", StringComparison.Ordinal));
        Assert.Equal(DocumentTestKit.Strip(DocumentTestKit.LongText(70)), ElementTexts(pdf, tree).First(e => e.Type == "TH").Text);
        DocumentTestKit.AssertValid(pdf, "tagged-table-tall-header");
    }

    /// <summary>
    /// A row taller than a page is split between pages: it is one <c>TR</c>, and a cell that goes on has marked content on each page under the same
    /// <c>TD</c>, a marked-content reference on the next. The long cell is the first: its content on the next page comes after the second cell's on
    /// this one, so the row keeps its cells open together.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void A_row_split_between_pages_is_one_row_whose_cells_go_on_with_marked_content_references(int longColumn)
    {
        string text = DocumentTestKit.LongText(1200);
        TableCell[] cells = [new TableCell("Short"), new TableCell("Short")];
        cells[longColumn] = new TableCell(text);
        PdfDocument document = new(Tagged());
        document.Add(new Paragraph("Before."));
        document.Add(new Table(Columns(2), new TableRow("Text", "Note"), [new TableRow(cells), new TableRow("Next", "Row")]) { Borders = Borders.All });

        byte[] pdf = DocumentTestKit.Save(document);

        StructureTreeReader tree = Read(pdf);
        Assert.True(tree.ContentStreams.Count >= 2);
        Assert.Equal("Document[P," + TableOutline(2, 1, 2) + "]", tree.Root.Outline());
        List<StructureTreeReader.Element> body = [.. Cells(tree).Where(c => c.Type == "TD")];
        StructureTreeReader.Element split = body[longColumn];
        StructureTreeReader.Element other = body[1 - longColumn];
        List<StructureTreeReader.ContentKid> parts = [.. split.Kids.OfType<StructureTreeReader.ContentKid>()];
        Assert.Equal(0, split.Page);
        Assert.Equal(Enumerable.Range(0, parts.Count), parts.Select(p => p.Page));
        Assert.True(parts.Count >= 2);
        Assert.All(parts.Skip(1), p => Assert.True(p.Reference));
        Assert.Equal<StructureTreeReader.Kid>([new StructureTreeReader.ContentKid(0, other.Kids.OfType<StructureTreeReader.ContentKid>().Single().Mcid, false)], other.Kids);
        List<(string Type, string Text)> texts = [.. ElementTexts(pdf, tree).Where(e => e.Type == "TD")];
        Assert.Equal(DocumentTestKit.Strip(text), texts[longColumn].Text);
        Assert.Equal(["Next", "Row"], texts.Skip(2).Select(t => t.Text));
        DocumentTestKit.AssertValid(pdf, $"tagged-table-split-row-{longColumn}");
    }

    /// <summary>
    /// A table with a header and no rows has its header rows straight under the <c>Table</c>, with no <c>THead</c>: PDF/UA-1 (7.2) wants a <c>THead</c>
    /// followed by a <c>TBody</c>, and ISO 32000-1 (14.8.4.3.4) lets a table hold its rows as <c>TR</c> without groups. The cells are still <c>TH</c> of
    /// their columns. A table with neither draws nothing and is not in the structure.
    /// </summary>
    [Fact]
    public void A_table_with_only_a_header_has_its_header_rows_under_the_table_and_an_empty_table_is_not_in_the_structure()
    {
        PdfDocument document = new(Tagged());
        document.Add(new Paragraph("Before."));
        document.Add(new Table(Columns(2), new TableRow("A", "B"), []) { Borders = Borders.All });
        document.Add(new Table(Columns(2), []) { Borders = Borders.All });
        document.Add(new Paragraph("After."));

        byte[] pdf = DocumentTestKit.Save(document);

        StructureTreeReader tree = Read(pdf);
        Assert.Equal("Document[P,Table[TR[TH,TH]],P]", tree.Root.Outline());
        Assert.Equal("Document[P," + TableOutline(2, 1, 0) + ",P]", tree.Root.Outline());
        Assert.Equal(["A", "B"], ElementTexts(pdf, tree).Where(e => e.Type == "TH").Select(e => e.Text));
        foreach (StructureTreeReader.Element header in tree.Root.Descendants().Where(e => e.Type == "TH"))
        {
            Assert.Equal("Table", Name(header, "O"));
            Assert.Equal("Column", Name(header, "Scope"));
        }

        DocumentTestKit.AssertValid(pdf, "tagged-table-header-only");
    }

    /// <summary>
    /// A header with no body row, however the rows come: given lazily and turning out to be none (known only by pulling the first), or a header of two
    /// rows. Each has its header rows straight under the <c>Table</c>.
    /// </summary>
    [Fact]
    public void A_header_with_lazy_rows_that_are_none_or_of_two_rows_has_no_thead()
    {
        static IEnumerable<TableRow> None()
        {
            yield break;
        }

        PdfDocument document = new(Tagged());
        document.Add(new Table(Columns(2), new TableRow("A", "B"), None()));
        document.Add(new Table(Columns(2), [new TableRow("C", "D"), new TableRow("E", "F")], []));

        byte[] pdf = DocumentTestKit.Save(document);

        StructureTreeReader tree = Read(pdf);
        Assert.Equal("Document[" + TableOutline(2, 1, 0) + "," + TableOutline(2, 2, 0) + "]", tree.Root.Outline());
        Assert.Equal(["A", "B", "C", "D", "E", "F"], ElementTexts(pdf, tree).Where(e => e.Type == "TH").Select(e => e.Text));
        DocumentTestKit.AssertValid(pdf, "tagged-table-header-lazy-none");
    }

    /// <summary>A table of a header and one row, given lazily, is unchanged: a <c>THead</c> and a <c>TBody</c>.</summary>
    [Fact]
    public void A_header_with_one_lazy_row_keeps_its_thead_and_tbody()
    {
        static IEnumerable<TableRow> One()
        {
            yield return new TableRow("1", "2");
        }

        PdfDocument document = new(Tagged());
        document.Add(new Table(Columns(2), new TableRow("A", "B"), One()));

        byte[] pdf = DocumentTestKit.Save(document);

        Assert.Equal("Document[Table[THead[TR[TH,TH]],TBody[TR[TD,TD]]]]", Read(pdf).Root.Outline());
    }

    /// <summary>A table without a header that runs over several pages: every cell is a <c>TD</c> of the body, and no page has a repeated header.</summary>
    [Fact]
    public void A_table_without_a_header_over_several_pages_is_a_body_of_cells()
    {
        PdfDocument document = new(Tagged());
        document.Add(new Table(Columns(2), Enumerable.Range(0, 150).Select(i => new TableRow($"Row {i}", $"{i}"))) { Borders = Borders.Horizontal, ZebraFill = Color.Gray(0.95) });

        byte[] pdf = DocumentTestKit.Save(document);

        StructureTreeReader tree = Read(pdf);
        Assert.True(tree.ContentStreams.Count >= 3, $"{tree.ContentStreams.Count} pages");
        Assert.Equal("Document[" + TableOutline(2, 0, 150) + "]", tree.Root.Outline());
        Assert.DoesNotContain(tree.Root.Descendants(), e => e.Type == "TH");
        Assert.All(tree.ContentText, t => Assert.DoesNotContain("/Type /Pagination>>", t, StringComparison.Ordinal));
        Assert.All(tree.ContentMcids, page => Assert.All(page, m => Assert.Equal("TD", m.Tag)));
        Assert.Equal(Enumerable.Range(0, 150).Select(i => $"Row{i}"), ElementTexts(pdf, tree).Where(e => e.Type == "TD").Where((_, i) => i % 2 == 0).Select(e => e.Text));
        DocumentTestKit.AssertValid(pdf, "tagged-table-no-header");
    }

    /// <summary>
    /// A header of two rows, one of whose cells has two paragraphs, the second blank: the blank one has no element, so the cell is a <c>TH</c> with one
    /// <c>P</c>. On every later page the two rows are drawn again as two pagination artifacts, with no marked content of the structure in them.
    /// </summary>
    [Fact]
    public void A_header_of_two_rows_is_repeated_on_later_pages_as_artifacts()
    {
        PdfDocument document = new(Tagged());
        document.Add(new Table(
            Columns(2),
            [new TableRow(new TableCell([new Paragraph("Group"), new Paragraph(" ")]), new TableCell("Total")), new TableRow("Name", "Value")],
            Enumerable.Range(0, 120).Select(i => new TableRow($"Row {i}", $"{i}")))
        {
            HeaderFill = Color.Gray(0.9),
            Borders = Borders.All,
        });

        byte[] pdf = DocumentTestKit.Save(document);

        StructureTreeReader tree = Read(pdf);
        Assert.True(tree.ContentStreams.Count >= 3, $"{tree.ContentStreams.Count} pages");
        Assert.Equal("Document[Table[THead[TR[TH[P],TH],TR[TH,TH]],TBody[" + string.Join(",", Enumerable.Repeat("TR[TD,TD]", 120)) + "]]]", tree.Root.Outline());
        Assert.Equal(
            [("TH", ""), ("P", "Group"), ("TH", "Total"), ("TH", "Name"), ("TH", "Value")],
            ElementTexts(pdf, tree).Where(e => e.Type is "TH" or "P"));
        Assert.DoesNotContain("/Type /Pagination>>", tree.ContentText[0], StringComparison.Ordinal);
        string[] artifacts = TaggedFlowTests.ArtifactTexts(pdf);
        for (int page = 1; page < tree.ContentStreams.Count; page++)
        {
            AssertRepeatedHeaderHasNoStructure(tree.ContentText[page], 2);
            Assert.Equal("GroupTotalNameValue", artifacts[page]);
        }

        DocumentTestKit.AssertValid(pdf, "tagged-table-two-row-header");
    }

    /// <summary>
    /// A right-to-left row split over several pages, with a cell of two paragraphs whose second is long in the first column and a long cell in the
    /// second: one <c>TR</c>, the cells in logical order, the long paragraph and the long cell each with marked content on every page they reach.
    /// </summary>
    [Fact]
    public void A_right_to_left_row_split_over_several_pages_keeps_its_cells_and_paragraphs()
    {
        string longParagraph = DocumentTestKit.LongText(900, "para");
        string longCell = DocumentTestKit.LongText(1500, "cell");
        PdfDocument document = new(Tagged());
        document.Add(new Table(
            Columns(2),
            new TableRow("First", "Second"),
            [new TableRow(new TableCell([new Paragraph("Opening"), new Paragraph(longParagraph)]), new TableCell(longCell)), new TableRow("Next", "Row")])
        {
            Direction = TextDirection.RightToLeft,
            Borders = Borders.All,
        });

        byte[] pdf = DocumentTestKit.Save(document);

        StructureTreeReader tree = Read(pdf);
        Assert.True(tree.ContentStreams.Count >= 3, $"{tree.ContentStreams.Count} pages");
        Assert.Equal("Document[Table[THead[TR[TH,TH]],TBody[TR[TD[P,P],TD],TR[TD,TD]]]]", tree.Root.Outline());
        StructureTreeReader.Element row = tree.Root.Descendants().Where(e => e.Type == "TR").ElementAt(1);
        StructureTreeReader.Element first = row.Children.ElementAt(0);
        StructureTreeReader.Element second = row.Children.ElementAt(1);
        List<StructureTreeReader.ContentKid> paragraphParts = [.. first.Children.ElementAt(1).Kids.OfType<StructureTreeReader.ContentKid>()];
        List<StructureTreeReader.ContentKid> cellParts = [.. second.Kids.OfType<StructureTreeReader.ContentKid>()];
        Assert.True(paragraphParts.Count >= 2 && cellParts.Count >= 3, $"{paragraphParts.Count} and {cellParts.Count} parts");
        Assert.Equal(Enumerable.Range(0, cellParts.Count), cellParts.Select(p => p.Page));
        Assert.Equal(Enumerable.Range(paragraphParts[0].Page, paragraphParts.Count), paragraphParts.Select(p => p.Page));
        Assert.All(cellParts.Skip(1), p => Assert.True(p.Reference));
        Assert.Equal(
            [DocumentTestKit.Strip("Opening"), DocumentTestKit.Strip(longParagraph), DocumentTestKit.Strip(longCell), "Next", "Row"],
            ElementTexts(pdf, tree).Where(e => e.Type == "P" || (e.Type == "TD" && e.Text.Length > 0)).Select(e => e.Text));

        // The first column stands on the right.
        Dictionary<int, double> lefts = Lefts(pdf, 0);
        Assert.True(lefts[0] > lefts[1], "the first header is on the right");
        DocumentTestKit.AssertValid(pdf, "tagged-table-split-row-rtl");
    }

    /// <summary>
    /// The fills (header, zebra, row and cell) are an artifact drawn before a row's text and the borders one drawn after it, the closing rule of each page
    /// too: no path is painted inside a cell's marked content, and the artifacts have no text.
    /// </summary>
    [Fact]
    public void Fills_and_borders_are_artifacts_around_the_cells_marked_content()
    {
        PdfDocument document = new(Tagged());
        document.Add(new Table(
            Columns(2),
            new TableRow("Name", "Value"),
            Enumerable.Range(0, 90).Select(i => i switch
            {
                2 => new TableRow($"Row {i}", $"{i}") { Fill = Color.FromRgb(255, 240, 200) },
                3 => new TableRow(new TableCell($"Row {i}") { Fill = Color.FromRgb(200, 240, 255) }, new TableCell($"{i}")),
                _ => new TableRow($"Row {i}", $"{i}"),
            }))
        {
            HeaderFill = Color.Gray(0.9),
            ZebraFill = Color.Gray(0.97),
            Borders = Borders.All,
        });

        byte[] pdf = DocumentTestKit.Save(document);

        StructureTreeReader tree = Read(pdf);
        Assert.True(tree.ContentStreams.Count >= 2);
        foreach (string content in tree.ContentText)
        {
            foreach ((string begin, string body) in Sequences(content))
            {
                if (begin.Contains("/MCID", StringComparison.Ordinal))
                {
                    Assert.DoesNotMatch(PathPaintingPattern(), body);
                }
            }
        }

        // Header: fills, two headers, borders; row 0: no fill; row 1: the zebra; row 2: the row's fill; row 3: the cell's fill.
        List<string> marks = Marks(tree.ContentText[0]);
        Assert.Equal(["A", "TH", "TH", "A", "TD", "TD", "A", "A", "TD", "TD", "A", "A", "TD", "TD", "A", "A", "TD", "TD", "A"], marks.Take(19));
        Assert.Equal("A", marks[^1]); // the closing rule of the page
        Assert.Equal("", TaggedFlowTests.ArtifactTexts(pdf)[0]);
        DocumentTestKit.AssertValid(pdf, "tagged-table-fills-borders");
    }

    [Fact]
    public void A_table_cut_at_its_row_limit_keeps_a_consistent_structure()
    {
        PdfDocument document = new(Tagged());
        document.Add(new Table(Columns(2), new TableRow("Name", "Value"), Enumerable.Range(0, 20).Select(i => new TableRow($"Row {i}", $"{i}"))) { MaxRows = 5 });
        document.Add(new Paragraph("After."));

        byte[] pdf = DocumentTestKit.Save(document);

        Assert.Single(document.Diagnostics.TruncatedTables);
        StructureTreeReader tree = Read(pdf);
        Assert.Equal("Document[" + TableOutline(2, 1, 5) + ",P]", tree.Root.Outline());
        Assert.Equal("Row4", ElementTexts(pdf, tree).Last(e => e.Type == "TD" && e.Text.StartsWith("Row", StringComparison.Ordinal)).Text);
        DocumentTestKit.AssertValid(pdf, "tagged-table-max-rows");
    }

    /// <summary>A table cannot be in a callout, a column or a header (it can run over pages): tagging changes nothing of that, so a table is always in the flow.</summary>
    [Fact]
    public void A_table_is_refused_in_a_callout_a_column_and_a_header_tagged_as_untagged()
    {
        Table table = new(Columns(1), [new TableRow("Cell")]);
        Assert.Throws<ArgumentException>(() => new Callout([table]));
        Assert.Throws<ArgumentException>(() => new Columns([[table]]));

        PdfDocument document = new(Tagged(o => o.Header = _ => table));
        document.Add(new Paragraph("Body."));
        Assert.Throws<InvalidOperationException>(() => DocumentTestKit.Save(document));
    }

    /// <summary>A table of many rows (the 1,000 of the measuring scenario), with a header and a footer: every row is a row of the structure, once.</summary>
    private static PdfDocument LongTable(int rows) =>
        new PdfDocument(Tagged(o =>
        {
            o.Header = page => new Paragraph("Report");
            o.Footer = page => new Paragraph($"Page {page.Number} of {page.Count}");
        }))
            .Add(new Heading(1, "Report"))
            .Add(new Table(Columns(3), new TableRow("Item", "Quantity", "Note"), Enumerable.Range(0, rows).Select(i => new TableRow($"Item {i}", $"{i * 7 % 100}", $"Note {i}")))
            {
                Borders = Borders.Horizontal,
                HeaderFill = Color.Gray(0.9),
                ZebraFill = Color.Gray(0.97),
                CellPadding = Margins.Pt(1, 3, 1, 3),
            });

    [Fact]
    public void A_table_of_1000_rows_is_one_table_of_1000_rows_with_their_texts_and_the_same_file_twice()
    {
        byte[] pdf = DocumentTestKit.Save(LongTable(1000));

        StructureTreeReader tree = Read(pdf);
        Assert.True(tree.ContentStreams.Count >= 15);
        Assert.Equal("Document[H1," + TableOutline(3, 1, 1000) + "]", tree.Root.Outline());
        List<(string Type, string Text)> cells = [.. ElementTexts(pdf, tree).Where(e => e.Type == "TD")];
        Assert.Equal(Enumerable.Range(0, 1000).SelectMany(i => new[] { $"Item{i}", $"{i * 7 % 100}", $"Note{i}" }), cells.Select(c => c.Text));
        Assert.Equal(pdf, DocumentTestKit.Save(LongTable(1000)));
        DocumentTestKit.AssertValid(pdf, "tagged-table-1000");
    }

    /// <summary>
    /// A table of 10,000 rows is streamed tagged as untagged: each row's elements are written when the row is placed, so nearly all of them are in the
    /// file before the last page's content (the writer holds the open branch, not the tree), and the allocation grows with the rows, not faster.
    /// </summary>
    [Fact]
    public void A_table_of_10000_rows_writes_its_rows_as_they_are_placed_and_allocates_in_proportion()
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        byte[] small = DocumentTestKit.Save(LongTable(1000));
        long smallBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        before = GC.GetAllocatedBytesForCurrentThread();
        byte[] pdf = DocumentTestKit.Save(LongTable(10_000));
        long largeBytes = GC.GetAllocatedBytesForCurrentThread() - before;

        StructureTreeReader tree = Read(pdf);
        int pages = tree.ContentStreams.Count;
        Assert.True(pages >= 150, $"{pages} pages");
        Assert.Equal(2 + 3 + 4 + (10_000 * 4), tree.StructElemObjects); // Document and H1; Table, THead and TBody; the header row; the rows

        string file = Encoding.Latin1.GetString(pdf);
        int lastPage = file.LastIndexOf("/TD <</MCID 0>> BDC", StringComparison.Ordinal);
        int writtenBefore = Regex.Count(file[..lastPage], @"/Type /StructElem\b");
        int onLastPage = tree.ContentMcids[^1].Count;
        int total = tree.StructElemObjects;
        Assert.True(writtenBefore >= total - onLastPage - 8, $"{writtenBefore} of {total} elements are written before the last page's content ({onLastPage} MCIDs on it)");

        // The output and the work grow ten times for ten times the rows (the fonts and the first pages are the same): what the structure holds does not grow with them.
        Assert.True(largeBytes < smallBytes * 11, $"10,000 rows allocated {largeBytes} bytes, 1,000 rows {smallBytes}");
        Assert.True(pdf.Length < small.Length * 11);
        DocumentTestKit.AssertValid(pdf, "tagged-table-10000");
    }

    /// <summary>
    /// Each marked-content sequence of a content stream, in the order they end: the line that begins it and everything up to its own <c>EMC</c>, the
    /// sequences inside it included (a language span in a cell, the text of a repeated header row), matched by depth rather than to the first <c>EMC</c>.
    /// </summary>
    private static List<(string Begin, string Body)> Sequences(string content)
    {
        List<(string, string)> found = [];
        Stack<(string Begin, int Start)> open = new();
        foreach (Match operation in MarkedContentLinePattern().Matches(content))
        {
            if (operation.Value == "EMC\n")
            {
                (string begin, int start) = open.Pop();
                found.Add((begin, content[start..operation.Index]));
            }
            else
            {
                open.Push((operation.Value, operation.Index + operation.Length));
            }
        }

        Assert.Empty(open);
        return found;
    }

    /// <summary>The marked content of a content stream in order: <c>A</c> for an artifact, <c>R</c> for a pagination artifact (a repeated header row), or the tag of a sequence with an <c>MCID</c>.</summary>
    private static List<string> Marks(string content) =>
        [.. MarkPattern().Matches(content).Select(m => m.Groups["tag"].Success ? m.Groups["tag"].Value : m.Groups["pagination"].Success ? "R" : "A")];

    // A line that begins or ends marked content: a BDC or a BMC with its operands, or an EMC. Glyphs are hexadecimal strings, so no text drawn matches.
    [GeneratedRegex(@"(?m)^(/[^\n]*\b(BDC|BMC)|EMC)\n")]
    private static partial Regex MarkedContentLinePattern();

    // A path painted: a rectangle, a line, a fill or a stroke.
    [GeneratedRegex(@"(^|\s)(re|l|f|S)\n")]
    private static partial Regex PathPaintingPattern();

    // Marked content in order: an artifact, a pagination artifact (with its property list), or a sequence with an MCID and its tag. Language spans are not counted.
    [GeneratedRegex(@"/Artifact BMC\n|(?<pagination>/Artifact <<[^\n]*>> BDC\n)|/(?<tag>TD|TH|P) <</MCID \d+>> BDC\n")]
    private static partial Regex MarkPattern();
}
