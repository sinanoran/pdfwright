using System.Text;
using System.Text.RegularExpressions;
using Pdfwright.Tests.Conformance;
using Pdfwright.Tests.Images;
using Pdfwright.Tests.Pdf;
using UglyToad.PdfPig.Content;
using DrawingBlock = Pdfwright.Drawing;

namespace Pdfwright.Tests.Document;

/// <summary>
/// The document layer of tagged PDF for flow text, behind the internal <see cref="PdfDocumentOptions.Tagged"/>: the <c>Document</c> element,
/// headings and paragraphs, a paragraph split between pages, <c>/Lang</c> on elements and spans, the header and the footer as pagination artifacts,
/// callouts and columns as <c>Div</c>. The documents are made through the public API and read back by <see cref="StructureTreeReader"/>, and the
/// text of each element by PdfPig's marked content.
/// </summary>
public partial class TaggedFlowTests
{
    private static PdfDocumentOptions Tagged(Action<DocumentTestKit.OptionsBuilder>? change = null) =>
        DocumentTestKit.Options(o =>
        {
            o.Tagged = true;
            o.Language = "en";
            o.Title = "Tagged";
            change?.Invoke(o);
        });

    /// <summary>The text of each element of the tree, in the tree's order: the letters of its marked content, page by page, with white space left out.</summary>
    private static List<(string Type, string Text)> ElementTexts(byte[] pdf, StructureTreeReader tree)
    {
        Dictionary<(int Page, int Mcid), string> marked = MarkedTexts(pdf);
        List<(string, string)> texts = [];
        foreach (StructureTreeReader.Element element in tree.Root.Descendants().Skip(1))
        {
            string text = string.Concat(element.Kids.OfType<StructureTreeReader.ContentKid>().Select(k => marked.GetValueOrDefault((k.Page, k.Mcid), string.Empty)));
            texts.Add((element.Type, text));
        }

        return texts;
    }

    /// <summary>The text of each marked-content sequence with an <c>MCID</c>, by page and number, as PdfPig reads it.</summary>
    private static Dictionary<(int Page, int Mcid), string> MarkedTexts(byte[] pdf)
    {
        using UglyToad.PdfPig.PdfDocument read = UglyToad.PdfPig.PdfDocument.Open(pdf);
        Dictionary<(int, int), string> texts = [];
        Page[] pages = DocumentTestKit.Pages(read);
        for (int page = 0; page < pages.Length; page++)
        {
            foreach (MarkedContentElement element in pages[page].GetMarkedContents())
            {
                if (!element.IsArtifact && element.MarkedContentIdentifier >= 0)
                {
                    texts[(page, element.MarkedContentIdentifier)] = DocumentTestKit.Strip(string.Concat(Letters(element).Select(l => l.Value)));
                }
            }
        }

        return texts;
    }

    /// <summary>The text of the artifacts of each page, as PdfPig reads it.</summary>
    private static string[] ArtifactTexts(byte[] pdf)
    {
        using UglyToad.PdfPig.PdfDocument read = UglyToad.PdfPig.PdfDocument.Open(pdf);
        return [.. DocumentTestKit.Pages(read).Select(p => DocumentTestKit.Strip(string.Concat(p.GetMarkedContents().Where(e => e.IsArtifact).SelectMany(Letters).Select(l => l.Value))))];
    }

    /// <summary>The letters of a marked-content sequence and of the sequences inside it, once each, in the order they are drawn.</summary>
    private static List<Letter> Letters(MarkedContentElement element)
    {
        List<Letter> letters = [];
        HashSet<Letter> seen = new(ReferenceEqualityComparer.Instance);
        void Walk(MarkedContentElement e)
        {
            foreach (Letter letter in e.Letters)
            {
                if (seen.Add(letter))
                {
                    letters.Add(letter);
                }
            }

            foreach (MarkedContentElement child in e.Children)
            {
                Walk(child);
            }
        }

        Walk(element);
        return [.. letters.OrderBy(l => l.TextSequence)];
    }

