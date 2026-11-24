using Pdfwright.Benchmarks.Scenarios;
using Pdfwright.Samples;
using Pdfwright.Tests.Conformance;
using Pdfwright.Tests.Images;
using Pdfwright.Tests.Pdf;
using DrawingBlock = Pdfwright.Drawing;

namespace Pdfwright.Tests.Document;

/// <summary>
/// Tagged documents validated by veraPDF against PDF/UA-1 (<see cref="VeraPdf"/>): the corpus of every script, the README report, the sample program's
/// report, the benchmark scenarios that are cheap to make, and documents with every kind of block. A document with a title and a language and nothing
/// else in the way passes with no failed rule and claims PDF/UA-1; one with an accessibility issue is written without the claim and fails. For every
/// file the diagnostics' <c>IsPdfUA</c> must agree with veraPDF's verdict. Without veraPDF the tests skip (see <see cref="VeraPdf.Require"/>).
/// </summary>
public class PdfUAValidationTests
{
    private static readonly PdfImage Png = PdfImage.FromBytes(ImageTestKit.Png(16, 16, 8, 6));

    private static readonly TextStyle Arabic = new() { Family = "Noto Sans Arabic", Language = "ar" };

    /// <summary>A built document, saved, with its diagnostics.</summary>
    private sealed record Saved(string Name, byte[] Pdf, DocumentDiagnostics Diagnostics);

    private static Saved Save(string name, PdfDocument document)
    {
        byte[] pdf = DocumentTestKit.Save(document);
        return new Saved(name, pdf, document.Diagnostics);
    }

    /// <summary>
    /// Validates the files in one veraPDF run and asserts, for each, that the file and its diagnostics agree: claimed PDF/UA-1 (with no issue) when
    /// veraPDF finds it compliant, and not claimed (with an issue) when it does not; with <paramref name="allCompliant"/> every file must also be
    /// compliant. A file that fails is kept (<see cref="VeraPdf.Keep"/>). Returns veraPDF's results by name.
    /// </summary>
    private static IReadOnlyDictionary<string, VeraPdf.Result> ValidateAgreeing(IReadOnlyList<Saved> files, bool allCompliant = false)
    {
        IReadOnlyDictionary<string, VeraPdf.Result> results = VeraPdf.Validate(files.Select(f => (f.Name, f.Pdf)));
        List<string> failures = [];
        foreach (Saved file in files)
        {
            VeraPdf.Result result = results[file.Name];
            bool issues = file.Diagnostics.AccessibilityIssues.Count > 0;
            if (allCompliant && !result.Compliant)
            {
                failures.Add($"{result}\n    kept as {VeraPdf.Keep(file.Name, file.Pdf)}");
            }
            else if (file.Diagnostics.IsPdfUA != result.Compliant || file.Diagnostics.IsPdfUA == issues)
            {
                failures.Add($"{file.Name}: the diagnostics and veraPDF disagree: IsPdfUA {file.Diagnostics.IsPdfUA}, issues [{string.Join("; ", file.Diagnostics.AccessibilityIssues)}], "
                    + $"veraPDF {result}\n    kept as {VeraPdf.Keep(file.Name, file.Pdf)}");
            }
        }

        Assert.True(failures.Count == 0, $"{failures.Count} of {files.Count} file(s) failed:\n  " + string.Join("\n  ", failures));
        return results;
    }

    /// <summary>Validates documents that must be PDF/UA-1: every one compliant, claimed, and with no issue. A file that is not is kept (<see cref="VeraPdf.Keep"/>).</summary>
    private static void AssertPdfUA(IReadOnlyList<Saved> files)
    {
        _ = ValidateAgreeing(files, allCompliant: true);
        Assert.All(files, f => Assert.True(f.Diagnostics.IsPdfUA, $"{f.Name} does not claim PDF/UA-1"));
    }

    private static PdfDocumentOptions Tagged(Action<DocumentTestKit.OptionsBuilder>? change = null) => TaggedFlowTests.Tagged(o =>
    {
        o.Compress = true;
        change?.Invoke(o);
    });

    [Fact]
    public void The_corpus_of_every_script_tagged_is_pdf_ua()
    {
        _ = VeraPdf.Require();
        List<Saved> files = [];
        foreach (PageCorpus corpus in PageCorpus.LoadAll())
        {
            foreach (bool isLong in new[] { false, true })
            {
                files.Add(Save($"corpus-{corpus.Group}-{(isLong ? "long" : "short")}", corpus.Build(isLong, o => o.Tagged = true).Document));
            }
        }

        AssertPdfUA(files);
    }

