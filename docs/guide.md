# Pdfwright user guide

This is the guide to the whole public surface of version 0.1, in the order a report is built. The [README](../README.md) has a first example and the reasons the library exists;
[known-limitations.md](known-limitations.md) lists what it does not do and why. The full public surface (every type and member) is recorded in
`tests/Pdfwright.Tests/PublicApi/Pdfwright.PublicApi.txt`, and every public member has XML documentation that your editor shows.

Every C# code block in this guide is compiled and run by the test suite (`tests/Pdfwright.Tests/Documentation`), and the suite fails when a block and the code that was run differ,
so what you copy is what was tested. A block is a piece of a program, not a whole one: unless it makes its own, the snippets assume

- `fonts`, a `FontCollection` holding the fonts of the examples (Noto Sans in regular, bold and italic, and Noto Sans Arabic);
- `fontDirectory`, the folder those `.ttf` files are in, and `logoBytes`, the bytes of a JPEG, PNG or WebP file;
- `invoices`, a sequence of your own records with a name, an amount and a status;
- `stream`, a writable `Stream`, and `cancellationToken`, a `CancellationToken`.

Two type names are also names in `System.Drawing`: `Drawing` and `Image`. Where both namespaces are imported, alias ours (`using Image = Pdfwright.Image;`).

## Contents