    private static StructureTreeReader Read(byte[] pdf)
    {
        StructureTreeReader tree = StructureTreeReader.Read(pdf);
        tree.AssertConsistent();
        return tree;
    }

    [Fact]
    public void Headings_and_paragraphs_are_elements_in_reading_order_with_their_text()
    {
        PdfDocument document = new(Tagged());
        document.Add(new Heading(1, "Annual report"));
        document.Add(new Paragraph("The first paragraph."));
        document.Add(new Heading(2, "Results"));
        document.Add(new Paragraph([new TextRun("Sales rose "), new TextRun("twelve", new TextStyle { Weight = 700 }), new TextRun(" percent.")]));
        document.Add(new Heading(6, "Notes"));
        document.Add(new Spacer(Length.Pt(10)));
        document.Add(new Paragraph("The end."));

        byte[] pdf = DocumentTestKit.Save(document);

        StructureTreeReader tree = Read(pdf);
        Assert.Equal("Document[H1,P,H2,P,H6,P]", tree.Root.Outline());
        Assert.Equal(
            [("H1", "Annualreport"), ("P", "Thefirstparagraph."), ("H2", "Results"), ("P", "Salesrosetwelvepercent."), ("H6", "Notes"), ("P", "Theend.")],
            ElementTexts(pdf, tree));
        Assert.All(tree.Root.Descendants(), e => Assert.Null(e.Language));
        DocumentTestKit.AssertValid(pdf, "tagged-flow-headings");
    }

    [Fact]
    public void A_paragraph_split_between_pages_is_one_element_with_a_marked_content_reference_on_the_next_page()
    {
        PdfDocument document = new(Tagged(o => o.PageSize = new PageSize(Length.Mm(100), Length.Mm(80))));
        document.Add(new Heading(1, "Long"));
        document.Add(new Paragraph(DocumentTestKit.LongText(120)));
        document.Add(new Paragraph("After."));

        byte[] pdf = DocumentTestKit.Save(document);

        StructureTreeReader tree = Read(pdf);
        Assert.Equal("Document[H1,P,P]", tree.Root.Outline());
        StructureTreeReader.Element split = tree.Root.Children.ElementAt(1);
        Assert.True(tree.StructParents.Count >= 2);
        Assert.Equal(0, split.Page);
        List<StructureTreeReader.ContentKid> kids = [.. split.Kids.OfType<StructureTreeReader.ContentKid>()];
        Assert.True(kids.Count >= 2, "the paragraph has content on two pages or more");
        Assert.Equal(Enumerable.Range(0, kids.Count), kids.Select(k => k.Page));
        Assert.False(kids[0].Reference);
        Assert.All(kids.Skip(1), k => Assert.True(k.Reference));

        // Its text, read through its marked content page by page, is the whole paragraph.
        Assert.Equal(DocumentTestKit.Strip(DocumentTestKit.LongText(120)), ElementTexts(pdf, tree)[1].Text);
        DocumentTestKit.AssertValid(pdf, "tagged-flow-split");
    }