    [Fact]
    public void The_reports_and_the_samples_tagged_are_pdf_ua()
    {
        _ = VeraPdf.Require();
        using FontCollection sampleFonts = ReportSample.LoadFonts(TestPaths.FontsDirectory);
        byte[] logo = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Images", "Files", "gradient-24x16.jpg"));
        List<Saved> files =
        [
            Save("readme-report", SampleDocumentTests.ReadmeReport(o => o.Tagged = true)),
            Save("readme-report-uncompressed", SampleDocumentTests.ReadmeReport(o => { o.Tagged = true; o.Compress = false; })),
            Save("sample-report", ReportSample.Build(sampleFonts, logo).WithTagged(true)),
            Save("rtl-report", SampleDocumentTests.RightToLeftReport(o => { o.Tagged = true; o.Title = "تقرير المبيعات الشهري"; })),
        ];

        // The table's header repeated on later pages is a pagination artifact with no subtype, which PDF/UA-1 accepts as any artifact.
        Assert.Contains("/Artifact <</Type /Pagination>> BDC", System.Text.Encoding.Latin1.GetString(files[1].Pdf), StringComparison.Ordinal);
        AssertPdfUA(files);
    }

    [Fact]
    public void The_benchmark_scenarios_tagged_are_pdf_ua()
    {
        _ = VeraPdf.Require();
        using FontCollection fonts = ScenarioDocuments.LoadFonts(TestPaths.FontsDirectory);
        List<Saved> files =
        [
            Save("table-1000", ScenarioDocuments.Table(fonts, tagged: true)),
            Save("report-20", ScenarioDocuments.Report20(fonts).WithTagged(true)),
            Save("report-10", ScenarioDocuments.Report(fonts).WithTagged(true)),
            Save("table-arabic", ScenarioDocuments.TableArabic(fonts).WithTagged(true)),
            Save("table-devanagari", ScenarioDocuments.TableDevanagari(fonts).WithTagged(true)),
        ];

        AssertPdfUA(files);
    }

