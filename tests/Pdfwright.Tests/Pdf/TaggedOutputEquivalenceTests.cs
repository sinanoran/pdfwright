using System.Reflection;
using System.Security.Cryptography;
using Pdfwright.Benchmarks.Scenarios;
using Pdfwright.Tests.Conformance;
using Pdfwright.Tests.Document;
using Pdfwright.Tests.Images;
using DrawingBlock = Pdfwright.Drawing;

namespace Pdfwright.Tests.Pdf;

/// <summary>
/// The bytes of tagged files, and of a few untagged ones the golden files do not cover, pinned by their SHA-256: the structure tree is written by a
/// path made for allocation (no dictionaries, arrays and names built for each element), and it must write exactly the bytes of the general path it
/// replaced. The hashes were taken from the files written before that change. Every file is uncompressed and deterministic, so its bytes depend on
/// nothing but the input and are the same on every runtime.
/// </summary>
public class TaggedOutputEquivalenceTests
{
    private static readonly PdfImage Png = PdfImage.FromBytes(ImageTestKit.Png(16, 16, 8, 6));

    /// <summary>The SHA-256 of each file, in hexadecimal, as the writer wrote it before the structure tree had its own path.</summary>
    private static readonly Dictionary<string, string> Expected = new(StringComparer.Ordinal)
    {
        ["corpus-arabic-short"] = "5487BA4AE0674C0698769B2B64FEE80E5FECBB602902A3328F0F43B74C0FE2E1",
        ["corpus-arabic-long"] = "604E9EF5CAA1BF6EB507DAA86F3C554C5213B2B10733E1A0DC6202F3FD1D9077",
        ["corpus-bengali-short"] = "7A03F096AFFF115CD2DB226A4A35227313F7F60E8E68E670B80F08069553778F",
        ["corpus-bengali-long"] = "CBE250820A93D2A228A7B6833BD29A2B09F06457C801C7F487B02F5CCD4CD807",
        ["corpus-cyrillic-short"] = "3A974A02BBA28966DB81A43F1B6B3BD3E70CCA5B6A756FE2D875A25CAC0EBCA1",
        ["corpus-cyrillic-long"] = "D9E7DF2A3ED4824641DDEE90DE8E87BB4B79132279D289B14BFB06DDDE9C0A42",
        ["corpus-devanagari-short"] = "E9E8E8E14F75768BDCED859FA30F2569ACE58CECA3A357965666AE72423BAD9A",
        ["corpus-devanagari-long"] = "613CEA10DB45AA3C38298ACE7D31457A4F2C8CEA9D9BDB65F68F03A8726A3361",
        ["corpus-greek-short"] = "B59CCC49C87E8BC8BD7E764C098BA27761736848EE1D4A1397D3F04D86544FA8",
        ["corpus-greek-long"] = "A6DC83B94C5EB4A71F47C3CA90E689D01638624392E59F05D5AF1BE06EFFDDDD",
        ["corpus-hebrew-short"] = "E53B5F3D20F5432BC47BABDDB708F83E3445AE5EFCD111F30C2B049E4F7BF6DB",
        ["corpus-hebrew-long"] = "A43D526BB49339E9FEAB8041E48E5AF94E068715E4053E8AB92722C6C7E556A7",
        ["corpus-latin-extended-short"] = "DF6A6D611FCC5DB57709F1F9156854B86BC94F853BBB99B7DD5610C6D28FBE55",
        ["corpus-latin-extended-long"] = "13A20A83657F82B908AC885533773BFACEF376172DA9B6941BEC47F9681BC03B",
        ["corpus-mixed-short"] = "478BBB4F2D2790B97491F3D30797DD2E1A1989176D408C34A241CEC0567DA2C7",
        ["corpus-mixed-long"] = "8BCBF726995DA581389501D117809190A7A0DD5D576A4468FAEE16954F17CE64",
        ["corpus-tamil-short"] = "82EC3CC233729EB93045100BB5274DD51447F3BF04FD64F07F00701AF2553DA5",
        ["corpus-tamil-long"] = "1808EA3CEB23EFAF385C28CBEE091385963BDFDD2B689E4A5E88B7743C929968",
        ["corpus-thai-short"] = "4CE09D7FEA866B71D68A2313584C5313CB3B867EB31C9AFFCA075E7A010400EC",
        ["corpus-thai-long"] = "94F661EA3332D5443B6CA211DB8ABB13D5AABF65A44D9EE8024222D3DA337B72",
        ["readme-report"] = "4F46388BAEF19AF4CD93CCF594FC66A9496A2778AE95EA58ACF85E3014CB49E3",
        ["rtl-report"] = "1F846FBB976F738D7823488A7A4B154FF0C9CB31C52248FBC233CCAD371A4AD8",
        ["flow"] = "E56AFF7D9DB2803CB60BC645720950CA57B848AF2A9FDAAA609BE32B3A749844",
        ["lists"] = "0A8293C880E043CAFE6EB985126300528AFDFDD3F92B3C414F38D7DA00156E41",
        ["figures"] = "48453E03AAD71186B6B2DD8F739BBF5EBC6A8A5AF9861001B0ECD1A694318B7D",
        ["boxes"] = "078DB35B3158B65D158B152A89043EDB213E7F3B19FF8317B8F69CE2D52BB16E",
        ["tables"] = "6CB4A3F9DCDC6984D84B43733FA68545C10FC28ADB1C1850557046A033A24619",
        ["empty"] = "3B134BD895B0B3F72CF41B3F9497871EF7BFD3BAAC1CC41C7BB766C58328C08E",
        ["table-1000"] = "36547E2BCDDE7DF9029C00B16EC70611E5BD3F7A8026B780E29BA851F4508D36",
        ["table-1000-untagged"] = "F34E381B2925998A057BD24CF098E45548A9FC2C8B65AB21D295922DFBF53CF8",
        ["report-20"] = "5B59B64BE39CF656415FC7C4555B1E4D06D9D4CA01E8F2794AEC1329CF278163",
        ["report-20-untagged"] = "63AC223F36DEB1FC4AB99222859389135AF5BAE9DAD4AA2F5885703F8F03FBAE",
        ["stress"] = "4815ABB50D513DE9EEFDFF0CA025A54E7CE0FFF3F5F7F654DC22095248CB24BE",
        ["table-arabic"] = "3D4FF5570B20C5E09F0453E27347C4481255E4AFF84F461CD307C2F54D7DE744",
        ["table-devanagari"] = "A310BC11E30C0257DFBA5BC23117CD13B89F900EA2C0C73455F33AC6D3D88F5A",    };