    [Fact]
    public void The_header_and_the_footer_are_pagination_artifacts_with_no_element()
    {
        PdfImage logo = PdfImage.FromBytes(ImageTestKit.Png(16, 16, 8, 6));
        PdfDocument document = new(Tagged(o =>
        {
            o.PageSize = new PageSize(Length.Mm(120), Length.Mm(100));
            o.Margins = Margins.Mm(top: 20, right: 15, bottom: 25, left: 15);
            o.FooterOffset = Length.Mm(3);
            o.Header = page => new Columns([new Image(logo) { Width = Length.Pt(12) }, new Paragraph("Running head") { Alignment = TextAlignment.End }]);
            o.Footer = page => page.IsFirst ? new Callout(new Paragraph($"Folio {page.Number}")) { Fill = Color.Gray(0.9) } : new Paragraph($"Folio {page.Number}");
        }));
        document.Add(new Heading(1, "Body"));
        document.Add(new Paragraph(DocumentTestKit.LongText(150)));

        byte[] pdf = DocumentTestKit.Save(document);

        StructureTreeReader tree = Read(pdf);
        Assert.Equal("Document[H1,P]", tree.Root.Outline());
        Assert.True(tree.ContentStreams.Count >= 2);
        string[] artifacts = ArtifactTexts(pdf);
        for (int page = 0; page < tree.ContentStreams.Count; page++)
        {
            List<string> streams = tree.ContentStreams[page];
            Assert.Equal(3, streams.Count);
            Assert.StartsWith("/Artifact <</Type /Pagination /Subtype /Header>> BDC\n", streams[0], StringComparison.Ordinal);
            Assert.EndsWith("EMC\n", streams[0], StringComparison.Ordinal);
            Assert.StartsWith("/Artifact <</Type /Pagination /Subtype /Footer>> BDC\n", streams[2], StringComparison.Ordinal);
            Assert.EndsWith("EMC\n", streams[2], StringComparison.Ordinal);
            Assert.DoesNotContain("/MCID", streams[0], StringComparison.Ordinal);
            Assert.DoesNotContain("/MCID", streams[2], StringComparison.Ordinal);
            Assert.Contains("Runninghead", artifacts[page], StringComparison.Ordinal);
            Assert.Contains($"Folio{page + 1}", artifacts[page], StringComparison.Ordinal);
        }

        Assert.All(ElementTexts(pdf, tree), e => Assert.DoesNotContain("Folio", e.Text, StringComparison.Ordinal));
        Assert.All(ElementTexts(pdf, tree), e => Assert.DoesNotContain("Running", e.Text, StringComparison.Ordinal));
        DocumentTestKit.AssertValid(pdf, "tagged-flow-overlays");
    }

    [Fact]
    public void A_paragraph_in_another_language_has_its_lang_and_a_run_in_another_language_is_a_span()
    {
        TextStyle arabic = new() { Family = "Noto Sans Arabic", Language = "ar" };
        PdfDocument document = new(Tagged());
        document.Add(new Paragraph([new TextRun("An English paragraph with "), new TextRun("كلمة عربية", arabic), new TextRun(" in it.")]));
        document.Add(new Paragraph([new TextRun("Un paragraphe en français avec un mot "), new TextRun("English", new TextStyle { Language = "en" }), new TextRun(".")])
        {
            Style = new TextStyle { Language = "fr" },
        });
        document.Add(new Heading(2, [new TextRun("Überschrift", new TextStyle { Language = "de" })]));
        document.Add(new Paragraph("English again, with an explicit tag.") { Style = new TextStyle { Language = "EN" } });

        byte[] pdf = DocumentTestKit.Save(document);

        StructureTreeReader tree = Read(pdf);
        Assert.Equal("Document[P,P,H2,P]", tree.Root.Outline());
        List<StructureTreeReader.Element> elements = [.. tree.Root.Children];
        Assert.Null(elements[0].Language);
        Assert.Equal("fr", elements[1].Language);
        Assert.Null(elements[3].Language);

        // The heading's base style is the document's: its only run, in German, is a span inside it.
        Assert.Null(elements[2].Language);

        string content = tree.ContentText[0];
        Assert.Equal("ar", SpansIn(content, "P", 0).Single());
        Assert.Equal("en", SpansIn(content, "P", 1).Single());
        Assert.Equal("de", SpansIn(content, "H2", 2).Single());
        Assert.Empty(SpansIn(content, "P", 3));

        // PdfPig reads the span's language inside the paragraph's marked content.
        using UglyToad.PdfPig.PdfDocument read = UglyToad.PdfPig.PdfDocument.Open(pdf);
        MarkedContentElement first = DocumentTestKit.Pages(read)[0].GetMarkedContents().First(e => e.MarkedContentIdentifier == 0);
        Assert.Contains(first.Children, c => c.Language == "ar");
        DocumentTestKit.AssertValid(pdf, "tagged-flow-language");
    }