1. [How a document is made](#how-a-document-is-made)
2. [Fonts](#fonts)
3. [Text and styles](#text-and-styles)
4. [Headings, paragraphs and page breaks](#headings-paragraphs-and-page-breaks)
5. [Tables](#tables)
6. [Tables of many rows](#tables-of-many-rows)
7. [Columns and callouts](#columns-and-callouts)
8. [Lists](#lists)
9. [Drawing: charts and figures](#drawing-charts-and-figures)
10. [Measuring text](#measuring-text)
11. [Images](#images)
12. [Headers and footers](#headers-and-footers)
13. [Right-to-left and mixed-direction text](#right-to-left-and-mixed-direction-text)
14. [Metadata and deterministic output](#metadata-and-deterministic-output)
15. [Diagnostics and limits](#diagnostics-and-limits)
16. [Threads and lifetimes](#threads-and-lifetimes)
17. [Startup time](#startup-time)

## How a document is made

A `PdfDocument` is made from `PdfDocumentOptions`, given blocks with `Add`, and saved. The blocks flow from the top of the first page down and break onto new pages as they fill:
`Heading`, `Paragraph`, `Spacer`, `PageBreak`, `Table`, `ItemList`, `Columns`, `Callout`, `Drawing` and `Image`. There is no page object to manage and no coordinate to compute.

Lengths are `Length` values (`Length.Pt`, `Mm`, `Cm`, `In`), and there is no implicit conversion from a number, so a unit is never guessed. Page sizes are `PageSize.A4`, `A5`, `Letter` and `Legal`
(and `Landscape()` turns one on its side), margins are `Margins` (20 mm at the top and bottom and 15 mm at the sides by default), and colours are `Color` values (`Color.FromHex("#3366CC")`,
`Color.Gray(0.9)`, `Color.FromRgb(...)`, and named ones such as `Color.Red`). The one place that uses plain `double`s is the drawing canvas, which is in points because chart code is arithmetic.

The examples in this guide are fragments, and some names in them come from the code around them: `document` is a `PdfDocument` made as in the [quick start](../README.md#quick-start), `fonts` is the `FontCollection` of
the [Fonts](#fonts) section, `fontDirectory` is the folder that holds your `.ttf` files, `cancellationToken` is the `CancellationToken` that your method received (`CancellationToken.None` in a console program),
`stream` is the stream you save to, and `invoices` is a sequence of your own records, each with a name, an amount and a status.

<!-- snippet: guide-save -->
```csharp
// The whole file is made in memory and then copied, so a document that fails writes nothing. The stream is not closed.
using MemoryStream stream = new();
await document.SaveAsync(stream, cancellationToken);
byte[] pdf = stream.ToArray();   // e.g. return Results.File(pdf, "application/pdf", "report.pdf") from an ASP.NET Core endpoint
```

`Save` and `SaveAsync` build the whole file in memory and write it to the stream only when it is complete, so a document that fails, or hits one of its limits, writes nothing. The stream
need not be seekable and is not closed. `SaveAsync` does the layout on a thread-pool thread and copies the bytes with the stream's asynchronous methods; the token is checked between blocks and pages.
The memory a save holds is about the size of the file, plus the page being built. A document can be saved more than once.

## Fonts

Fonts are **supplied, never discovered.** Pdfwright does not look in the operating system's font folders, so the same code produces the same file on a developer's machine, a Linux container
and a build server. The package ships no font. Give the collection TrueType files (`glyf` outlines; Noto, Roboto, Open Sans, Arial and most `.ttf` files are) as bytes:

<!-- snippet: guide-fonts -->
```csharp
// One collection for the whole application: it parses each font once and keeps a native copy of it.
FontCollection fonts = new();
fonts.Add(File.ReadAllBytes(Path.Combine(fontDirectory, "NotoSans-Regular.ttf")));
fonts.Add(File.ReadAllBytes(Path.Combine(fontDirectory, "NotoSans-Bold.ttf")));
fonts.Add(File.ReadAllBytes(Path.Combine(fontDirectory, "NotoSansArabic-Regular.ttf")));

string families = string.Join(", ", fonts.Families); // Noto Sans, Noto Sans Arabic

// fontBytes stands for the bytes of one more font file, here one that may be refused.
try
{
    fonts.Add(fontBytes);
}
catch (FontNotEmbeddableException e)
{
    // The font's licence forbids embedding it: e.Family names it and e.EmbeddingPermission is its OS/2 fsType.
    Console.Error.WriteLine($"{e.Family} may not be embedded (fsType {e.EmbeddingPermission}).");
}
catch (ArgumentException)
{
    // Not a TrueType font. OpenType fonts with CFF outlines (most .otf files) are refused as well.
}

fonts.Dispose(); // when the application is done with it, not after each document
```

- A collection parses each font once when it is added, reads its family, weight and style from the font itself, and is safe to share between any number of documents and threads. It is meant to
  live as long as the application: it is `IDisposable` because it holds a native copy of every font for the shaping engine (HarfBuzz).
- A font whose licence forbids embedding (OS/2 `fsType` restricted, no subsetting, or bitmap only) is refused with `FontNotEmbeddableException`, and nothing is added. Only the glyphs a document
  uses are embedded, as a subset.
- OpenType fonts with CFF outlines (most `.otf` files) are refused with an `ArgumentException`; CFF support is planned after 0.1.
- CJK text is not tested in 0.1. Most CJK fonts have CFF outlines and are refused; a CJK font with TrueType outlines is accepted, and its text is shaped, embedded and broken
  between characters by the Unicode rules, but no CJK text is in the test corpus, and there is no vertical text.

**Choosing a font.** A style names a `Family` (compared without regard to case), a `Weight` (400 regular, 700 bold) and `Italic`. The font whose weight is nearest is used, and there is **no synthetic
bold or italic**: a family without a bold font draws bold text in its nearest weight, and one without an italic draws upright. Supply the fonts of the styles you use.

**Fallback.** The font is chosen for each grapheme cluster, not for each word: a cluster takes the first font, in the order of the style's family first and then every other font of the collection, that
covers all of it. So a Latin family and an Arabic font in one collection can share a paragraph, and the Arabic is drawn in the Arabic font, in the style's weight where that font has it. A character
no font has is drawn as the `.notdef` glyph of the font chosen for it (usually an empty box) and **reported**, never an exception: see [Diagnostics and limits](#diagnostics-and-limits). A family named by a
style that the collection does not have is replaced by the first font and listed in `DocumentDiagnostics.MissingFamilies`.

A mark that follows a character of another script (a Hebrew point after a Latin digit) is shaped with the font of its base character and may be a missing glyph; put such text in a font that has both.

## Text and styles

A `TextStyle` has six properties, all optional: `Family`, `Size`, `Weight`, `Italic`, `Color` and `Language`. An unset property is taken from the style around it, so the styles are laid over each other:

1. the library's defaults (the first font of the collection, 10 points, weight 400, upright, black);
2. `PdfDocumentOptions.DefaultStyle`;
3. inside a table, `Table.Style`, then `Table.HeaderStyle` in the header, then the `TableCell.Style`;
4. the `Paragraph.Style` (for a heading, the style for its level);
5. the `TextRun.Style` of a run.

A later layer overrides only the properties it sets. `Language` is a BCP 47 tag (`tr`, `ar`, `hi`) that selects language-specific forms when shaping (the Turkish dotted and dotless i, for example);
text whose style sets none takes the document's `Language`.

<!-- snippet: guide-styles -->
```csharp
PdfDocument document = new(new PdfDocumentOptions
{
    Fonts = fonts,
    // The base of every style. What it leaves unset is the library's: the first font, 10 points, regular, upright, black.
    DefaultStyle = new TextStyle { Family = "Noto Sans", Size = Length.Pt(10), Language = "en" },
});

document.Add(new Paragraph(
[
    new TextRun("Totals are "),
    new TextRun("final", new TextStyle { Weight = 700 }),   // bold, in the paragraph's size and colour
    new TextRun(", amounts in "),
    new TextRun("euros", new TextStyle { Italic = true, Color = Color.MidGray }),
    new TextRun("."),
])
{
    // Under the runs' own styles, over the document's: this paragraph is 11 points.
    Style = new TextStyle { Size = Length.Pt(11) },
});
```

A colour does not change how text is shaped: a word of which one letter is red has the same shape and width, and Arabic letters keep joining across a change of colour.
Every text a caller gives (a run, a table cell, a header or footer, a list item, a drawn or a measured string) is read by one rule, so that no string makes a save throw:

- A line break of any kind (`\n`, `\r\n`, `\r`, U+0085, U+000B, U+000C, U+001C to U+001E, U+2028, U+2029) is a hard line break inside the same paragraph. A break at the very end of a text adds no empty line, a `\r\n` is one break (not two), and an empty line (two breaks in a row, or one at the start) is as tall as a line of text. It is never a new paragraph: not even U+2029 PARAGRAPH SEPARATOR starts one. Make several `Paragraph`s to get spacing and a direction for each. This holds in a heading, a table cell (a header cell too), a list item, a header or footer and a string drawn on a canvas as much as in a paragraph; no place takes a single line only.
- The other control characters (U+0000 to U+001F except the line breaks above, U+007F, U+0080 to U+009F) are removed from the layout and from the text layer. A tab is set as a space.
- A surrogate without its pair (a lone high or low surrogate, a pair in the wrong order) is replaced by U+FFFD, which is drawn with the font's replacement glyph (or its `.notdef` when it has none, and then reported in `DocumentDiagnostics.MissingGlyphs`).
- Everything else is kept, unassigned and private-use code points included: a font without a glyph for it draws `.notdef` and the character is reported as missing.
- The strings you gave are never changed: the rule is applied when the text is laid out, so a `Paragraph` or a `TextRun` still holds exactly what you wrote.
- Metadata (title, subject, author, creator, language) is read by the same rule except that a line break or a tab becomes a space, and white space at either end is trimmed; an entry that is empty after that is left out of the file. The `Family` and `Language` of a `TextStyle` are read the same way before they are used or reported (so `"en-US\n"` is `"en-US"` in `MissingGlyph.Language` and in the language the shaper gets, and a family of only control characters counts as not given).

## Headings, paragraphs and page breaks

<!-- snippet: guide-flow -->
```csharp
PdfDocument document = new(new PdfDocumentOptions
{
    Fonts = fonts,
    DefaultStyle = new TextStyle { Family = "Noto Sans", Size = Length.Pt(10) },
    // Six styles for levels 1 to 6; what one leaves unset comes from the default style. Without this, headings are bold at 2.0, 1.6, 1.3, 1.15, 1.0 and 1.0 times its size.
    HeadingStyles =
    [
        new TextStyle { Size = Length.Pt(24), Weight = 700, Color = Color.FromHex("#1F3A5F") },
        new TextStyle { Size = Length.Pt(16), Weight = 700, Color = Color.FromHex("#1F3A5F") },
        new TextStyle { Size = Length.Pt(13), Weight = 700 },
        new TextStyle { Size = Length.Pt(11), Weight = 700 },
        new TextStyle { Size = Length.Pt(10), Weight = 700 },
        new TextStyle { Size = Length.Pt(10), Weight = 700, Italic = true },
    ],
});

document.Add(new Heading(1, "Quarterly review"));
document.Add(new Heading(2, "Summary") { SpaceBefore = Length.Pt(18) });
document.Add(new Paragraph("Revenue grew in every region. The paragraph breaks into lines at the width of the page.")
{
    Alignment = TextAlignment.Start,   // Start, Center or End: logical, so Start is the right edge in a right-to-left paragraph
    LineSpacing = 1.2,                 // a multiplier of the natural line height
    SpaceAfter = Length.Pt(8),         // the default is 0.6 of the font size
});
document.Add(new Spacer(Length.Mm(5)));
document.Add(new Paragraph("A short block that is moved whole to the next page rather than split.") { KeepTogether = true });
document.Add(new PageBreak());
document.Add(new Heading(2, "Details"));
```

- **Spacing.** A heading has the space above it (by default the heading's font size) and below it (0.4 of it); a paragraph has none above and 0.6 of its font size below. A space at the edge of a page is dropped,
  not carried over; a `Spacer` is always kept.
- **Keeping things together.** A heading is kept with the first line of the next block (the whole of a drawing, an image or a row of columns), and with the whole of a `KeepTogether` paragraph or callout that follows it when that fits a page. It is not kept with a table: a heading directly above a table can be left at the bottom of a page, with the table starting on the next. `Paragraph.KeepTogether` and `Callout.KeepTogether` move
  a block that does not fit the rest of the page to the next one instead of splitting it. A block taller than a page is split whatever it asks. There is no widow or orphan control.
- **Page breaks.** `PageBreak` ends the page; two in a row make a blank page.
- **Line breaking** follows the Unicode rules (UAX #14). A word wider than its box is cut between characters, with no hyphen, and there is no hyphenation and no dictionary breaking for Thai, Lao, Khmer or Burmese: a run of such text without spaces is one word to the line breaker, so it breaks at spaces only, and a run wider than the line is cut between grapheme clusters, mid-word.

## Tables

A `Table` has columns, optional header rows and the body rows. Columns are `TableColumn`s with a width and an alignment:

- `ColumnWidth.Fixed(length)`: a given width;
- `ColumnWidth.Fraction(weight)`: a share of what the fixed and automatic columns leave, in proportion to the weight;
- `ColumnWidth.Auto` (the default): measured from the content of the header and the first 100 rows. A cell further down that is wider wraps; nothing is re-measured, because the rows are streamed.
  Give a column a fixed width or a fraction when the later rows differ from the first.

Alignment is `Start`, `Center` or `End` and is **logical**, so a numeric column set to `End` is at the right edge of a left-to-right table and at the left edge of a right-to-left one.

<!-- snippet: guide-table -->
```csharp
using System.Globalization;

TableColumn[] columns =
[
    new(ColumnWidth.Fraction(2)),                                              // shares what the others leave, in proportion to its weight
    new(ColumnWidth.Fixed(Length.Mm(25))) { Alignment = TextAlignment.End },   // a given width; numbers sit at the end edge
    new(),                                                                     // auto: measured from the header and the first 100 rows
];

TableRow header = new("Customer", "Amount", "Status");
IEnumerable<TableRow> rows = invoices.Select(i => new TableRow(i.Name, i.Amount.ToString("N2", CultureInfo.InvariantCulture), i.Status));

document.Add(new Table(columns, header, rows)
{
    Borders = Borders.Horizontal,
    HeaderFill = Color.Gray(0.9),
    ZebraFill = Color.Gray(0.97),
    CellPadding = Margins.Pt(top: 3, right: 5, bottom: 3, left: 5),
    HeaderStyle = new TextStyle { Weight = 700 },
    Style = new TextStyle { Size = Length.Pt(9) },
});

// A row can be made of cells with a style, a fill, an alignment or several paragraphs of their own.
document.Add(new Table(
    columns,
    [
        new TableRow(
            new TableCell("Total") { Style = new TextStyle { Weight = 700 } },
            new TableCell("1,234.50") { Alignment = TextAlignment.End, Fill = Color.FromHex("#FFF3CD") },
            new TableCell([new Paragraph("Paid"), new Paragraph("by card") { Style = new TextStyle { Color = Color.MidGray } }])),
    ]));
```

- The header row is repeated at the top of every page the table continues on (`RepeatHeader`, on by default; a header taller than half a page is not repeated), and is bold by default.
- A row is never split between pages unless it is taller than a page; then it is split between the lines of its cells. A row that does not fit the rest of a page moves whole to the next one.
- `Borders` are `None` (the default), `All`, `Horizontal`, `Outer` or your own lines made of a `BorderLine`, set for the outer and inner edges separately. Each page's part of a table has its own top and bottom line.
- `HeaderFill`, `ZebraFill` (every second body row) and a `Fill` on a row or a cell are painted in that order, the cell's on top.
- A cell's padding is physical (`Margins`), 3 points above and below and 5 at the sides by default; a cell may set its own.
- A row with fewer cells than the table has columns leaves the rest empty. There are no cell spans, no vertical alignment and no table inside a table or a cell.

## Tables of many rows

The rows of a `Table` are an `IEnumerable<TableRow>` that is **enumerated once for each save and never held whole**, so a table costs the pages it makes and not the rows it came from. A lazy sequence
(a `Select` over a query, a `yield return` method) is the intended input. Two consequences:

- a sequence that can be enumerated only once (a reader, a one-shot iterator) works for one save only, and `IAsyncEnumerable` is not accepted: read your rows into an enumerable first, or build it to be repeatable;
- a table does not know its length. `Table.MaxRows` reads one row more than it draws to know that there are more.

The example below uses the `columns` and `header` of the table example above, and your own `invoices`:

<!-- snippet: guide-table-streaming -->
```csharp
using System.Diagnostics;
using System.Globalization;

PdfDocument document = new(new PdfDocumentOptions { Fonts = fonts });

// A lazy sequence is the intended input: it is enumerated once for each save and never held whole, so a table of a hundred
// thousand rows costs the pages it makes and not the rows it came from.
IEnumerable<TableRow> rows = invoices.Select(i => new TableRow(i.Name, i.Amount.ToString("N2", CultureInfo.InvariantCulture), i.Status));

// MaxRows draws the first 100 rows and leaves the rest out; the document says so in its diagnostics.
document.Add(new Table(columns, header, rows) { MaxRows = 100 });

await document.SaveAsync(stream, cancellationToken);

foreach (TruncatedTable cut in document.Diagnostics.TruncatedTables)
{
    Trace.WriteLine($"The table at block {cut.BlockIndex} was cut after {cut.MaxRows} rows.");
}
```

`Table.MaxRows` cuts one table and lists it in the diagnostics; `DocumentLimits.MaxTableRows` stops the save with an exception when any table goes past it (see [Diagnostics and limits](#diagnostics-and-limits)).
Use the first when a partial table is acceptable (a "200 of 5,300 rows" note is yours to add) and the second as a guard against a runaway query. When both are set, whichever a table reaches first applies.

## Columns and callouts

`Columns` is a row of one to four equal-width cells, each holding one block or several: a KPI row, a logo beside a title, two paragraphs side by side. A row of columns is never split between pages. `Callout`
is a box around one or more blocks with a fill, a border, a padding and a corner radius (a radius larger than half the shorter side makes a pill). A callout in the flow is split between pages when it is too
tall, with a box on each page, unless `KeepTogether` is set.

<!-- snippet: guide-columns-callout -->
```csharp
// A KPI row: one to four equal cells, here inside a filled box that is kept on one page.
document.Add(new Callout(new Columns(
[
    new Paragraph([new TextRun("Revenue\n"), new TextRun("1.2 M", new TextStyle { Size = Length.Pt(16), Weight = 700 })]),
    new Paragraph([new TextRun("Orders\n"), new TextRun("8,430", new TextStyle { Size = Length.Pt(16), Weight = 700 })]),
    new Paragraph([new TextRun("Returns\n"), new TextRun("2.1%", new TextStyle { Size = Length.Pt(16), Weight = 700, Color = Color.Red })]),
]))
{
    Fill = Color.FromHex("#E8F0FE"),
    Border = new BorderLine(Length.Pt(0.75), Color.FromHex("#3366CC")),
    CornerRadius = Length.Pt(4),
    Padding = Margins.Pt(top: 8, right: 10, bottom: 8, left: 10),
    KeepTogether = true,
});

// A cell may hold several blocks, and a gap can be set; a callout in the flow holds any blocks and is split between pages when it is too tall.
document.Add(new Columns([[new Heading(3, "Notes"), new Paragraph("Left cell")], [new Paragraph("Right cell")]]) { Gap = Length.Mm(8) });
document.Add(new Callout([new Heading(3, "Note"), new Paragraph("A note is a few blocks in a box.")]) { Fill = Color.FromHex("#FFF3CD") });
```

Columns are one level deep: no columns inside columns (also through a callout), and no table or page break in a cell or a callout. A callout inside a column cell or another callout is not split, and one taller than a
page fails the save with an `InvalidOperationException`. `Direction` on `Columns` and `Callout` sets the order of cells and the direction of the paragraphs inside.

## Lists

`ItemList` is a list of items, each after a marker. It is not called `List`, which would clash with `System.Collections.Generic.List<T>` in nearly every file. An item is a `ListItem`: a paragraph (the marker stands before it), and, if you want,
more paragraphs under it and nested lists. A list of plain texts takes the strings directly. The markers are `ListMarker.Bullet` (the default), `Decimal`, `LowerAlpha`, `UpperAlpha` and `ArabicIndicDecimal`, and `Start` is the number of the first item.

<!-- snippet: guide-lists -->
```csharp
// A bulleted list of texts, and a numbered one that starts at 3.
document.Add(new ItemList(["Revenue grew in every region.", "Returns fell to 2.1%."]));
document.Add(new ItemList(["Check the totals", "Send the report", "File the copy"]) { Marker = ListMarker.Decimal, Start = 3 });

// An item may hold more paragraphs and a list of its own, one level deep.
document.Add(new ItemList(
[
    new ListItem([new Paragraph("Regions"), new ItemList(["North", "South"]) { Marker = ListMarker.LowerAlpha }]),
    new ListItem("Products"),
])
{
    Indent = Length.Pt(14),                 // from the start edge to the marker
    MarkerGap = Length.Pt(6),               // from the widest marker to the text
    ItemSpacing = Length.Pt(3),             // between two items
    MarkerStyle = new TextStyle { Weight = 700 },
});

// In a right-to-left list the marker is at the right edge and the text hangs to its left.
document.Add(new ItemList(["البند الأول", "البند الثاني"]) { Direction = TextDirection.RightToLeft, Marker = ListMarker.ArabicIndicDecimal });
```

- **Hanging indent.** The lines of an item that wrap are aligned with the text of its first line, not with the marker. The text of every item starts at the same place, whatever the width of its marker (`9.` and `10.`).
- **Direction.** The marker is on the start edge of its item: the right one when the item's paragraph is right to left. The direction is the paragraph's own `Direction`, else the list's, else the enclosing `Columns` or `Callout`'s, else the document's;
  with `TextDirection.Auto` each item's text decides. A marker is set in the direction of its item, so in a right-to-left item `1.` shows the full stop to the left of the digit, where readers of Arabic and Hebrew expect it.
  `ArabicIndicDecimal` writes the digits U+0660 to U+0669; the font has to have them, and a marker that no font can draw is reported in `DocumentDiagnostics.MissingGlyphs` with its page like any other character.
- **Pages.** A list flows like paragraphs: items are split between pages by their lines, and a marker is drawn with the first line of its item, so it never stands alone at the bottom of a page. A heading before a list stays with its first line.
- **Bullet.** The bullet is U+2022. A font may lack it (Noto Sans Arabic and Noto Sans Hebrew do): when no font of the collection has it for the item's style, the first of U+25CF, U+2219, U+00B7 and the hyphen-minus that a font has takes its place, so a collection of only such fonts still draws a bullet (both Noto Sans Arabic and Noto Sans Hebrew get the hyphen-minus).
- **Where.** A list may be in the flow, in a `Callout`, in a cell of `Columns`, and as a header or a footer (or inside columns or a callout there). Nesting is one level deep: a nested list may not hold a list, and trying to makes an `ArgumentException`.
- **Spacing.** `ItemSpacing` is the space below each paragraph of an item (0.3 of the font size by default); `SpaceBefore` and `SpaceAfter` are the space around the whole list (0.6 of the list's font size below, by default: the `Style` over the document's). A paragraph's own `SpaceBefore` on the first paragraph and `SpaceAfter` on the last count too: the larger of the two is used.
  `Style` is the style of the paragraphs and the markers, and `MarkerStyle` is laid over it for the markers only. A `MarkerStyle` size larger than the text makes the first line of the item taller, so the marker never reaches into the line above.
  A paragraph of an item with `KeepTogether` is kept whole, with its marker, and moves to the next page when it does not fit.

## Drawing: charts and figures

Pdfwright has a drawing canvas and a text measurer, not a chart library: you draw the bars, lines, areas and gauges of your own charts from your data and your colours, and they are vectors in the file.
A `Drawing` is a box of a given size and a callback that draws on a `DrawingCanvas`. The callback runs when the document is saved, and the box is a clip.

All numbers on the canvas are `double`s in **points**, the origin is the **top left** of the box, and **y points down**.

<!-- snippet: guide-drawing -->
```csharp
using System.Globalization;

double[] values = [4, 7, 5, 9, 6];
string[] labels = ["Jun", "Jul", "Aug", "Sep", "Oct"];
TextStyle small = new() { Size = Length.Pt(8) };
TextMeasurer measurer = new(fonts, new TextStyle { Family = "Noto Sans" });

document.Add(new Drawing(Length.Mm(120), Length.Mm(50), canvas =>
{
    // Points, origin at the top left, y pointing down. Nothing is drawn outside the box.
    double baseline = canvas.Height - 14;
    double step = canvas.Width / values.Length;

    canvas.Stroke(new DrawingPathBuilder().MoveTo(0, baseline).LineTo(canvas.Width, baseline).Build(), Color.MidGray, 0.5);

    for (int i = 0; i < values.Length; i++)
    {
        double height = values[i] / 10 * (baseline - 14);
        double x = (i * step) + 6;
        canvas.Fill(new DrawingPathBuilder().Rectangle(x, baseline - height, step - 12, height).Build(), Color.FromHex("#3366CC"));

        // Place the value above the bar, centred, using the width the engine will draw it with.
        string value = values[i].ToString(CultureInfo.InvariantCulture);
        double width = measurer.Measure(value, small).Width.Points;
        if (width < step - 12)
        {
            canvas.DrawText(value, x + ((step - 12) / 2), baseline - height - 3, small, TextAlignment.Center);
        }

        canvas.DrawText(labels[i], x + ((step - 12) / 2), canvas.Height - 3, small, TextAlignment.Center);
    }
})
{ AltText = "Bar chart of sales by month" });
```

The canvas can fill and stroke paths (lines, rectangles, rounded rectangles, ellipses, arcs, quadratic and cubic curves, closed or not), with even-odd or non-zero fill, opacity, and pens with dashes, caps and joins.
It can clip to a path and transform the axes (`Translate`, `Scale`, `Rotate`, `Transform`); `Save` and `Restore` bracket a change of state, and a `Save` left open is closed when the callback returns.

<!-- snippet: guide-canvas -->
```csharp
document.Add(new Drawing(Length.Mm(60), Length.Mm(30), canvas =>
{
    DrawingPath disc = new DrawingPathBuilder().Ellipse(40, 40, 30, 30).Build();
    canvas.Fill(disc, Color.FromHex("#3366CC"), opacity: 0.5);

    canvas.Save();                                   // the transform and the clip are undone by Restore
    canvas.Clip(new DrawingPathBuilder().Rectangle(0, 0, canvas.Width, canvas.Height).Build());
    canvas.Translate(120, 40);
    canvas.Rotate(20);                               // degrees, clockwise on the canvas
    DrawingPen dashed = new(Color.Red, width: 2, dashes: [5, 3]);
    canvas.Stroke(new DrawingPathBuilder().RoundedRectangle(-20, -10, 40, 20, 4, 4).Build(), dashed);
    canvas.Restore();

    // A pie slice: a path of an arc and two lines.
    DrawingPath slice = new DrawingPathBuilder().MoveTo(40, 40).LineTo(70, 40).Arc(40, 40, 30, 30, 0, 90).Close().Build();
    canvas.Fill(slice, Color.Green, rule: FillRule.NonZero);
}));
```

- `DrawText` sets text through the document's fonts with the same engine as paragraphs (so Arabic and Hindi are shaped, and missing glyphs are reported). The point is the start of the first baseline, and
  `alignment` says which edge of the text sits at the point. It does not wrap; break lines yourself, using the measurer. A `Drawing` sets text in the direction of the `Columns` or `Callout` it is in.
- A drawing is never split or scaled: one taller than a page, or wider than the room it is in, fails the save with an `InvalidOperationException`. Give it the size of its place.
- Write numbers on a scale near 1: operands are written with five decimals, so data in units whose fractions are below 0.00001 loses them. Scale your data into points before drawing.
- `AltText` is kept for tagged output, which is not in 0.1, and is not written to the file yet.

## Measuring text

`TextMeasurer` measures text with the very engine that draws it (shaping, fallback, bidi, line breaking), so a measured size and a drawn size cannot disagree. It tells you the width a label will take, how many
lines a caption needs at a given width, and how tall they are. It is safe to use from several threads.

<!-- snippet: guide-measuring -->
```csharp
// Make it the way the document is made: the same fonts, default style, language and line spacing.
TextMeasurer measurer = new(fonts, new TextStyle { Family = "Noto Sans", Size = Length.Pt(9) }, language: "en");

TextMeasurement one = measurer.Measure("Revenue by quarter");
double widthInPoints = one.Width.Points;

// With a width the text wraps like a paragraph of that width: how many lines, and how tall.
TextMeasurement wrapped = measurer.Measure("A caption that is longer than the room the figure leaves for it.", maxWidth: Length.Mm(40));
int lines = wrapped.LineCount;
Length height = wrapped.Height;
```

A measurement matches a document only if it is made the way the document is: the same fonts, default style, language, direction (the `direction` argument) and line spacing. A paragraph in a document is measured
at the content width of its place, so give `Measure` the width you mean.

## Images

`PdfImage.FromBytes` reads a JPEG, a PNG or a WebP, and only those: GIF, BMP, TIFF, HEIC and SVG are refused with an `ArgumentException`, so convert
such a picture to PNG (or to JPEG for a photograph) before you hand it over. A JPEG is read when it is baseline, extended or progressive with 8-bit
samples (grey, RGB, YCbCr or CMYK); arithmetic-coded, lossless, hierarchical and 12-bit JPEGs are refused. A PNG is read in every colour type and bit
depth, interlaced or not, with its transparency (`tRNS` or an alpha channel); 16-bit samples are reduced to 8 bits. A WebP is read lossy (VP8), lossless (VP8L) and in the extended format (VP8X) with an alpha plane in any of the ways the format has (uncompressed or lossless coded, with each of its four filters); the picture comes out as 8-bit RGB with a separate alpha, as a PNG does. An **animated WebP is refused** with an `ArgumentException` that says so (its first frame is not shown). A JPEG is passed through into the file undecoded; a PNG and a WebP are decoded once by a managed decoder (no native image library), and their transparency is kept. The picture is held decoded for the life
of the object and written once to each document that uses it, so make it once and share it. The colour management of a PNG (`iCCP`, `gAMA`, `sRGB`, `cHRM`) is ignored, and so are the orientation and colour profile of a JPEG and the `ICCP`, `EXIF` and `XMP ` chunks of a WebP; the colours of a lossy WebP are those of libwebp's formulas; an image is limited to 65,535 pixels on a side and, by default, 40 million pixels in all (a PNG or a WebP with transparency is decoded to 4 bytes a pixel, so about 160 MB at that size, and a lossless WebP needs about twice that while it is being decoded), refused with an exception before memory is allocated.
The pixel limit applies to a JPEG as well, which is not decoded here but is decoded by whoever opens the file.

<!-- snippet: guide-images -->
```csharp
PdfImage logo = PdfImage.FromBytes(logoBytes);   // JPEG, PNG or WebP; make it once and use it in any number of documents

// The block keeps the picture's proportions: give a width, a height, or neither (the picture's natural size, 96 pixels to the inch).
document.Add(new Image(logo) { Width = Length.Mm(30), Alignment = TextAlignment.End, AltText = "Company logo" });

// The canvas stretches the picture to the rectangle it is given.
document.Add(new Drawing(Length.Mm(40), Length.Mm(20), canvas => canvas.DrawImage(logo, 0, 0, canvas.Width, canvas.Height, opacity: 0.8)));
```

The `Image` block keeps the picture's proportions (set a width or a height, not both; setting both stretches it) and is scaled down to fit the width when it is wider than its place. It can be placed in the flow, in a cell, in a callout,
in a header, and drawn on a canvas.

A picture of more pixels than the limit is refused with an `ArgumentException` whose message gives its size, the limit and the option that changes it. `PdfImageOptions.MaxPixels` raises the limit for pictures you trust, up to 100,000,000 (about 400 MB decoded for a PNG
with transparency), or lowers it to be stricter about pictures from users; a value below 1 or above 100,000,000 is refused where it is set.

<!-- snippet: guide-images-limit -->
```csharp
// The limit is on width times height, for every format (JPEG, PNG and WebP); the default is 40 million, and the most that can be set is 100 million.
PdfImage photo = PdfImage.FromBytes(photoBytes, new PdfImageOptions { MaxPixels = 60_000_000 });
```

## Headers and footers

`Header` and `Footer` are functions from a `PageContext` (the number of the page, the page count, whether it is the first or the last, and the page size) to a block, or to `null` for a page with none. They are called
once for each page, in order, **after the whole body has been laid out**, so `PageContext.Count` is the final count and "page x of y" costs nothing: a table of any length is still enumerated once. The block is laid out
at the content width and drawn **inside the margin**, so the body's area does not change; it may be a paragraph, a heading, a list, columns (a logo on one side and a date on the other), a callout, a drawing, an image or a spacer, but
not a table or a page break. A block taller than the room the margin leaves (the margin minus the offset) fails the save with an `InvalidOperationException` that names the page and says whether it was the header or the footer.
The callback should be a pure function of its argument.

<!-- snippet: guide-header-footer -->
```csharp
PdfImage logo = PdfImage.FromBytes(logoBytes);

PdfDocument document = new(new PdfDocumentOptions
{
    Fonts = fonts,
    Margins = Margins.Mm(top: 25, right: 15, bottom: 20, left: 15),
    HeaderOffset = Length.Mm(8),   // from the top edge of the page to the top of the header (by default, half the margin)
    FooterOffset = Length.Mm(8),   // from the bottom edge of the page to the bottom of the footer
    // Called once for each page, after the whole body is laid out, so Count is final. Return null for a page with no header.
    Header = page => page.IsFirst
        ? null
        : new Columns([new Image(logo) { Width = Length.Pt(24) }, new Paragraph("Monthly report") { Alignment = TextAlignment.End, Style = new TextStyle { Color = Color.MidGray } }]),
    Footer = page => new Paragraph($"Page {page.Number} of {page.Count}") { Alignment = TextAlignment.End },
});

document.Add(new Heading(1, "Monthly report"));
for (int i = 0; i < 90; i++)
{
    document.Add(new Paragraph($"Line {i} of the body, which runs over several pages."));
}
```

**Page numbers in a right-to-left document.** In a right-to-left document a footer such as `$"{page.Number} / {page.Count}"` is a right-to-left paragraph, so the two numbers are ordered by the bidirectional
algorithm: page 1 of 3 shows as "3 / 1". Say what you mean in one of three ways. Make the whole footer left to right:

<!-- snippet: guide-rtl-footer -->
```csharp
PdfDocument document = new(new PdfDocumentOptions
{
    Fonts = fonts,
    DefaultStyle = new TextStyle { Family = "Noto Sans Arabic", Language = "ar" },
    Direction = TextDirection.RightToLeft,
    // The footer is a right-to-left paragraph here, so "1 / 3" would be read in bidi order and shown as "3 / 1".
    // Make the whole footer left to right:
    Footer = page => new Paragraph($"{page.Number} / {page.Count}") { Direction = TextDirection.LeftToRight, Alignment = TextAlignment.Center },
});
```

Or keep the footer a right-to-left line and put the numbers inside a left-to-right isolate, written `⁦` and `⁩` in C# (U+2066 and U+2069):

<!-- snippet: guide-rtl-footer-isolate -->
```csharp
PdfDocument document = new(new PdfDocumentOptions
{
    Fonts = fonts,
    DefaultStyle = new TextStyle { Family = "Noto Sans Arabic", Language = "ar" },
    Direction = TextDirection.RightToLeft,
    // "Page" and the numbers: the numbers sit inside a left-to-right isolate (U+2066 ... U+2069), so "1 / 3" keeps its order, and the footer stays a right-to-left line.
    Footer = page => new Paragraph($"صفحة ⁦{page.Number} / {page.Count}⁩"),
});
```

Or write it the way Arabic does, with a word between the numbers: `صفحة 1 من 3` ("page 1 of 3"). Each number is then a run of its own between two Arabic words, so the bidirectional algorithm
keeps the numbers in the order an Arabic reader expects, from right to left, and no isolate is needed. A bare "1 / 3" is laid out the same way, each number in its own
place from right to left, but readers take "1 / 3" as one left-to-right expression, so there it looks swapped:

<!-- snippet: guide-rtl-footer-of -->
```csharp
PdfDocument document = new(new PdfDocumentOptions
{
    Fonts = fonts,
    DefaultStyle = new TextStyle { Family = "Noto Sans Arabic", Language = "ar" },
    Direction = TextDirection.RightToLeft,
    // "Page 1 of 3" in Arabic: the word "من" (of) separates the numbers, so each keeps its own place and the line reads from right to left.
    Footer = page => new Paragraph($"صفحة {page.Number} من {page.Count}"),
});
```

## Right-to-left and mixed-direction text

Direction comes from `PdfDocumentOptions.Direction` (`Auto`, the default, `LeftToRight` or `RightToLeft`), overridden by the `Direction` of a paragraph, heading, table, columns or callout. With `Auto`, each
paragraph takes its direction from its first strong character. Inside a paragraph, the runs are ordered by the Unicode bidirectional algorithm (UAX #9), including paired brackets and isolates.

The text is written to the file **in logical order** (the order you typed it), with each glyph in the position the bidirectional algorithm gives it. That is what makes the text of the file searchable, copyable and readable
by a screen reader, and it is what separates Pdfwright from writers that draw presentation forms in visual order.

<!-- snippet: guide-rtl -->
```csharp
PdfDocument document = new(new PdfDocumentOptions
{
    Fonts = fonts,
    DefaultStyle = new TextStyle { Family = "Noto Sans Arabic", Size = Length.Pt(11), Language = "ar" },
    Language = "ar",
    Direction = TextDirection.RightToLeft,   // the default is Auto: each paragraph takes its direction from its first strong character
});

document.Add(new Heading(1, "تقرير المبيعات الشهري"));

// English inside Arabic: the Unicode bidirectional algorithm orders the runs; the text stays in logical order in the file.
document.Add(new Paragraph("بلغت المبيعات 1,200 وحدة في Q3 وفق تقرير Finance."));

// A reference code reads left to right, as one line.
document.Add(new Paragraph("INV-2026-0042") { Direction = TextDirection.LeftToRight });

// The first column is at the right edge; Start alignment is the right edge, End the left.
document.Add(new Table(
    [new TableColumn(ColumnWidth.Fraction(1)), new TableColumn(ColumnWidth.Fixed(Length.Mm(30))) { Alignment = TextAlignment.End }],
    new TableRow("العميل", "المبلغ"),
    [new TableRow("شركة الأفق", "1,200.00"), new TableRow("مؤسسة النور", "845.50")])
{
    Borders = Borders.Horizontal,
});
```

- Alignment, table columns, column cells and the position of an `Image` are logical: in a right-to-left context `Start` is the right edge and the first column or cell is at the right edge. Padding, margins and borders are physical.
- Arabic is shaped by HarfBuzz (joining, ligatures, marks); Hebrew points, Devanagari, Bengali and Tamil conjuncts and reordering vowel signs, and Thai stacked marks are positioned by the font's own tables. Supply a font for each
  script you use (the Noto fonts used by the tests cover all of them).
- Copying text out of a PDF made by Pdfwright can carry an invisible U+034F (a combining grapheme joiner) after some letters of Arabic, Indic and Thai text, and a reader that places glyphs by position (`pdftotext`'s default mode)
  can show spaces inside words in some of those scripts. The file is correct and readers that follow the content stream get the text exactly; see [known-limitations.md](known-limitations.md) for what was measured and why.

## Metadata and deterministic output

<!-- snippet: guide-metadata -->
```csharp
PdfDocumentOptions options = new()
{
    Fonts = fonts,
    Title = "Monthly report",
    Subject = "Sales, October 2026",
    Author = "Finance",                 // written only when set; nothing is added that the caller did not give
    Creator = "Reporting service",
    Language = "en",                    // a screen reader chooses its voice from this
    Deterministic = true,               // the clock is never read: the same document is the same bytes
    Creation = new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.Zero),   // optional; a deterministic file has no date without it
    Compress = false,                   // Flate depends on the runtime's zlib; off, the bytes are the same on every .NET version
};

PdfDocument document = new(options);
document.Add(new Paragraph("Hello"));
```

- Metadata is the title, subject, author, creator and language. Nothing is written that was not set, and the library records itself as the producer. A line break or a tab in them is written as a space, other control characters are left out, and a surrogate without its pair is written as U+FFFD, so that the Info dictionary and the XMP packet always agree and are well formed.
- With `Deterministic = true` the clock is never read, so the same document is the same bytes. The file's `/ID` is a hash of its content either way. Without `Deterministic` and without `Creation`, the date is the time of the save.
- `Compress` (on by default) is Flate. The compressed bytes depend on the zlib of the .NET runtime, so a compressed file is the same bytes on the same runtime but may differ between runtimes. For golden files that must match everywhere,
  switch it off.
- Every number in the file is written with the invariant culture, so a German or Turkish machine writes the same file as an American one.

## Diagnostics and limits

A document is made for a report, so a character no font has is **data to inspect, not an exception**. After a save, `PdfDocument.Diagnostics` says what it found:

| Property | What it holds |
|---|---|
| `MissingGlyphs` | Each character no font had, once for each language and page it occurred on, at most 1,000 |
| `MissingFamilies` | The families a style asked for that the collection does not have |
| `TruncatedTables` | The tables that went past their `MaxRows` |
| `PageCount`, `ByteCount` | The size of what was written |

`PdfDocumentOptions.Limits` stops a runaway document with a `PdfLimitExceededException` whose `Limit` says which limit and `Maximum` its value: `MaxPages`, `MaxBytes` (checked after every page and once more when the file is complete,
so a file over the limit is never returned) and `MaxTableRows`. **Nothing is written to the stream when a limit is hit.**

<!-- snippet: guide-diagnostics-limits -->
```csharp
using System.Diagnostics;

PdfDocument document = new(new PdfDocumentOptions
{
    Fonts = fonts,
    Limits = new DocumentLimits { MaxPages = 50, MaxBytes = 5_000_000, MaxTableRows = 5_000 },
});

document.Add(new Paragraph("Hello 你好"));   // no font of the collection has these two characters

try
{
    await document.SaveAsync(stream, cancellationToken);
}
catch (PdfLimitExceededException e)
{
    // Nothing was written to the stream. e.Limit says which limit and e.Maximum its value.
    Trace.WriteLine($"{e.Limit} of {e.Maximum} was exceeded.");
    throw;
}

// What the save found: data to inspect, never an exception.
DocumentDiagnostics report = document.Diagnostics;
foreach (MissingGlyph glyph in report.MissingGlyphs)
{
    Trace.WriteLine($"{glyph.Character} (U+{glyph.CodePoint:X4}) is in no font; language {glyph.Language ?? "unset"}, page {glyph.Page}.");
}

Trace.WriteLine($"{report.PageCount} page(s), {report.ByteCount} bytes, missing families: {string.Join(", ", report.MissingFamilies)}");
```

Other failures are exceptions where a value is wrong: a property is checked where it is set and the exception names it (`ArgumentOutOfRangeException` for a size or weight out of range, `ArgumentException` for a negative margin),
and a layout that cannot be made (a drawing wider than its place, a header taller than its margin, a callout whose padding leaves no room) fails the save with an `InvalidOperationException` that says what and where. The font
collection throws `ObjectDisposedException` after it is disposed.

## Threads and lifetimes

- `FontCollection` and `TextMeasurer` are safe to share between threads and documents; a `PdfImage` is made once and is meant to be shared by documents.
- A `PdfDocument` is not thread-safe: do not use one instance from several threads at once (adding while a save runs, or two saves at once, throws `InvalidOperationException`). Make a document for each report.
- Do not dispose a `FontCollection` while a document made with it is being saved.
- The callbacks (`Drawing`'s, `Header` and `Footer`) run during the save, on the thread that saves, so they should not capture something that changes meanwhile.

## Startup time

The first document a process saves pays for compiling and loading the code it runs (the JIT): in a fresh process most of the time of a first report is that, not the work. A service that saves many reports pays it once; a command-line tool or a function that starts a process per report pays it every time. Publishing the application with ReadyToRun compiles it ahead of time, Pdfwright and the other libraries included, so nothing in the library has to change:

<!-- snippet: none -->
```xml
<PropertyGroup>
  <PublishReadyToRun>true</PublishReadyToRun>
</PropertyGroup>
```

The code is compiled for one platform, so a publish needs a runtime identifier: `-r linux-x64` (or `win-x64`, `linux-musl-x64`, `osx-arm64` ...), the platform the application runs on, or `<RuntimeIdentifier>` in the project when it is only ever built for one. For one publish, without changing the project:

<!-- snippet: none -->
```bash
dotnet publish -c Release -r linux-x64 --self-contained false -p:PublishReadyToRun=true
```

Measured with the benchmark program of the repository (`benchmarks/Pdfwright.Benchmarks`, .NET 10, Windows x64, 30 fresh processes per set, two sets; [baselines.md](baselines.md) has the method and the caveats of that machine), from the start of the process to its exit:

| A fresh process that saves | Default (JIT) | ReadyToRun |
|---|---|---|
| a table of 1,000 rows (21 pages) | 607 and 549 ms | 378 and 370 ms |
| a ten-page report | 432 and 393 ms | 264 and 251 ms |
| nothing (the runtime starting) | 113 and 109 ms | 111 and 107 ms |

ReadyToRun about doubles the size of the published DLLs (the benchmark's grew from 27 MB to 56 MB), and with tiered compilation (the default) the code it compiled ahead of time is recompiled by the JIT when it is hot, so a long-running service keeps its steady-state speed. Turning tiered PGO off for the process (`DOTNET_TieredPGO=0`) took about a tenth more off the first save with ReadyToRun and a fifth without, in the same measurement (the table: 343 and 331 ms with ReadyToRun, 479 and 461 ms without), at the cost of the profile-guided optimisation that a long-running process gains from; measure it on your own workload before you use it. What is left of the first save, the runtime starting and the work itself, is a matter of the application (keeping a process alive, for one), not of the library; native AOT has not been tried with Pdfwright and its native shaping library.