    [Fact]
    public void Tagged_files_are_the_bytes_they_were()
    {
        List<string> failures = [];
        List<string> actual = [];
        foreach ((string name, Func<PdfDocument> build) in Documents())
        {
            string hash = Convert.ToHexString(SHA256.HashData(DocumentTestKit.Save(build())));
            actual.Add($"        [\"{name}\"] = \"{hash}\",");
            if (!Expected.TryGetValue(name, out string? expected))
            {
                failures.Add($"{name}: no expected hash");
            }
            else if (expected != hash)
            {
                failures.Add($"{name}: {hash}, expected {expected}");
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures) + "\n\nActual:\n" + string.Join("\n", actual));
        Assert.Equal(Expected.Count, actual.Count);
    }

    private static IEnumerable<(string Name, Func<PdfDocument> Build)> Documents()
    {
        foreach (PageCorpus corpus in PageCorpus.LoadAll())
        {
            foreach (bool isLong in new[] { false, true })
            {
                yield return ($"corpus-{corpus.Group}-{(isLong ? "long" : "short")}", () => corpus.Build(isLong, o => { o.Tagged = true; o.Compress = false; }).Document);
            }
        }

        yield return ("readme-report", () => SampleDocumentTests.ReadmeReport(o => { o.Tagged = true; o.Compress = false; }));
        yield return ("rtl-report", () => SampleDocumentTests.RightToLeftReport(o => { o.Tagged = true; o.Compress = false; o.Title = "تقرير المبيعات الشهري"; }));
        yield return ("flow", Flow);
        yield return ("lists", Lists);
        yield return ("figures", Figures);
        yield return ("boxes", Boxes);
        yield return ("tables", Tables);
        yield return ("empty", () => new PdfDocument(Tagged()));

        FontCollection fonts = Fonts.Value;
        yield return ("table-1000", () => Plain(ScenarioDocuments.Table(fonts), tagged: true));
        yield return ("table-1000-untagged", () => Plain(ScenarioDocuments.Table(fonts), tagged: false));
        yield return ("report-20", () => Plain(ScenarioDocuments.Report20(fonts), tagged: true));
        yield return ("report-20-untagged", () => Plain(ScenarioDocuments.Report20(fonts), tagged: false));
        yield return ("stress", () => Plain(ScenarioDocuments.Stress(fonts), tagged: true));
        yield return ("table-arabic", () => Plain(ScenarioDocuments.TableArabic(fonts, rows: 200), tagged: true));
        yield return ("table-devanagari", () => Plain(ScenarioDocuments.TableDevanagari(fonts, rows: 200), tagged: true));
    }