    /// <summary>The languages of the spans inside the marked content of <paramref name="tag"/> numbered <paramref name="mcid"/>, in order.</summary>
    private static List<string> SpansIn(string content, string tag, int mcid)
    {
        string begin = $"/{tag} <</MCID {mcid}>> BDC\n";
        int start = content.IndexOf(begin, StringComparison.Ordinal);
        Assert.True(start >= 0, $"no {begin.Trim()}");
        int depth = 0;
        List<string> spans = [];
        foreach (Match op in MarkedContentPattern().Matches(content, start))
        {
            if (op.Groups["emc"].Success)
            {
                depth--;
                if (depth == 0)
                {
                    return spans;
                }

                continue;
            }

            depth++;
            if (op.Groups["lang"].Success)
            {
                Assert.Equal(2, depth); // directly inside the element's content
                spans.Add(op.Groups["lang"].Value);
            }
        }

        Assert.Fail($"{begin.Trim()} is not ended");
        return spans;
    }

    [GeneratedRegex(@"(?:/Span <</Lang \((?<lang>[^)]*)\)>> BDC|/[A-Za-z0-9]+ <<[^\n]*>> BDC|/[A-Za-z0-9]+ BMC|(?<emc>EMC))\n")]
    private static partial Regex MarkedContentPattern();

    [Theory]
    [InlineData(TextDirection.LeftToRight)]
    [InlineData(TextDirection.RightToLeft)]
    public void Callouts_and_columns_are_divisions_and_the_columns_are_in_logical_order(TextDirection direction)
    {
        PdfDocument document = new(Tagged());
        document.Add(new Paragraph("Before."));
        document.Add(new Callout([new Paragraph("Inside one."), new Heading(3, "Inside two")]) { Fill = Color.Gray(0.9), Border = new BorderLine(Length.Pt(1), Color.Black) });
        document.Add(new Columns([new Paragraph("Alpha"), new Paragraph("Beta"), new Paragraph("Gamma")]) { Direction = direction });
        document.Add(new Callout(new Columns([[new Paragraph("Left one"), new Paragraph("Left two")], [new Paragraph("Right")]])));
        document.Add(new Paragraph("After."));

        byte[] pdf = DocumentTestKit.Save(document);

        StructureTreeReader tree = Read(pdf);
        Assert.Equal("Document[P,Div[P,H3],Div[Div[P],Div[P],Div[P]],Div[Div[Div[P,P],Div[P]]],P]", tree.Root.Outline());
        Assert.Equal(
            ["Before.", "", "Insideone.", "Insidetwo", "", "", "Alpha", "", "Beta", "", "Gamma", "", "", "", "Leftone", "Lefttwo", "", "Right", "After."],
            ElementTexts(pdf, tree).Select(e => e.Text));

        // The box is an artifact, drawn before the content it holds.
        string content = tree.ContentText[0];
        int box = content.IndexOf("/Artifact BMC\n", StringComparison.Ordinal);
        Assert.True(box >= 0 && box < content.IndexOf("/P <</MCID 1>> BDC", StringComparison.Ordinal));
        DocumentTestKit.AssertValid(pdf, $"tagged-flow-divisions-{direction}");
    }

    [Fact]
    public void A_callout_split_between_pages_is_one_division_and_its_box_is_an_artifact_on_each_page()
    {
        PdfDocument document = new(Tagged(o => o.PageSize = new PageSize(Length.Mm(100), Length.Mm(80))));
        document.Add(new Callout(Enumerable.Range(0, 20).Select(i => (Block)new Paragraph($"Line {i} of a long note"))) { Border = new BorderLine(Length.Pt(1), Color.Black) });

        byte[] pdf = DocumentTestKit.Save(document);

        StructureTreeReader tree = Read(pdf);
        StructureTreeReader.Element division = Assert.Single(tree.Root.Children);
        Assert.Equal("Div", division.Type);
        Assert.Equal(20, division.Children.Count());
        Assert.True(tree.ContentStreams.Count >= 2);
        Assert.Equal(
            Enumerable.Range(0, tree.ContentStreams.Count),
            division.Children.SelectMany(p => p.Kids.OfType<StructureTreeReader.ContentKid>()).Select(k => k.Page).Distinct());
        Assert.All(tree.ContentText, page => Assert.Contains("/Artifact BMC\n", page, StringComparison.Ordinal));
        Assert.Equal(Enumerable.Range(0, 20).Select(i => $"Line{i}ofalongnote"), ElementTexts(pdf, tree).Skip(1).Select(e => e.Text));
        DocumentTestKit.AssertValid(pdf, "tagged-flow-split-callout");
    }