    [Fact]
    public void Documents_with_every_kind_of_block_tagged_are_pdf_ua()
    {
        _ = VeraPdf.Require();
        List<Saved> files = [];

        PdfDocument flow = new(Tagged(o =>
        {
            o.Header = page => new Paragraph($"Header of page {page.Number}");
            o.Footer = page => new Columns([new Paragraph("Footer"), new Paragraph($"{page.Number} / {page.Count}") { Alignment = TextAlignment.End }]);
        }));
        flow.Add(new Heading(1, "Flow"));
        flow.Add(new Paragraph([new TextRun("English, "), new TextRun("Deutsch", new TextStyle { Language = "de" }), new TextRun(", and "), new TextRun("français", new TextStyle { Language = "fr" })]));
        flow.Add(new Paragraph("Ein deutscher Absatz.") { Style = new TextStyle { Language = "de" } });
        flow.Add(new Heading(2, "Long text"));
        flow.Add(new Paragraph(DocumentTestKit.LongText(900)));
        flow.Add(new Heading(2, "Empty and blank"));
        flow.Add(new Paragraph(string.Empty));
        flow.Add(new Paragraph("   "));
        flow.Add(new Heading(3, "Deeper"));
        flow.Add(new Heading(2, "Back up"));
        flow.Add(new Spacer(Length.Pt(20)));
        flow.Add(new PageBreak());
        flow.Add(new Heading(1, "Second part"));
        files.Add(Save("flow", flow));

        PdfDocument lists = new(Tagged());
        lists.Add(new Heading(1, "Lists"));
        lists.Add(new ItemList(["First item", "Second item", "Third item"]));
        lists.Add(new ItemList(["One", "Two"]) { Marker = ListMarker.Decimal, Start = 9 });
        lists.Add(new ItemList(
        [
            new ListItem([new Paragraph("One"), new ItemList(["One a", "One b"]) { Marker = ListMarker.LowerAlpha }, new Paragraph("One more")]),
            new ListItem([new Paragraph("Two")]),
        ]));
        lists.Add(new ItemList(["Short", DocumentTestKit.LongText(150), .. Enumerable.Range(0, 60).Select(i => $"Item {i}")]) { Marker = ListMarker.Decimal });
        lists.Add(new ItemList(["أول", "ثان"]) { Direction = TextDirection.RightToLeft, Style = new TextStyle { Family = "Noto Sans Arabic", Language = "ar" } });
        files.Add(Save("lists", lists));

        PdfDocument figures = new(Tagged());
        figures.Add(new Heading(1, "Figures"));
        figures.Add(new Image(Png) { Width = Length.Mm(30), AltText = "A small chart" });
        figures.Add(new Image(Png) { Width = Length.Mm(10) });
        figures.Add(new DrawingBlock(Length.Mm(80), Length.Mm(30), canvas =>
        {
            canvas.Fill(new DrawingPathBuilder().Rectangle(0, 0, 40, 20).Build(), Color.FromHex("#3366CC"));
            canvas.DrawText("Label", 10, 28);
            canvas.DrawImage(Png, 50, 0, 20, 20);
        })
        { AltText = "A bar with a label and a picture" });
        figures.Add(new DrawingBlock(Length.Mm(80), Length.Mm(10), canvas => canvas.DrawText("Decorative", 0, 10)));
        files.Add(Save("figures", figures));

        PdfDocument boxes = new(Tagged());
        boxes.Add(new Heading(1, "Callouts and columns"));
        boxes.Add(new Callout([new Paragraph("Note."), new ItemList(["Callout item"])]) { Border = new BorderLine(Length.Pt(1), Color.Black), Fill = Color.Gray(0.95) });
        boxes.Add(new Columns([[new ItemList(["Left item", "Left two"])], [new Paragraph("Right"), new Image(Png) { Width = Length.Mm(10), AltText = "Icon" }]]));
        boxes.Add(new Columns([new Paragraph("يمين") { Style = Arabic }, new Paragraph("يسار") { Style = Arabic }]) { Direction = TextDirection.RightToLeft });
        boxes.Add(new Callout(Enumerable.Range(0, 80).Select(i => (Block)new Paragraph($"Line {i} of a long note that runs over pages."))) { CornerRadius = Length.Pt(6) });
        files.Add(Save("boxes", boxes));

        PdfDocument tables = new(Tagged(o => o.Footer = page => new Paragraph($"{page.Number}")));
        tables.Add(new Heading(1, "Tables"));
        tables.Add(new Table(
            [new TableColumn(ColumnWidth.Fraction(1)), new TableColumn(ColumnWidth.Fixed(Length.Mm(30))) { Alignment = TextAlignment.End }, new TableColumn()],
            new TableRow("Customer", "Amount", string.Empty),
            Enumerable.Range(0, 120).Select(i => new TableRow($"Customer {i}", $"{i * 37 % 1000}.50", i % 3 == 0 ? string.Empty : "Paid")))
        {
            Borders = Borders.All,
            HeaderFill = Color.Gray(0.9),
            ZebraFill = Color.Gray(0.97),
        });
        tables.Add(new Heading(2, "Without a header"));
        tables.Add(new Table([new TableColumn(), new TableColumn()], [new TableRow("a", "b"), new TableRow(new TableCell([new Paragraph("Two"), new Paragraph("paragraphs")]), new TableCell("c"))]));
        tables.Add(new Heading(2, "A row over pages"));
        tables.Add(new Table([new TableColumn(), new TableColumn()], new TableRow("Short", "Long"), [new TableRow("x", DocumentTestKit.LongText(1200))]));
        files.Add(Save("tables", tables));

        // Text whose glyphs and characters do not pair one to one: ligatures, one glyph drawn for two texts (a second code and a CIDToGIDMap), a
        // cluster of several glyphs (the rest mapped to U+034F), marks, joiners and invisible characters, in every font of the collection.
        PdfDocument text = new(Tagged());
        text.Add(new Heading(1, "Text"));
        text.Add(new Paragraph("office affine fluff \uFB01 \uFB02, a\u00A0b c\u2007d, e\u0301 n\u0303, soft\u00ADhyphen, zero\u200Bwidth, joiner\u200D\u200Cnon-joiner, word\u2060joiner, \uFEFF."));
        text.Add(new Paragraph("Bold, italic and both.") { Style = new TextStyle { Weight = 700 } });
        text.Add(new Paragraph([new TextRun("Italic ", new TextStyle { Italic = true }), new TextRun("small ", new TextStyle { Size = Length.Pt(6) }), new TextRun("large", new TextStyle { Size = Length.Pt(24) })]));
        text.Add(new Paragraph("لا إله، السلام عليكم ١٢٣ 123") { Style = Arabic, Direction = TextDirection.RightToLeft });
        text.Add(new Paragraph("עברית עם ניקוד: שָׁלוֹם") { Style = new TextStyle { Family = "Noto Sans Hebrew", Language = "he" } });
        text.Add(new Paragraph("क्षत्रिय श्री द्वार हिन्दी") { Style = new TextStyle { Family = "Noto Sans Devanagari", Language = "hi" } });
        text.Add(new Paragraph("ক্ষ্ম বাংলা") { Style = new TextStyle { Family = "Noto Sans Bengali", Language = "bn" } });
        text.Add(new Paragraph("க்ஷ ஸ்ரீ தமிழ்") { Style = new TextStyle { Family = "Noto Sans Tamil", Language = "ta" } });
        text.Add(new Paragraph("ภาษาไทย น้ำ ที่") { Style = new TextStyle { Family = "Noto Sans Thai", Language = "th" } });
        text.Add(new Paragraph("Ελληνικά, кириллица, Tiếng Việt") { Style = new TextStyle { Color = Color.Red } });
        files.Add(Save("text", text));

        // A document with no blocks is one empty page: an empty Document element.
        files.Add(Save("empty", new PdfDocument(Tagged())));

        AssertPdfUA(files);
    }

