using DrawingBlock = Pdfwright.Drawing;
namespace Pdfwright.Tests.Document;

/// <summary>Every text entry point of the public API, and the awkward strings that are put into each (line breaks of every kind, controls, broken surrogates, unassigned code points).</summary>
internal static class TextInputKit
{
    internal static readonly (string Name, string Text)[] Specials =
    [
        ("LF", "\n"),
        ("CRLF", "\r\n"),
        ("CR", "\r"),
        ("LS", ((char)0x2028).ToString()),
        ("PS", ((char)0x2029).ToString()),
        ("NEL", ((char)0x0085).ToString()),
        ("VT", ((char)0x000B).ToString()),
        ("FF", ((char)0x000C).ToString()),
        ("FS", ((char)0x001C).ToString()),
        ("GS", ((char)0x001D).ToString()),
        ("RS", ((char)0x001E).ToString()),
        ("US", ((char)0x001F).ToString()),
        ("NUL", ((char)0x0000).ToString()),
        ("SOH", ((char)0x0001).ToString()),
        ("BEL", ((char)0x0007).ToString()),
        ("BS", ((char)0x0008).ToString()),
        ("ESC", ((char)0x001B).ToString()),
        ("DEL", ((char)0x007F).ToString()),
        ("C1-80", ((char)0x0080).ToString()),
        ("C1-9F", ((char)0x009F).ToString()),
        ("TAB", "\t"),
        ("BOM", ((char)0xFEFF).ToString()),
        ("lone high", ((char)0xD800).ToString()),
        ("lone low", ((char)0xDC00).ToString()),
        ("reversed pair", new string([(char)0xDC00, (char)0xD800])),
        ("high then ascii", new string([(char)0xDBFF, 'a'])),
        ("valid pair", new string([(char)0xD83D, (char)0xDE00])),
        ("unassigned", char.ConvertFromUtf32(0x378) + char.ConvertFromUtf32(0xE0080)),
        ("private use", char.ConvertFromUtf32(0xE000) + char.ConvertFromUtf32(0xF0000) + char.ConvertFromUtf32(0x10FFFD)),
        ("noncharacters", new string([(char)0xFFFE, (char)0xFFFF])),
        ("mix", new string([(char)0xA, (char)0xD, (char)0x2028, (char)0x2029, (char)0x85, (char)0xB, (char)0xC, (char)0, (char)1, (char)0x9F, (char)0xD800, (char)0xDC00, (char)0xFEFF])),
        ("empty", string.Empty),
    ];

    /// <summary>The strings put into each entry point: each special alone, between two words, doubled, and at the start and the end of a word.</summary>
    internal static IEnumerable<(string Name, string Text)> Cases()
    {
        foreach ((string name, string text) in Specials)
        {
            yield return (name, text);
            yield return (name + " between words", "KPI" + text + "label");
            yield return (name + " doubled", text + text);
            yield return (name + " around", text + "x" + text);
        }
    }

    /// <summary>One way of putting a string into the public API and saving the document (or measuring it).</summary>
    internal sealed record EntryPoint(string Name, Action<string> Run);

    /// <summary>A tall page with room for a header or a footer of any number of lines (a header that is too tall is a documented error, and not what these tests are about).</summary>
    internal static PdfDocumentOptions Tall(Action<DocumentTestKit.OptionsBuilder>? change = null) =>
        DocumentTestKit.Options(b =>
        {
            b.PageSize = new PageSize(Length.Pt(595), Length.Pt(4000));
            b.Margins = Margins.Pt(1500, 40, 1500, 40);
            change?.Invoke(b);
        });

    private static void Save(PdfDocument document)
    {
        using MemoryStream stream = new();
        document.Save(stream);
    }

    private static PdfDocument With(Block block, Action<DocumentTestKit.OptionsBuilder>? change = null)
    {
        PdfDocument document = new(Tall(change));
        document.Add(block);
        return document;
    }

    private static readonly TableColumn[] TwoColumns = [new TableColumn(), new TableColumn()];

    /// <summary>The text entry points of the public API, each given the string as the one text it takes.</summary>
    internal static IReadOnlyList<EntryPoint> EntryPoints { get; } =
    [
        new("Paragraph", s => Save(With(new Paragraph(s)))),
        new("TextRun", s => Save(With(new Paragraph([new TextRun("a"), new TextRun(s, new TextStyle { Weight = 700 }), new TextRun("b")])))),
        new("Heading", s => Save(With(new Heading(1, s)))),
        new("TableCell body", s => Save(With(new Table(TwoColumns, [new TableRow([new TableCell(s), new TableCell("x")])])))),
        new("TableRow(string[]) body", s => Save(With(new Table(TwoColumns, [new TableRow(s, "x")])))),
        new("TableRow(string[]) header", s => Save(With(new Table(TwoColumns, new TableRow(s, s), [new TableRow("a", "b")])))),
        new("TableCell header paragraphs", s => Save(With(new Table(TwoColumns, new TableRow([new TableCell([new Paragraph(s), new Paragraph(s)]), new TableCell("h")]), [new TableRow("a", "b")])))),
        new("Columns", s => Save(With(new Columns([[new Paragraph(s)], [new Paragraph(s), new Paragraph("x")]])))),
        new("Callout", s => Save(With(new Callout(new Paragraph(s))))),
        new("ItemList strings", s => Save(With(new ItemList([s, "b", s])))),
        new("ItemList ListItem", s => Save(With(new ItemList([new ListItem(s), new ListItem(new Paragraph(s))])))),
        new("ItemList marker style", s => Save(With(new ItemList(["a", "b"]) { Marker = ListMarker.Decimal, MarkerStyle = new TextStyle { Family = s } }))),
        new("Header", s => Save(With(new Paragraph("body"), b => b.Header = _ => new Paragraph(s)))),
        new("Footer", s => Save(With(new Paragraph("body"), b => b.Footer = page => new Paragraph(s + page.Number)))),
        new("DrawText", s => Save(With(new DrawingBlock(Length.Pt(200), Length.Pt(100), canvas => canvas.DrawText(s, 10, 20))))),
        new("DrawText aligned", s => Save(With(new DrawingBlock(Length.Pt(200), Length.Pt(100), canvas => canvas.DrawText(s, 100, 20, null, TextAlignment.Center, TextDirection.RightToLeft))))),
        new("Drawing AltText", s => Save(With(new DrawingBlock(Length.Pt(20), Length.Pt(20), _ => { }) { AltText = s }))),
        new("TextMeasurer", s => _ = new TextMeasurer(DocumentTestKit.Fonts).Measure(s)),
        new("TextMeasurer width", s => _ = new TextMeasurer(DocumentTestKit.Fonts).Measure(s, null, Length.Pt(40))),
        new("Title", s => Save(With(new Paragraph("x"), b => b.Title = s))),
        new("Subject", s => Save(With(new Paragraph("x"), b => b.Subject = s))),
        new("Author", s => Save(With(new Paragraph("x"), b => b.Author = s))),
        new("Creator", s => Save(With(new Paragraph("x"), b => b.Creator = s))),
        new("Language", s => Save(With(new Paragraph("x"), b => b.Language = s))),
        new("TextStyle.Family", s => Save(With(new Paragraph([new TextRun("x", new TextStyle { Family = s })])))),
        new("TextStyle.Language", s => Save(With(new Paragraph([new TextRun("x", new TextStyle { Language = s })])))),
        new("Document Language and text", s => Save(With(new Paragraph(s), b => b.Language = s))),
    ];
}
