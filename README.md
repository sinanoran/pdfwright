# Pdfwright

[![CI](https://github.com/sinanoran/pdfwright/actions/workflows/ci.yml/badge.svg)](https://github.com/sinanoran/pdfwright/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

**Pdfwright writes PDF reports: paragraphs, tables that paginate, vector charts, in every script.**

> **Status: pre-release, nothing is published.** The writer, the text engine, the drawing canvas and (since slice S5a)
> the first public API are in the repository: lengths, page sizes, fonts, text styles, headings, paragraphs with styled runs, spacers
> and page breaks, tables (since S5b), `PdfDocument`, the text measurer and the diagnostics. Columns, callouts, drawings, keep-together and
> headers and footers are the next slices (see [`docs/plan-0.1.md`](docs/plan-0.1.md)); the package id `Pdfwright` is not on nuget.org.

## Why it will exist

A PDF of a report is the one export a person reads on paper or forwards unchanged. Generating it with a
headless browser works and costs a browser: about 290 MB on disk, half a gigabyte of memory and two
seconds for a 20-page table ([`docs/baselines.md`](docs/baselines.md)). The small native PDF backends are
fast and light, but the one measured here writes Arabic and Hebrew as presentation forms in visual order,
so the text cannot be searched, copied or read by a screen reader, and cannot be tagged for accessibility.

Pdfwright will be the third way: our own layout engine and PDF writer over HarfBuzz, MIT-licensed, managed
code with one small native library, fonts supplied by the caller and never looked up in the operating
system, output that reads back in logical order. None of it exists yet; the plan describes it.

## What version 0.1 will be

Headings, paragraphs, callouts, columns, tables with pagination and a repeated header, a drawing canvas for vector charts,
headers and footers with page numbers, embedded font subsets, right-to-left and mixed-direction text,
Arabic, Indic and Thai shaping. **Not in 0.1:** CJK, emoji and colour fonts, forms, encryption, PDF/A,
tagged PDF (the design carries it; it is 0.2). The whole design, the public API sketch, the targets and
the test strategy are in [`docs/plan-0.1.md`](docs/plan-0.1.md).

## Usage (what exists today)

```csharp
using Pdfwright;

// Fonts are supplied, never discovered. A collection is parsed once, can be shared by every document, and is meant to live long (it is IDisposable: it holds native copies of the fonts).
FontCollection fonts = new FontCollection()
    .Add(File.ReadAllBytes("NotoSans-Regular.ttf"))
    .Add(File.ReadAllBytes("NotoSans-Bold.ttf"))
    .Add(File.ReadAllBytes("NotoSansArabic-Regular.ttf"));

PdfDocument document = new(new PdfDocumentOptions
{
    Fonts = fonts,
    DefaultStyle = new TextStyle { Family = "Noto Sans", Size = Length.Pt(10), Language = "en" },
    PageSize = PageSize.A4,
    Margins = Margins.Mm(top: 20, right: 15, bottom: 20, left: 15),
    Title = "Monthly report",
    Header = page => page.IsFirst ? null : new Paragraph("Monthly report") { Style = new TextStyle { Color = Color.MidGray } },
    Footer = page => new Paragraph($"{page.Number} / {page.Count}") { Alignment = TextAlignment.End }, // drawn inside the margins, after the body: the count is always known
    Deterministic = true, // no clock: the same document is the same bytes
});

document.Add(new Heading(1, "Monthly report"));
document.Add(new Paragraph(
[
    new TextRun("Sales rose "),
    new TextRun("12 %", new TextStyle { Weight = 700, Color = Color.Green }),
    new TextRun(" over the quarter."),
]));
document.Add(new Paragraph("تقرير المبيعات الشهري") { Style = new TextStyle { Language = "ar" } });

// A table: the rows are an IEnumerable, enumerated once when the document is saved and never held whole.
// The header repeats on every page; a row is not split between pages unless it is taller than one.
TableColumn[] columns =
[
    new(ColumnWidth.Fraction(1)),                                         // shares what is left
    new(ColumnWidth.Fixed(Length.Mm(30))) { Alignment = TextAlignment.End },
    new(),                                                                // auto: measured from the header and the first 100 rows
];
document.Add(new Table(columns, new TableRow("Customer", "Amount", "Status"), invoices.Select(i => new TableRow(i.Name, i.Amount, i.Status)))
{
    Borders = Borders.Horizontal,
    HeaderFill = Color.Gray(0.9),
    ZebraFill = Color.Gray(0.97),
    // Direction = TextDirection.RightToLeft puts the first column at the right edge.
});

// A KPI row: Columns of 1 to 4 equal cells, here on a filled Callout (a note; it is split between pages when it must be, or KeepTogether = true).
document.Add(new Callout(new Columns(
[
    new Paragraph([new TextRun("Revenue\n"), new TextRun("1.2 M", new TextStyle { Size = Length.Pt(16), Weight = 700 })]),
    new Paragraph([new TextRun("Orders\n"), new TextRun("8,430", new TextStyle { Size = Length.Pt(16), Weight = 700 })]),
    new Paragraph([new TextRun("Returns\n"), new TextRun("2.1 %", new TextStyle { Size = Length.Pt(16), Weight = 700, Color = Color.Red })]),
])) { CornerRadius = Length.Pt(4), KeepTogether = true });

// A drawing: a box and a callback that draws on a canvas in points (origin top left, y down). It runs when the document is saved.
double[] sales = [4, 7, 5, 9, 6];
document.Add(new Drawing(Length.Mm(120), Length.Mm(50), canvas =>
{
    double step = canvas.Width / sales.Length;
    for (int i = 0; i < sales.Length; i++)
    {
        double height = sales[i] / 10 * (canvas.Height - 14);
        canvas.Fill(new DrawingPathBuilder().Rectangle((i * step) + 6, canvas.Height - 14 - height, step - 12, height).Build(), Color.FromHex("#3366CC"));
        canvas.DrawText($"Q{i + 1}", (i * step) + (step / 2), canvas.Height - 2, alignment: TextAlignment.Center);
    }
}) { AltText = "Bar chart of sales by quarter" });

await document.SaveAsync(stream, cancellationToken);
DocumentDiagnostics report = document.Diagnostics; // missing glyphs by language and page, page count, bytes
```

A drawing, a picture (`PdfImage.FromBytes(...)`, JPEG or PNG; `new Image(picture) { Width = Length.Mm(40) }` places one in the flow) and the header and footer can all
hold a logo and a date: a header is any block built per page, for example `Columns` of an `Image` and a `Paragraph`.

`TextMeasurer` measures text with the engine that draws it, for placing labels. `Length` has no implicit
conversion from a number, so a unit is never guessed.

## Build and test

```bash
dotnet build Pdfwright.slnx
dotnet test Pdfwright.slnx
```

Targets `net8.0`, `net9.0`, `net10.0` and `net11.0`. The tests run on all four. The build uses the .NET 11
SDK (`global.json`).

## Measuring

`benchmarks/Pdfwright.Benchmarks` is the harness. It measures a fresh process (wall time and peak
working set, median of N runs) and holds the data of the standard scenarios, and
`benchmarks/Pdfwright.Benchmarks/Baselines/` has the Playwright script the comparison table is made
with. See [`docs/baselines.md`](docs/baselines.md) for the numbers and how to reproduce them.

## Licence

MIT, see [`LICENSE`](LICENSE). The fonts of the test corpus are SIL OFL 1.1, listed with their licences in
[`NOTICE`](NOTICE) and `tests/Pdfwright.Tests/Fonts/`; the package ships no font.