    /// <summary>
    /// Lists, tables, images and drawings are not tagged in this slice: they are artifacts, in the flow, in a callout, in columns and in a header, and
    /// the file stays consistent around them.
    /// </summary>
    [Fact]
    public void Blocks_not_tagged_yet_are_artifacts_and_the_file_stays_consistent()
    {
        PdfImage png = PdfImage.FromBytes(ImageTestKit.Png(16, 16, 8, 6));
        DrawingBlock drawing = new(Length.Mm(40), Length.Mm(20), canvas =>
        {
            canvas.Fill(new DrawingPathBuilder().Rectangle(0, 0, canvas.Width, canvas.Height).Build(), Color.Gray(0.85));
            canvas.DrawText("Canvas text", 4, 12);
        });
        PdfDocument document = new(Tagged(o =>
        {
            o.Header = page => new Columns([new Image(png) { Width = Length.Pt(12) }, new ItemList(["Header item"])]);
            o.Footer = page => new Paragraph($"Page {page.Number}");
        }));
        document.Add(new Heading(1, "Mixed"));
        document.Add(new ItemList(["First item", "Second item"]));
        document.Add(new Paragraph("Between."));
        document.Add(new Table([new TableColumn(), new TableColumn()], new TableRow("Name", "Value"), Enumerable.Range(0, 80).Select(i => new TableRow($"Row {i}", $"{i}")))
        {
            Borders = Borders.Horizontal,
            HeaderFill = Color.Gray(0.9),
            ZebraFill = Color.Gray(0.97),
        });
        document.Add(new Callout([new Paragraph("In the callout."), new ItemList(["Callout item"]), new Image(png) { Width = Length.Mm(10) }, drawing]));
        document.Add(new Columns([[new Image(png) { Width = Length.Mm(10) }], [new ItemList(["Column item"])], [new Paragraph("Caption")]]));
        document.Add(drawing);
        document.Add(new Paragraph("The end."));

        byte[] pdf = DocumentTestKit.Save(document);

        StructureTreeReader tree = Read(pdf);
        Assert.Equal("Document[H1,P,Div[P],Div[Div[P]],P]", tree.Root.Outline());
        Assert.Equal(["Mixed", "Between.", "", "Inthecallout.", "", "", "Caption", "Theend."], ElementTexts(pdf, tree).Select(e => e.Text));
        string artifacts = string.Concat(ArtifactTexts(pdf));
        foreach (string text in new[] { "Firstitem", "Row79", "Calloutitem", "Columnitem", "Canvastext", "Headeritem" })
        {
            Assert.Contains(text, artifacts, StringComparison.Ordinal);
        }

        DocumentTestKit.AssertValid(pdf, "tagged-flow-artifacts");
    }

    [Fact]
    public void The_report_of_the_readme_tagged_is_consistent_and_valid()
    {
        byte[] pdf = DocumentTestKit.Save(SampleDocumentTests.ReadmeReport(o =>
        {
            o.Compress = false;
            o.Tagged = true;
        }));

        StructureTreeReader tree = Read(pdf);
        Assert.Equal("Document[H1,P,Div[Div[Div[P],Div[P],Div[P]]],H2,H2]", tree.Root.Outline());
        Assert.Equal("Monthlyreport", ElementTexts(pdf, tree)[0].Text);
        DocumentTestKit.AssertValid(pdf, "tagged-readme-report");
    }

