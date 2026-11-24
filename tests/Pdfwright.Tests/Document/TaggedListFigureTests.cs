using System.Text;
using System.Text.RegularExpressions;
using Pdfwright.Tests.Images;
using Pdfwright.Tests.Pdf;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Tokens;
using DrawingBlock = Pdfwright.Drawing;

namespace Pdfwright.Tests.Document;

/// <summary>
/// The document layer of tagged PDF for lists and figures, written with <see cref="PdfDocumentOptions.Tagged"/> (the default): a list is an <c>L</c> with
/// an <c>LI</c> for each item, the marker its <c>Lbl</c> and the item's paragraphs and nested list its <c>LBody</c>; an image or a drawing with
/// alternative text is a <c>Figure</c> with <c>/Alt</c> and a bounding box, and one without is an artifact. The documents are made through the
/// public API and read back by <see cref="StructureTreeReader"/>.
/// </summary>
public class TaggedListFigureTests
{
    private static readonly PdfImage Png = PdfImage.FromBytes(ImageTestKit.Png(16, 16, 8, 6));

    private static PdfDocumentOptions Tagged(Action<DocumentTestKit.OptionsBuilder>? change = null) => TaggedFlowTests.Tagged(change);

    private static StructureTreeReader Read(byte[] pdf) => TaggedFlowTests.Read(pdf);

    private static List<(string Type, string Text)> ElementTexts(byte[] pdf, StructureTreeReader tree) => TaggedFlowTests.ElementTexts(pdf, tree);

    private static string Name(StructureTreeReader.Element element, string key) => ((NameToken)element.Attributes!.Data[key]).Data;

    /// <summary>The <c>/BBox</c> of a figure's layout attributes: left, bottom, right, top.</summary>
    private static double[] BBox(StructureTreeReader.Element figure)
    {
        Assert.Equal("Layout", Name(figure, "O"));
        return [.. ((ArrayToken)figure.Attributes!.Data["BBox"]).Data.Select(t => ((NumericToken)t).Double)];
    }