    [Fact]
    public void A_document_with_an_accessibility_issue_is_tagged_without_the_claim_and_fails_validation()
    {
        _ = VeraPdf.Require();
        List<Saved> files = [];

        PdfDocument noTitle = new(Tagged(o => o.Title = null));
        noTitle.Add(new Heading(1, "No title")).Add(new Paragraph("Text."));
        files.Add(Save("no-title", noTitle));

        PdfDocument blankTitle = new(Tagged(o => o.Title = " \t"));
        blankTitle.Add(new Paragraph("Text."));
        files.Add(Save("blank-title", blankTitle));

        PdfDocument noLanguage = new(Tagged(o => o.Language = null));
        noLanguage.Add(new Paragraph("Text."));
        files.Add(Save("no-language", noLanguage));

        PdfDocument badLanguage = new(Tagged(o => o.Language = "en_US"));
        badLanguage.Add(new Paragraph("Text."));
        files.Add(Save("invalid-language", badLanguage));

        PdfDocument badRunLanguage = new(Tagged());
        badRunLanguage.Add(new Paragraph("Text."));
        badRunLanguage.Add(new PageBreak());
        badRunLanguage.Add(new Paragraph([new TextRun("A run in "), new TextRun("no language", new TextStyle { Language = "qq!" })]));
        files.Add(Save("invalid-run-language", badRunLanguage));

        PdfDocument missingGlyph = new(Tagged(o => o.Footer = page => new Paragraph("Footer")));
        missingGlyph.Add(new Paragraph("Text."));
        missingGlyph.Add(new PageBreak());
        missingGlyph.Add(new Paragraph("A character no font has: \u4E00."));
        files.Add(Save("missing-glyph", missingGlyph));

        // A noncharacter has no glyph either; its text is kept out of ToUnicode (PDF/UA-1 7.21.7), so the glyph is the one rule it fails.
        PdfDocument noncharacter = new(Tagged());
        noncharacter.Add(new Paragraph("A noncharacter: \uFFFE."));
        files.Add(Save("noncharacter", noncharacter));

        PdfDocument skipped = new(Tagged());
        skipped.Add(new Heading(1, "One")).Add(new Heading(3, "Three"));
        files.Add(Save("skipped-heading", skipped));

        PdfDocument firstH2 = new(Tagged());
        firstH2.Add(new Heading(2, "Two")).Add(new Paragraph("Text."));
        files.Add(Save("first-heading-h2", firstH2));

        IReadOnlyList<AccessibilityIssue> Issues(string name) => files.Single(f => f.Name == name).Diagnostics.AccessibilityIssues;
        Assert.Equal([AccessibilityIssueKind.MissingTitle], Issues("no-title").Select(i => i.Kind));
        Assert.Equal([AccessibilityIssueKind.MissingTitle], Issues("blank-title").Select(i => i.Kind));
        Assert.Equal([AccessibilityIssueKind.MissingLanguage], Issues("no-language").Select(i => i.Kind));
        AccessibilityIssue language = Assert.Single(Issues("invalid-language"));
        Assert.Equal((AccessibilityIssueKind.InvalidLanguage, (int?)null), (language.Kind, language.Page));
        AccessibilityIssue runLanguage = Assert.Single(Issues("invalid-run-language"));
        Assert.Equal((AccessibilityIssueKind.InvalidLanguage, (int?)2), (runLanguage.Kind, runLanguage.Page));
        AccessibilityIssue glyph = Assert.Single(Issues("missing-glyph"));
        Assert.Equal((AccessibilityIssueKind.MissingGlyph, (int?)2), (glyph.Kind, glyph.Page));
        Assert.Contains("U+4E00", glyph.Message, StringComparison.Ordinal);
        Assert.Equal([AccessibilityIssueKind.MissingGlyph], Issues("noncharacter").Select(i => i.Kind));
        AccessibilityIssue skip = Assert.Single(Issues("skipped-heading"));
        Assert.Equal((AccessibilityIssueKind.SkippedHeadingLevel, (int?)1), (skip.Kind, skip.Page));
        Assert.Equal([AccessibilityIssueKind.FirstHeadingNotH1], Issues("first-heading-h2").Select(i => i.Kind));
        Assert.All(files, f => Assert.False(f.Diagnostics.IsPdfUA));
        Assert.All(files, f => Assert.DoesNotContain("pdfuaid", Metadata(f.Pdf), StringComparison.Ordinal));

        IReadOnlyDictionary<string, VeraPdf.Result> results = ValidateAgreeing(files);

        // Each fails the rule of the missing claim and the rules of its issue, and nothing else.
        string[] title = [VeraPdf.ClaimRule, "7.1-9", "7.1-10"];
        string[] heading = [VeraPdf.ClaimRule, "7.4.2-1"];
        Dictionary<string, string[]> expected = new()
        {
            ["no-title"] = title,
            ["blank-title"] = title,
            ["no-language"] = [VeraPdf.ClaimRule, "7.2-33", "7.2-34"],
            ["invalid-language"] = [VeraPdf.ClaimRule, "7.2-29"],
            ["invalid-run-language"] = [VeraPdf.ClaimRule, "7.2-29"],
            ["missing-glyph"] = [VeraPdf.ClaimRule, "7.21.8-1"],
            ["noncharacter"] = [VeraPdf.ClaimRule, "7.21.8-1"],
            ["skipped-heading"] = heading,
            ["first-heading-h2"] = heading,
        };
        Assert.All(expected, e => Assert.True(
            e.Value.Order(StringComparer.Ordinal).SequenceEqual(results[e.Key].FailedIds.Order(StringComparer.Ordinal)),
            $"{results[e.Key]}\n  expected the rules {string.Join(", ", e.Value)}"));
    }