    private static readonly Lazy<FontCollection> Fonts = new(() => ScenarioDocuments.LoadFonts(TestPaths.FontsDirectory));

    private static PdfDocumentOptions Tagged(Action<DocumentTestKit.OptionsBuilder>? change = null) => TaggedFlowTests.Tagged(change);

    /// <summary>
    /// A copy of a document of the benchmark scenarios, which are compressed and dated by the clock, written uncompressed and deterministic: the copy's
    /// options are its own (<see cref="PdfDocument.WithTagged"/> copies them), so they are changed through their init-only setters.
    /// </summary>
    private static PdfDocument Plain(PdfDocument document, bool tagged)
    {
        PdfDocument copy = document.WithTagged(tagged);
        PdfDocumentOptions options = (PdfDocumentOptions)typeof(PdfDocument).GetField("_options", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(copy)!;
        typeof(PdfDocumentOptions).GetProperty(nameof(PdfDocumentOptions.Compress))!.SetValue(options, false);
        typeof(PdfDocumentOptions).GetProperty(nameof(PdfDocumentOptions.Deterministic))!.SetValue(options, true);
        return copy;
    }

    private static PdfDocument Flow()
    {
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
        return flow;
    }

    private static PdfDocument Lists()
    {
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
        return lists;
    }

    private static PdfDocument Figures()
    {
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
        { AltText = "A bar with a label and a picture (and parentheses \\ backslash), naïve" });
        figures.Add(new DrawingBlock(Length.Mm(80), Length.Mm(10), canvas => canvas.DrawText("Decorative", 0, 10)));
        return figures;
    }

    private static PdfDocument Boxes()
    {
        TextStyle arabic = new() { Family = "Noto Sans Arabic", Language = "ar" };
        PdfDocument boxes = new(Tagged());
        boxes.Add(new Heading(1, "Callouts and columns"));
        boxes.Add(new Callout([new Paragraph("Note."), new ItemList(["Callout item"])]) { Border = new BorderLine(Length.Pt(1), Color.Black), Fill = Color.Gray(0.95) });
        boxes.Add(new Columns([[new ItemList(["Left item", "Left two"])], [new Paragraph("Right"), new Image(Png) { Width = Length.Mm(10), AltText = "Icon" }]]));
        boxes.Add(new Columns([new Paragraph("يمين") { Style = arabic }, new Paragraph("يسار") { Style = arabic }]) { Direction = TextDirection.RightToLeft });
        boxes.Add(new Callout(Enumerable.Range(0, 80).Select(i => (Block)new Paragraph($"Line {i} of a long note that runs over pages."))) { CornerRadius = Length.Pt(6) });
        return boxes;
    }

    private static PdfDocument Tables()
    {
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
        tables.Add(new Heading(2, "Languages in cells"));
        tables.Add(new Table(
            [new TableColumn(), new TableColumn()],
            new TableRow(new TableCell("Word"), new TableCell([new Paragraph("Wort") { Style = new TextStyle { Language = "de" } }])),
            [new TableRow(new TableCell([new Paragraph("mot") { Style = new TextStyle { Language = "fr" } }]), new TableCell([new Paragraph("Zwei") { Style = new TextStyle { Language = "de" } }, new Paragraph("two")])), new TableRow("only one")]));
        tables.Add(new Heading(2, "A row over pages"));
        tables.Add(new Table([new TableColumn(), new TableColumn()], new TableRow("Short", "Long"), [new TableRow("x", DocumentTestKit.LongText(1200)), new TableRow(DocumentTestKit.LongText(900), DocumentTestKit.LongText(1300))]));
        return tables;
    }
}