    private static void AssertBox(double[] expected, double[] actual)
    {
        Assert.Equal(4, actual.Length);
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(expected[i], actual[i], 0.01);
        }
    }

    [Theory]
    [InlineData(TextDirection.LeftToRight)]
    [InlineData(TextDirection.RightToLeft)]
    public void A_list_is_an_l_with_an_item_label_and_body_for_each_item(TextDirection direction)
    {
        PdfDocument document = new(Tagged());
        document.Add(new Paragraph("Before."));
        document.Add(new ItemList(["First item", "Second item", "Third item"]) { Direction = direction });
        document.Add(new Paragraph("After."));

        byte[] pdf = DocumentTestKit.Save(document);

        StructureTreeReader tree = Read(pdf);
        Assert.Equal("Document[P,L[LI[Lbl,LBody[P]],LI[Lbl,LBody[P]],LI[Lbl,LBody[P]]],P]", tree.Root.Outline());
        StructureTreeReader.Element list = tree.Root.Children.ElementAt(1);
        Assert.Equal("List", Name(list, "O"));
        Assert.Equal("Disc", Name(list, "ListNumbering"));
        Assert.Equal(
            [
                ("P", "Before."),
                ("L", ""),
                ("LI", ""), ("Lbl", "•"), ("LBody", ""), ("P", "Firstitem"),
                ("LI", ""), ("Lbl", "•"), ("LBody", ""), ("P", "Seconditem"),
                ("LI", ""), ("Lbl", "•"), ("LBody", ""), ("P", "Thirditem"),
                ("P", "After."),
            ],
            ElementTexts(pdf, tree));

        // The marker's marked content ends before its paragraph's begins: they are drawn one after the other, never one inside the other.
        Assert.Equal(["P", "Lbl", "P", "Lbl", "P", "Lbl", "P", "P"], tree.ContentMcids[0].Select(m => m.Tag));
        DocumentTestKit.AssertValid(pdf, $"tagged-list-flat-{direction}");
    }

    [Theory]
    [InlineData(ListMarker.Bullet, 1, "Disc", "•", "•")]
    [InlineData(ListMarker.Decimal, 1, "Decimal", "1.", "2.")]
    [InlineData(ListMarker.Decimal, 9, "Decimal", "9.", "10.")]
    [InlineData(ListMarker.LowerAlpha, 1, "LowerAlpha", "a.", "b.")]
    [InlineData(ListMarker.UpperAlpha, 26, "UpperAlpha", "Z.", "AA.")]
    [InlineData(ListMarker.ArabicIndicDecimal, 1, "Decimal", null, null)]
    public void The_list_numbering_and_the_labels_follow_the_marker(ListMarker marker, int start, string numbering, string? first, string? second)
    {
        PdfDocument document = new(Tagged());
        document.Add(new ItemList(["One", "Two"]) { Marker = marker, Start = start });

        byte[] pdf = DocumentTestKit.Save(document);

        StructureTreeReader tree = Read(pdf);
        Assert.Equal("Document[L[LI[Lbl,LBody[P]],LI[Lbl,LBody[P]]]]", tree.Root.Outline());
        Assert.Equal(numbering, Name(tree.Root.Children.Single(), "ListNumbering"));
        List<string> labels = [.. ElementTexts(pdf, tree).Where(e => e.Type == "Lbl").Select(e => e.Text)];
        Assert.Equal(2, labels.Count);
        Assert.All(labels, l => Assert.NotEmpty(l));
        if (first is not null)
        {
            Assert.Equal([first, second!], labels);
        }

        Assert.Equal(["One", "Two"], ElementTexts(pdf, tree).Where(e => e.Type == "P").Select(e => e.Text));
    }

    [Fact]
    public void A_nested_list_is_an_l_in_the_body_of_its_item_between_the_items_paragraphs()
    {
        PdfDocument document = new(Tagged());
        document.Add(new ItemList(
        [
            new ListItem([new Paragraph("One"), new ItemList(["One a", "One b"]) { Marker = ListMarker.LowerAlpha }, new Paragraph("One more")]),
            new ListItem("Two"),
        ])
        { Marker = ListMarker.Decimal });
        document.Add(new Paragraph("After."));

        byte[] pdf = DocumentTestKit.Save(document);

        StructureTreeReader tree = Read(pdf);
        Assert.Equal("Document[L[LI[Lbl,LBody[P,L[LI[Lbl,LBody[P]],LI[Lbl,LBody[P]]],P]],LI[Lbl,LBody[P]]],P]", tree.Root.Outline());
        StructureTreeReader.Element outer = tree.Root.Children.First();
        StructureTreeReader.Element inner = outer.Children.First().Children.ElementAt(1).Children.ElementAt(1);
        Assert.Equal("Decimal", Name(outer, "ListNumbering"));
        Assert.Equal("LowerAlpha", Name(inner, "ListNumbering"));
        Assert.Equal(
            [("Lbl", "1."), ("P", "One"), ("Lbl", "a."), ("P", "Onea"), ("Lbl", "b."), ("P", "Oneb"), ("P", "Onemore"), ("Lbl", "2."), ("P", "Two"), ("P", "After.")],
            ElementTexts(pdf, tree).Where(e => e.Type is "Lbl" or "P"));
        DocumentTestKit.AssertValid(pdf, "tagged-list-nested");
    }

    [Fact]
    public void A_list_in_a_callout_or_a_column_is_in_its_division_and_a_list_in_the_header_is_an_artifact()
    {
        PdfDocument document = new(Tagged(o => o.Header = page => new ItemList(["Header item"])));
        document.Add(new Callout([new Paragraph("Note."), new ItemList(["Callout item"])]) { Border = new BorderLine(Length.Pt(1), Color.Black) });
        document.Add(new Columns([[new ItemList(["Left item", "Left two"])], [new Paragraph("Right")]]));

        byte[] pdf = DocumentTestKit.Save(document);

        StructureTreeReader tree = Read(pdf);
        Assert.Equal("Document[Div[P,L[LI[Lbl,LBody[P]]]],Div[Div[L[LI[Lbl,LBody[P]],LI[Lbl,LBody[P]]]],Div[P]]]", tree.Root.Outline());
        Assert.Equal(["Note.", "Calloutitem", "Leftitem", "Lefttwo", "Right"], ElementTexts(pdf, tree).Where(e => e.Type == "P").Select(e => e.Text));
        Assert.Contains("Headeritem", TaggedFlowTests.ArtifactTexts(pdf)[0], StringComparison.Ordinal);
        Assert.DoesNotContain("/MCID", tree.ContentStreams[0][0], StringComparison.Ordinal);
        DocumentTestKit.AssertValid(pdf, "tagged-list-divisions");
    }

    [Fact]
    public void A_list_item_split_between_pages_is_one_paragraph_with_a_marked_content_reference_and_one_label()
    {
        PdfDocument document = new(Tagged(o => o.PageSize = new PageSize(Length.Mm(100), Length.Mm(80))));
        document.Add(new Paragraph("Before."));
        document.Add(new ItemList(["Short", DocumentTestKit.LongText(150), .. Enumerable.Range(0, 30).Select(i => $"Item {i}")]) { Marker = ListMarker.Decimal });

        byte[] pdf = DocumentTestKit.Save(document);

        StructureTreeReader tree = Read(pdf);
        StructureTreeReader.Element list = tree.Root.Children.ElementAt(1);
        Assert.Equal("L", list.Type);
        Assert.Equal(32, list.Children.Count());
        Assert.All(list.Children, item => Assert.Equal("LI[Lbl,LBody[P]]", item.Outline()));

        StructureTreeReader.Element split = list.Children.ElementAt(1).Children.ElementAt(1).Children.Single();
        List<StructureTreeReader.ContentKid> kids = [.. split.Kids.OfType<StructureTreeReader.ContentKid>()];
        Assert.True(kids.Count >= 2, "the item's paragraph has content on two pages or more");
        Assert.Equal(Enumerable.Range(kids[0].Page, kids.Count), kids.Select(k => k.Page));
        Assert.False(kids[0].Reference);
        Assert.All(kids.Skip(1), k => Assert.True(k.Reference));

        // The label is drawn once, with the first line.
        StructureTreeReader.Element label = list.Children.ElementAt(1).Children.First();
        Assert.Equal(kids[0].Page, Assert.Single(label.Kids.OfType<StructureTreeReader.ContentKid>()).Page);

        List<(string Type, string Text)> texts = ElementTexts(pdf, tree);
        Assert.Equal(DocumentTestKit.Strip(DocumentTestKit.LongText(150)), texts.Where(e => e.Type == "P").ElementAt(2).Text);
        Assert.Equal(Enumerable.Range(1, 32).Select(n => $"{n}."), texts.Where(e => e.Type == "Lbl").Select(e => e.Text));
        Assert.True(tree.ContentStreams.Count >= 3);
        DocumentTestKit.AssertValid(pdf, "tagged-list-split");
    }

    [Fact]
    public void An_image_with_alternative_text_is_a_figure_with_its_alt_and_the_box_it_is_drawn_in()
    {
        PdfDocument document = new(Tagged());
        document.Add(new Paragraph("Before."));
        document.Add(new Image(Png) { Width = Length.Mm(30), Alignment = TextAlignment.Center, AltText = "A small\nchart\u0001 " });
        document.Add(new Paragraph("After."));

        byte[] pdf = DocumentTestKit.Save(document);

        StructureTreeReader tree = Read(pdf);
        Assert.Equal("Document[P,Figure,P]", tree.Root.Outline());
        StructureTreeReader.Element figure = tree.Root.Children.ElementAt(1);
        Assert.Equal("A small chart", figure.Alt);
        Assert.Equal(0, figure.Page);
        Assert.Equal(new StructureTreeReader.ContentKid(0, 1, false), Assert.Single(figure.Kids));

        using UglyToad.PdfPig.PdfDocument read = UglyToad.PdfPig.PdfDocument.Open(pdf);
        IPdfImage image = Assert.Single(read.GetPage(1).GetImages());
        AssertBox([image.BoundingBox.Left, image.BoundingBox.Bottom, image.BoundingBox.Right, image.BoundingBox.Top], BBox(figure));
        double[] box = BBox(figure);
        Assert.Equal(Length.Mm(30).Points, box[2] - box[0], 0.01);
        Assert.Equal((PageSize.A4.Width.Points - Length.Mm(30).Points) / 2, box[0], 0.01); // centred: the margins are the same on both sides

        // The picture is drawn inside the figure's marked content.
        string content = tree.ContentText[0];
        int begin = content.IndexOf("/Figure <</MCID 1>> BDC\n", StringComparison.Ordinal);
        int end = content.IndexOf("EMC\n", begin, StringComparison.Ordinal);
        Assert.True(begin >= 0);
        Assert.Contains(" Do\n", content[begin..end], StringComparison.Ordinal);
        Assert.DoesNotContain("/Artifact", content, StringComparison.Ordinal);
        DocumentTestKit.AssertValid(pdf, "tagged-figure-image");
    }

    [Theory]
    [InlineData(TextDirection.LeftToRight)]
    [InlineData(TextDirection.RightToLeft)]
    public void A_drawing_with_alternative_text_is_one_figure_whose_marked_content_holds_its_canvas_text_and_pictures(TextDirection direction)
    {
        DrawingBlock drawing = new(Length.Mm(60), Length.Mm(30), canvas =>
        {
            canvas.Fill(new DrawingPathBuilder().Rectangle(0, 0, canvas.Width, canvas.Height).Build(), Color.Gray(0.85));
            canvas.Stroke(new DrawingPathBuilder().MoveTo(0, 0).LineTo(canvas.Width, canvas.Height).Build(), Color.Black);
            canvas.DrawText("Canvas text", 4, 12);
            canvas.DrawText("More", 4, 30, new TextStyle { Language = "fr" });
            canvas.DrawImage(Png, 100, 20, 20, 20, opacity: 0.5);
        })
        {
            AltText = "A diagram",
        };
        PdfDocument document = new(Tagged(o =>
        {
            o.Direction = direction;
            o.PageSize = new PageSize(Length.Mm(150), Length.Mm(120));
            o.Margins = Margins.Mm(top: 12, right: 18, bottom: 10, left: 14);
        }));
        document.Add(drawing);
        document.Add(new Paragraph("After."));

        byte[] pdf = DocumentTestKit.Save(document);

        StructureTreeReader tree = Read(pdf);
        Assert.Equal("Document[Figure,P]", tree.Root.Outline());
        StructureTreeReader.Element figure = tree.Root.Children.First();
        Assert.Equal("A diagram", figure.Alt);
        Assert.Single(figure.Kids);
        Assert.Equal([("Figure", "CanvastextMore"), ("P", "After.")], ElementTexts(pdf, tree));

        // The drawing is the first block: its top is the top of the content area, and it stands at the start edge.
        double width = Length.Mm(60).Points;
        double left = direction == TextDirection.RightToLeft ? Length.Mm(150 - 18).Points - width : Length.Mm(14).Points;
        double top = Length.Mm(120 - 12).Points;
        AssertBox([left, top - Length.Mm(30).Points, left + width, top], BBox(figure));

        // Everything the drawing paints is inside the figure's one sequence, which holds nothing tagged of its own.
        string content = tree.ContentText[0];
        int begin = content.IndexOf("/Figure <</MCID 0>> BDC\n", StringComparison.Ordinal);
        Assert.Equal(0, begin);
        int afterFigure = content.IndexOf("/P <</MCID 1>> BDC\n", StringComparison.Ordinal);
        string inside = content[begin..afterFigure];
        Assert.Equal(1, Regex.Count(inside, "/MCID"));
        Assert.DoesNotContain("/Artifact", inside, StringComparison.Ordinal);
        foreach (string op in new[] { "f", "S", "Do", "T[jJ]" })
        {
            Assert.Matches($@"(?<=\s){op}\n", inside);
        }

        DocumentTestKit.AssertValid(pdf, $"tagged-figure-drawing-{direction}");
    }

    [Fact]
    public void An_image_or_a_drawing_without_alternative_text_is_an_artifact()
    {
        DrawingBlock drawing = new(Length.Mm(40), Length.Mm(20), canvas => canvas.DrawText("Decorative text", 4, 12));
        PdfDocument document = new(Tagged());
        document.Add(new Paragraph("Before."));
        document.Add(new Image(Png) { Width = Length.Mm(10) });
        document.Add(new Image(Png) { Width = Length.Mm(10), AltText = string.Empty });
        document.Add(new Image(Png) { Width = Length.Mm(10), AltText = " \t\u0002" });
        document.Add(drawing);
        document.Add(drawing with { AltText = "  " });
        document.Add(new Paragraph("After."));

        byte[] pdf = DocumentTestKit.Save(document);

        StructureTreeReader tree = Read(pdf);
        Assert.Equal("Document[P,P]", tree.Root.Outline());
        Assert.Equal(5, Regex.Count(tree.ContentText[0], @"/Artifact BMC\n"));
        Assert.Contains("DecorativetextDecorativetext", TaggedFlowTests.ArtifactTexts(pdf)[0], StringComparison.Ordinal);
        DocumentTestKit.AssertValid(pdf, "tagged-figure-decorative");
    }

    [Fact]
    public void Figures_in_a_callout_and_in_columns_are_in_their_division_and_a_figure_in_the_header_is_an_artifact()
    {
        DrawingBlock drawing = new(Length.Mm(30), Length.Mm(15), canvas => canvas.DrawText("Inside", 2, 10)) { AltText = "Small drawing" };
        PdfDocument document = new(Tagged(o =>
        {
            o.Header = page => new Columns([new Image(Png) { Width = Length.Pt(12), AltText = "Header logo" }, new DrawingBlock(Length.Mm(30), Length.Mm(6), canvas => canvas.DrawText("Inside", 2, 10)) { AltText = "Header drawing" }]);
            o.Footer = page => new Paragraph($"Page {page.Number}");
        }));
        document.Add(new Callout([new Paragraph("Note."), new Image(Png) { Width = Length.Mm(20), AltText = "In the callout" }])
        {
            Border = new BorderLine(Length.Pt(2), Color.Black),
            Padding = Margins.Uniform(Length.Pt(7)),
        });
        document.Add(new Columns([[new Image(Png) { Width = Length.Mm(20), AltText = "In a column" }], [drawing], [new Paragraph("Caption")]]));

        byte[] pdf = DocumentTestKit.Save(document);

        StructureTreeReader tree = Read(pdf);
        Assert.Equal("Document[Div[P,Figure],Div[Div[Figure],Div[Figure],Div[P]]]", tree.Root.Outline());
        Assert.Equal(["In the callout", "In a column", "Small drawing"], tree.Root.Descendants().Where(e => e.Type == "Figure").Select(e => e.Alt));
        Assert.DoesNotContain(tree.Root.Descendants(), e => e.Alt is "Header logo" or "Header drawing");
        Assert.DoesNotContain("/MCID", tree.ContentStreams[0][0], StringComparison.Ordinal);
        Assert.Contains("Inside", TaggedFlowTests.ArtifactTexts(pdf)[0], StringComparison.Ordinal);
        Assert.Equal(["", "", "Inside"], ElementTexts(pdf, tree).Where(e => e.Type == "Figure").Select(e => e.Text));

        // Each image's figure has the box the image is drawn in (the header's logo is the first image of the page, and an artifact).
        using UglyToad.PdfPig.PdfDocument read = UglyToad.PdfPig.PdfDocument.Open(pdf);
        List<IPdfImage> images = [.. read.GetPage(1).GetImages()];
        Assert.Equal(3, images.Count);
        List<StructureTreeReader.Element> figures = [.. tree.Root.Descendants().Where(e => e.Type == "Figure")];
        foreach ((StructureTreeReader.Element figure, IPdfImage image) in figures.Take(2).Zip(images.Skip(1)))
        {
            AssertBox([image.BoundingBox.Left, image.BoundingBox.Bottom, image.BoundingBox.Right, image.BoundingBox.Top], BBox(figure));
        }

        // The callout's image is inside its border and padding.
        Assert.Equal(Length.Mm(15).Points + 9, BBox(figures[0])[0], 0.01);
        DocumentTestKit.AssertValid(pdf, "tagged-figure-divisions");
    }

    [Fact]
    public void A_figure_that_does_not_fit_moves_whole_to_the_next_page()
    {
        PdfDocument document = new(Tagged(o =>
        {
            o.PageSize = new PageSize(Length.Mm(100), Length.Mm(80));
            o.Margins = Margins.Mm(top: 10, right: 10, bottom: 10, left: 10);
        }));
        document.Add(new Paragraph(DocumentTestKit.LongText(60)));
        document.Add(new Image(Png) { Width = Length.Mm(40), AltText = "Square" });

        byte[] pdf = DocumentTestKit.Save(document);

        StructureTreeReader tree = Read(pdf);
        Assert.Equal("Document[P,Figure]", tree.Root.Outline());
        Assert.Equal(2, tree.ContentStreams.Count);
        StructureTreeReader.Element figure = tree.Root.Children.ElementAt(1);
        Assert.Equal(1, figure.Page);
        Assert.Equal(new StructureTreeReader.ContentKid(1, 0, false), Assert.Single(figure.Kids));
        double top = Length.Mm(70).Points;
        AssertBox([Length.Mm(10).Points, top - Length.Mm(40).Points, Length.Mm(50).Points, top], BBox(figure));
        DocumentTestKit.AssertValid(pdf, "tagged-figure-next-page");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_same_tagged_document_with_lists_and_figures_is_the_same_file_twice(bool compress)
    {
        byte[] Save()
        {
            PdfDocument document = new(Tagged(o => o.Compress = compress));
            document.Add(new ItemList([new ListItem([new Paragraph("One"), new ItemList(["One a"])]), new ListItem("Two")]) { Marker = ListMarker.Decimal });
            document.Add(new Image(Png) { Width = Length.Mm(20), AltText = "Picture" });
            document.Add(new DrawingBlock(Length.Mm(20), Length.Mm(10), canvas => canvas.DrawText("x", 1, 8)) { AltText = "Drawing" });
            return DocumentTestKit.Save(document);
        }

        Assert.Equal(Save(), Save());
    }

    [Fact]
    public void An_item_with_nothing_to_read_has_no_item_and_its_marker_is_an_artifact()
    {
        PdfDocument document = new(Tagged());
        document.Add(new ItemList(["", "Two", "  "]) { Marker = ListMarker.Decimal });
        document.Add(new ItemList([string.Empty, " "]));
        document.Add(new ItemList(
        [
            new ListItem([new Paragraph(string.Empty), new ItemList([string.Empty, " "])]),
            new ListItem([new Paragraph(" "), new ItemList(["Nested"])]),
        ]));
        document.Add(new Paragraph("After."));

        byte[] pdf = DocumentTestKit.Save(document);

        StructureTreeReader tree = Read(pdf);
        Assert.Equal("Document[L[LI[Lbl,LBody[P]]],L[LI[Lbl,LBody[L[LI[Lbl,LBody[P]]]]]],P]", tree.Root.Outline());
        Assert.Equal(
            [("Lbl", "2."), ("P", "Two"), ("Lbl", "•"), ("Lbl", "•"), ("P", "Nested"), ("P", "After.")],
            ElementTexts(pdf, tree).Where(e => e.Type is "Lbl" or "P"));

        // The markers of the items with nothing to read are drawn as artifacts: "1." and "3." of the first list, the two bullets of the second, the
        // bullet of the third's first item and the two of its nested list.
        Assert.Equal("1.3.•••••", TaggedFlowTests.ArtifactTexts(pdf)[0]);
        DocumentTestKit.AssertValid(pdf, "tagged-list-blank-items");
    }

    [Fact]
    public void A_list_item_label_and_paragraph_have_their_own_language()
    {
        PdfDocument document = new(Tagged());

        // The list's language is the label's and the paragraph's.
        document.Add(new ItemList(["Un"]) { Style = new TextStyle { Language = "fr" } });

        // The paragraph's language is the label's too (the marker is set in the style of the item's first paragraph), unless the marker style sets another.
        document.Add(new ItemList([new ListItem(new Paragraph("Eins") { Style = new TextStyle { Language = "de" } })]));
        document.Add(new ItemList([new ListItem(new Paragraph("Zwei") { Style = new TextStyle { Language = "de" } })]) { MarkerStyle = new TextStyle { Language = "en" } });

        // A marker style in another language than the text's.
        document.Add(new ItemList(["English"]) { MarkerStyle = new TextStyle { Language = "fr" } });

        byte[] pdf = DocumentTestKit.Save(document);

        StructureTreeReader tree = Read(pdf);
        Assert.Equal(
            new (string, string?)[] { ("Lbl", "fr"), ("P", "fr"), ("Lbl", "de"), ("P", "de"), ("Lbl", null), ("P", "de"), ("Lbl", "fr"), ("P", null) },
            tree.Root.Descendants().Where(e => e.Type is "Lbl" or "P").Select(e => (e.Type, e.Language)));
        Assert.All(tree.Root.Descendants().Where(e => e.Type is "L" or "LI" or "LBody"), e => Assert.Null(e.Language));
        DocumentTestKit.AssertValid(pdf, "tagged-list-language");
    }

    [Theory]
    [InlineData("Diagramme des ventes — été 2026", true)]
    [InlineData("Sales (net) and \\ (gross)", false)]
    [InlineData("Umsatz (netto) ⟶ \\brutto", true)]
    public void Alternative_text_is_written_as_a_text_string_and_reads_back_as_given(string alt, bool unicode)
    {
        PdfDocument document = new(Tagged());
        document.Add(new Image(Png) { Width = Length.Mm(20), AltText = alt });
        document.Add(new DrawingBlock(Length.Mm(20), Length.Mm(10), canvas => canvas.DrawText("x", 1, 8)) { AltText = alt });

        byte[] pdf = DocumentTestKit.Save(document);

        StructureTreeReader tree = Read(pdf);
        Assert.Equal([alt, alt], tree.Root.Children.Select(e => e.Alt));
        string file = Encoding.Latin1.GetString(pdf);
        if (unicode)
        {
            // UTF-16BE with its byte order mark, in hexadecimal.
            Assert.Equal(2, Regex.Count(file, "/Alt <FEFF[0-9A-F]+>", RegexOptions.IgnoreCase));
        }
        else
        {
            // A literal string, its parentheses and backslash escaped.
            Assert.Equal(2, Regex.Count(file, Regex.Escape(@"/Alt (Sales \(net\) and \\ \(gross\))")));
        }

        DocumentTestKit.AssertValid(pdf, $"tagged-figure-alt-{(unicode ? "unicode" : "escaped")}");
    }

    [Fact]
    public void An_untagged_document_with_lists_and_figures_has_no_marked_content_or_structure()
    {
        PdfDocument document = new(DocumentTestKit.Options(o => o.Tagged = false));
        document.Add(new ItemList([new ListItem([new Paragraph("One"), new ItemList(["One a"])]), new ListItem("Two")]));
        document.Add(new Image(Png) { Width = Length.Mm(20), AltText = "Picture" });
        document.Add(new DrawingBlock(Length.Mm(20), Length.Mm(10), canvas => canvas.DrawText("x", 1, 8)) { AltText = "Drawing" });

        string file = Encoding.Latin1.GetString(DocumentTestKit.Save(document));

        foreach (string key in new[] { "/StructTreeRoot", "/MarkInfo", "/StructParents", "/MCID", "/Artifact", "/Figure", "/ListNumbering" })
        {
            Assert.DoesNotContain(key, file, StringComparison.Ordinal);
        }

        Assert.DoesNotMatch(@"/Alt\b", file);
    }
}