    [Fact]
    public void An_untagged_document_has_no_claim_and_no_issue_and_a_tagged_one_with_none_has_the_claim()
    {
        PdfDocument untagged = new(DocumentTestKit.Options());
        untagged.Add(new Heading(3, "No title, no language, a skipped level: \u4E00"));
        byte[] plain = DocumentTestKit.Save(untagged);
        Assert.False(untagged.Diagnostics.IsPdfUA);
        Assert.Empty(untagged.Diagnostics.AccessibilityIssues);
        Assert.DoesNotContain("pdfuaid", Metadata(plain), StringComparison.Ordinal);

        PdfDocument tagged = new(Tagged());
        tagged.Add(new Heading(1, "Title")).Add(new Paragraph("Text."));
        byte[] pdf = DocumentTestKit.Save(tagged);
        Assert.True(tagged.Diagnostics.IsPdfUA);
        Assert.Empty(tagged.Diagnostics.AccessibilityIssues);
        Assert.Contains("<pdfuaid:part>1</pdfuaid:part>", Metadata(pdf), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("en", true)]
    [InlineData("en-US", true)]
    [InlineData("zh-Hant-TW", true)]
    [InlineData("english", true)]
    [InlineData("x-klingon", true)]
    [InlineData("de-1996", true)]
    [InlineData("en_US", false)]
    [InlineData("qq!", false)]
    [InlineData("en-", false)]
    [InlineData("-en", false)]
    [InlineData("1en", false)]
    [InlineData("toolongtag", false)]
    [InlineData(" en", false)]
    public void A_language_is_a_language_tag_of_rfc_3066(string language, bool valid) =>
        Assert.Equal(valid, Pdfwright.Document.DocumentRenderer.IsLanguageTag(language));

    /// <summary>The XMP packet of a file, which is never compressed.</summary>
    private static string Metadata(byte[] pdf)
    {
        string text = System.Text.Encoding.Latin1.GetString(pdf);
        int start = text.IndexOf("<?xpacket begin", StringComparison.Ordinal);
        int end = text.IndexOf("<?xpacket end", start, StringComparison.Ordinal);
        return text[start..end];
    }
}