    public static TheoryData<string, bool> CorpusPages()
    {
        TheoryData<string, bool> data = [];
        foreach (PageCorpus corpus in PageCorpus.LoadAll())
        {
            data.Add(corpus.Group, false);
            data.Add(corpus.Group, true);
        }

        return data;
    }

    /// <summary>The pages of the corpus, every script, tagged: the headings and paragraphs are elements with the text they were given, in order.</summary>
    [Theory]
    [MemberData(nameof(CorpusPages))]
    public void A_corpus_page_tagged_is_consistent_and_valid(string group, bool isLong)
    {
        PageCorpus corpus = PageCorpus.LoadAll().Single(c => c.Group == group);
        PageCorpus.Built built = corpus.Build(isLong, o =>
        {
            o.Compress = false;
            o.Tagged = true;
        });

        byte[] pdf = DocumentTestKit.Save(built.Document);

        StructureTreeReader tree = Read(pdf);
        List<PageCorpus.Item> flow = [.. corpus.Items.Where(i => i.Page == (isLong ? "long" : "short") && i.Kind is "heading" or "heading2" or "para")];
        List<(string Type, string Text)> elements = ElementTexts(pdf, tree);
        Assert.Equal(flow.Select(i => i.Kind switch { "heading" => "H1", "heading2" => "H2", _ => "P" }), elements.Select(e => e.Type));
        Assert.Equal(flow.Select(i => Canonical(DocumentTestKit.Strip(i.Cells[0]))), elements.Select(e => Canonical(e.Text)));
        DocumentTestKit.AssertValid(pdf, $"tagged-corpus-{group}-{(isLong ? "long" : "short")}");
    }

    /// <summary>The text compared in canonical composition: a reader returns a cluster's text as the font maps it, which can be a decomposed form of what was given.</summary>
    private static string Canonical(string text) => text.Normalize(NormalizationForm.FormC);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_same_tagged_document_is_the_same_file_twice(bool compress)
    {
        byte[] Save() => DocumentTestKit.Save(SampleDocumentTests.ReadmeReport(o =>
        {
            o.Compress = compress;
            o.Tagged = true;
        }));

        Assert.Equal(Save(), Save());
    }

    [Fact]
    public void An_untagged_document_has_no_marked_content_or_structure()
    {
        string file = Encoding.Latin1.GetString(DocumentTestKit.Save(SampleDocumentTests.ReadmeReport(o => o.Compress = false)));

        foreach (string key in new[] { "/StructTreeRoot", "/MarkInfo", "/StructParents", "/MCID", "/Artifact", "/Span <</Lang" })
        {
            Assert.DoesNotContain(key, file, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Elements are written when they close, not kept to the end: in a long document most paragraphs' elements are in the file before the last page's
    /// content, so what the writer holds is the open branch of the tree.
    /// </summary>
    [Fact]
    public void A_long_document_writes_its_elements_as_they_close()
    {
        PdfDocument document = new(Tagged());
        for (int i = 0; i < 400; i++)
        {
            document.Add(new Paragraph($"Paragraph {i}. " + DocumentTestKit.LongText(30)));
        }

        byte[] pdf = DocumentTestKit.Save(document);

        StructureTreeReader tree = Read(pdf);
        Assert.Equal(400, tree.Root.Children.Count());
        Assert.True(tree.ContentStreams.Count >= 10);
        string file = Encoding.Latin1.GetString(pdf);
        int lastPage = file.LastIndexOf("/P <</MCID 0>> BDC", StringComparison.Ordinal);
        int writtenBefore = Regex.Count(file[..lastPage], @"/Type /StructElem\b");
        int onLastPage = tree.ContentMcids[^1].Count;
        Assert.True(writtenBefore >= 400 - onLastPage - 1, $"{writtenBefore} of 400 elements are written before the last page's content ({onLastPage} on it)");
    }
}
