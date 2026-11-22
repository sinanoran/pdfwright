# Pdfwright

[![CI](https://github.com/sinanoran/pdfwright/actions/workflows/ci.yml/badge.svg)](https://github.com/sinanoran/pdfwright/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/Pdfwright.svg)](https://www.nuget.org/packages/Pdfwright)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

**Pdfwright writes PDF reports: headings, paragraphs, tables that paginate and vector charts, in every script, with text that reads back in logical order.**

It is a layout engine and a PDF writer in managed C#, with one small native library for text shaping ([HarfBuzz](https://github.com/harfbuzz/harfbuzz), through
[HarfBuzzSharp](https://github.com/mono/SkiaSharp)). Pdfwright is under the MIT licence, which covers commercial use; there is no paid licence tier.

<!-- snippet: none -->
```bash
dotnet add package Pdfwright
```

> **Status: version 0.1.0 (22 November 2026) is the first release.** It is a first release: the text of every script is read back and checked, but the review of the test text was done by an AI agent rather than by native speakers, the sample files were checked
> as PDFium renders and not in Acrobat Reader or macOS Preview, CJK fonts with TrueType outlines are accepted but not tested (CFF outlines come in 0.2), and the speed and memory targets of the plan are not met (see [docs/baselines.md](docs/baselines.md)).
> The list is in [docs/known-limitations.md](docs/known-limitations.md) and the checks that were and were not made are in [RELEASING.md](RELEASING.md). Version 0.1.0 is installed with the command above.

Targets `net8.0`, `net9.0`, `net10.0` and `net11.0`. Dependencies: `HarfBuzzSharp` and its native assets (Windows, Linux with glibc or musl, macOS). Besides the Windows, Linux and macOS runners, CI makes the sample report inside the slim
.NET runtime images `mcr.microsoft.com/dotnet/aspnet:10.0` (Debian, glibc) and `mcr.microsoft.com/dotnet/aspnet:10.0-alpine` (musl). The package ships no font.

## Why it exists

A PDF of a report is the one export a person reads on paper or forwards unchanged. The usual ways of making one each cost something:

- A **headless browser** works and costs a browser: on the machine this was measured on, a 20-page table took 2 seconds and about half a gigabyte, and the browser itself is 288 MB on disk.
- A **small native PDF backend** is fast and light, but the one measured here (Skia's) writes Arabic and Hebrew as presentation forms in visual order, so the text cannot be searched, copied or read by a screen reader.
- A **commercial library** costs a licence, which is the thing the sibling package [Sheetwright](https://github.com/sinanoran/sheetwright) exists to avoid for spreadsheets.

Pdfwright is the other way: its own layout engine over HarfBuzz and its own writer, fonts supplied by the caller and never looked up in the operating system, and output that reads back in logical order. The decision, with its
evidence, is in [`docs/plan-0.1.md`](docs/plan-0.1.md).

## What it is, and what it is not

**In version 0.1:**

- Blocks: headings (levels 1 to 6), paragraphs with styled runs, spacers, page breaks, callouts (a box around blocks), columns (one to four equal cells: a KPI row), images, and keep-together.
- **Tables** with fixed, fractional and automatic column widths, cell padding, borders and fills, a header repeated on every page, rows that are never split unless taller than a page, rows streamed from an `IEnumerable`.
- A **drawing canvas** for charts as vectors (paths, fills, strokes, dashes, clipping, transforms, opacity, text, images) and a **text measurer** that uses the engine that draws.
- **Headers and footers** built for each page from a `PageContext` (page number, page count), so "page x of y" needs no second pass.
- Embedded **font subsets** (TrueType), chosen by family, weight and style with fallback by coverage.
- **Right-to-left and mixed-direction text** (the Unicode bidirectional algorithm), Arabic, Hebrew, Devanagari, Bengali, Tamil and Thai shaping, and Latin, Greek and Cyrillic.
- Metadata, deterministic output (the same document is the same bytes), limits on pages, bytes and table rows, and diagnostics for missing glyphs.

**Not in 0.1:** tested CJK (most CJK fonts have CFF outlines, which are refused; a CJK font with TrueType outlines is accepted but no CJK text is in the test corpus), vertical text, emoji and colour fonts, forms, encryption and signatures, PDF/A and PDF/UA, **tagged (accessible) PDF** (the design carries it; it is planned for 0.2), links on text,
hyphenation and justified text, table cell spans, floating images, pictures other than JPEG and PNG (no WebP, GIF, TIFF or SVG), gradients, and any use of system fonts. It is a report writer, not a PDF editor: it does not read, merge or modify existing PDF files.

## Quick start

A report with a heading, styled text, a paragraph in Arabic, a table and a page number in the footer (see the note on installing above):

<!-- snippet: readme-quickstart -->
```csharp
using Pdfwright;
using System.Globalization;

string fontDirectory = "fonts"; // the folder that holds the .ttf files
using FileStream output = File.Create("report.pdf");

// Fonts are supplied by the caller, never looked up in the operating system. A collection is made once and shared.
using FontCollection fonts = new FontCollection()
    .Add(await File.ReadAllBytesAsync(Path.Combine(fontDirectory, "NotoSans-Regular.ttf"), cancellationToken))
    .Add(await File.ReadAllBytesAsync(Path.Combine(fontDirectory, "NotoSans-Bold.ttf"), cancellationToken))
    .Add(await File.ReadAllBytesAsync(Path.Combine(fontDirectory, "NotoSansArabic-Regular.ttf"), cancellationToken));

PdfDocument document = new(new PdfDocumentOptions
{
    Fonts = fonts,
    DefaultStyle = new TextStyle { Family = "Noto Sans", Size = Length.Pt(10) },
    Title = "Monthly report",
    Language = "en",
    Footer = page => new Paragraph($"{page.Number} / {page.Count}") { Alignment = TextAlignment.End },
});

document.Add(new Heading(1, "Monthly report"));
document.Add(new Paragraph(
[
    new TextRun("Sales rose "),
    new TextRun("12 %", new TextStyle { Weight = 700, Color = Color.Green }),
    new TextRun(" over the quarter."),
]));
document.Add(new Paragraph("تقرير المبيعات الشهري"));

// The rows are any sequence. They are enumerated once, when the document is saved, and never held whole.
document.Add(new Table(
    [new TableColumn(ColumnWidth.Fraction(1)), new TableColumn(ColumnWidth.Fixed(Length.Mm(30))) { Alignment = TextAlignment.End }, new TableColumn()],
    new TableRow("Customer", "Amount", "Status"),
    invoices.Select(i => new TableRow(i.Name, i.Amount.ToString("N2", CultureInfo.InvariantCulture), i.Status)))
{
    Borders = Borders.Horizontal,
    HeaderFill = Color.Gray(0.9),
});

await document.SaveAsync(output, cancellationToken);
```

`invoices` stands for your own records (a sequence of anything with a name, an amount and a status), and `cancellationToken` is the `CancellationToken` that your method received (`CancellationToken.None` in a console program). The code above is a test in this repository, `DocumentationSnippetTests`, which runs it and reads the file back; a second test fails when the README and the tested code differ, so the
README's code is always code that compiled and ran. The same holds for every block of the [user guide](docs/guide.md).

There is also a runnable sample, a multi-page report with a heading, a row of KPIs in a callout, a paginated table, a bar chart, a header and footer, and Arabic and English text: see [samples](samples/README.md).

## Key concepts

- **Fonts are supplied, never discovered.** A `FontCollection` is made once from TrueType bytes, parsed once, shared by every document and thread, and `IDisposable` (it holds native copies for the shaping engine). A font that forbids
  embedding is refused with `FontNotEmbeddableException`. A character no font has is drawn as the font's empty box and reported in the diagnostics; it is never an exception.
- **Blocks flow.** You add `Heading`, `Paragraph`, `Table`, `ItemList` (bulleted, numbered and nested lists with a hanging indent), `Columns`, `Callout`, `Drawing`, `Image`, `Spacer` and `PageBreak` to a `PdfDocument`; they flow from the top of the first page and break onto new pages as they fill.
  There are no coordinates, except on the drawing canvas.
- **Styles inherit.** A `TextStyle` has `Family`, `Size`, `Weight`, `Italic`, `Color` and `Language`, all optional; an unset property comes from the style around it: library defaults, the document's default style, the table or cell,
  the paragraph, the run.
- **Direction is logical.** Alignment and column order say *start* and *end*, so the same table is right to left when its `Direction` is. Text is written to the file in logical order, with every glyph where the bidirectional algorithm puts it.
- **`Deterministic = true`** reads no clock, so the same document is the same bytes (with `Compress = false` the bytes are the same on every .NET version too).
- **Diagnostics and limits.** `PdfDocument.Diagnostics` reports missing glyphs, missing families, truncated tables, the page count and the byte count. `DocumentLimits` (`MaxPages`, `MaxBytes`, `MaxTableRows`) stop a runaway
  document with a `PdfLimitExceededException`, and nothing is written to the stream when one is hit.
- **`SaveAsync` buffers.** The whole file is made in memory and then copied to the stream, so a failing document writes nothing and a stream that cannot seek works. Plan for memory about the size of the file; the rows of a table
  are never held.

## Scripts, and what was measured

Every script of the test corpus has a font in the test project, and each is tested the same way: a page of text and a long page with a 100-row table that runs over three to five pages, through the public API, then read
back by independent readers and compared with the input string for string. The corpus text is original and ordinary (it is not translated documentation); a native-speaker review of it is part of the release checklist.

| Script group | Strings read back equal to the input, PdfPig (every run, in the test suite) | Firefox's pdf.js (measured by hand, last on 22 November 2026) |
|---|---|---|
| Latin Extended (Turkish, German, Polish, Vietnamese) | 313 of 313 | 313 of 313 |
| Greek | 310 of 310 | 310 of 310 |
| Cyrillic | 311 of 311 | 311 of 311 |
| Arabic | 313 of 313 | 313 of 313 |
| Hebrew | 312 of 312 | 312 of 312 (2 only up to canonical equivalence) |
| Mixed direction | 317 of 317 | 317 of 317 |
| Devanagari | 312 of 312 | 312 of 312 |
| Bengali | 311 of 311 | 311 of 311 |
| Tamil | 311 of 311 | 311 of 311 |
| Thai | 310 of 310 | 310 of 310 |
| **All** | **3120 of 3120** | **3120 of 3120** |

A string passes when it is found whole, in logical order, with its spaces, after one normalisation (the invisible U+034F removed and runs of white space made one space). The mixed-direction corpus also has 61 single-line cases: English in
Arabic, nested quotes, numbers, Bengali reph, and four fonts in one line. PdfPig reads all 61 byte for byte, with the U+034F removed. pdf.js 6.3.335 (in Firefox 157.0) also read all 61, and all 3120 strings of the table, measured again on 22 November 2026 after the corpus text was corrected by a language review. The pdf.js comparison is looser than PdfPig's. Both texts are first put in canonical (NFD) form, and white space, the U+034F and the bidirectional control characters are removed. Every page of the golden documents is also drawn by PDFium and compared with a stored picture, so a mark
in the wrong place or a mirrored run is caught where text extraction would still pass.

**One reader falls below that.** xpdf's `pdftotext` in its default position-ordered mode reads 45 of the 61 single lines exactly: it puts a space where a glyph is drawn to the left of
the end of the glyph before it, which shaped Arabic, Indic and Thai text does by design. Three ways of writing the glyphs were tried and none removes it; 45 is the floor the tests hold. The file is correct, and readers that follow the content
stream get the text exactly. Poppler's `pdftotext` (the library behind Evince and Okular) has a position-ordered mode of its own, which was not measured. Chrome, Edge, Acrobat Reader and macOS Preview are checked by the maintainer before a release, not by a test. Copying text can also carry an invisible U+034F after some letters. Both are in
[known limitations](docs/known-limitations.md).

## Compared with the usual alternatives

Measured on one Windows machine on 21 and 22 November 2026 (the browser and Skia: one run each, on the 21st; Pdfwright: the median of 30 runs, on the 22nd): baselines to beat, not benchmarks (method and how to repeat them: [docs/baselines.md](docs/baselines.md)). The scenario is a table of 1,000 rows and 6 columns over 20 A4
pages with a repeated header. Where a cell says "not measured", nobody measured it here; where it says "documented", it comes from that project's own documentation.

| | Pdfwright 0.1 | Skia's PDF backend (SkiaSharp 4.157.0) | A headless browser (Playwright, Chromium headless shell) |
|---|---|---|---|
| Licence | MIT (HarfBuzz: "old MIT") | SkiaSharp: MIT (read from its package); Skia itself: BSD-3-Clause, with FreeType and ICU parts, not audited here | Playwright: Apache-2.0; Chromium not assessed here |
| Dependency on disk | HarfBuzz: `libHarfBuzzSharp` 2.2 MB (win-x64) and 16.8 MB (Linux, every architecture in one package) | `libSkiaSharp` 13.2 MB and `libHarfBuzzSharp` 2.2 MB (win-x64; 16.8 MB for Linux, every architecture in one package); the Linux Skia package is a 63 MB `.nupkg` for every architecture | the browser: 288 MB (headless shell) or 453 MB (full Chromium) |
| Arabic and Hebrew text read back in logical order | **yes**: 313 of 313 Arabic and 312 of 312 Hebrew strings (PdfPig), same in pdf.js | **no**: Arabic comes back as presentation forms in visual order, Hebrew reversed (read with PdfPig 0.1.16; feeding Skia the shaper's clusters did not change it) | not measured here |
| Mixed-direction paragraph (Latin in Arabic) | itemized and ordered by UAX #9 | the stock shaper does no bidirectional itemization: a mixed line was drawn with the Arabic in isolated forms | not measured here |
| Tagged (accessible) PDF | **not in 0.1** (planned for 0.2) | not possible through SkiaSharp: `SKDocumentPdfMetadata` has no structure tree, language, outlines or page labels | documented: Playwright's `page.pdf` has a `tagged` option (not tested here) |
| 1,000-row table, cold process | 2,095 ms median (p95 2,270 ms) over 30 fresh processes, including about 150 ms of runtime start, 52 MB peak (30 MB over the 22 MB of a bare runtime start), 514 KB, 21 pages: slower and larger than the plan's targets (section 7), on a machine low on free memory (0.6 to 7.5 GB free; the same build gave 1.5 to 2.1 s depending on the memory state, see baselines.md); 8 mm side margins and 3 pt cell padding against the browser's none and 1 px | 279 ms, 31 MB peak (7 MB over the process baseline), 590 KB: a text-only spike, and the font was barely subset | 2,048 ms from browser launch to the end of `page.pdf` (node start and browser close excluded), 495 MB peak over the process tree, 154 KB. The "cold" numbers are taken differently: Pdfwright's is process start to exit, runtime start included; the Skia spike's method is not documented; the layouts differ (margins, padding, fonts) |
| Fonts | supplied by you, embedded as subsets | not assessed here | looked up by the browser on the machine, unless the page loads its own |
| Byte-stable output (golden files) | yes (`Deterministic`, optionally `Compress = false`) | Skia writes a document id and dates; stability not assessed | not assessed here |

Reading it: the browser is the most complete and costs a browser; the Skia backend is the lightest and failed the extraction criterion for the scripts that matter here; Pdfwright's claim is the third row, measured by the tests in this
repository, and it pays for it with what is not in 0.1 (tested CJK, tagged PDF, forms, encryption, PDF/A). The two competitors' rows are spikes made for the plan, not the tools at their best: a team that does not need Arabic, Hebrew or Indic text
extraction, or needs full HTML and CSS, should use the browser or Skia. Pdfwright's own timings, a ten-page report and how to repeat them are in `docs/baselines.md`; they are order-of-magnitude comparisons only (the browser and Skia rows are one run each).

## Known limitations

The full list, with the reason for each, is in [docs/known-limitations.md](docs/known-limitations.md). The ones that matter most when choosing:

- No tagged PDF, so a screen reader reads the text but not a structure; `AltText` on images and drawings is kept for it and is not written yet.
- CJK is not tested or claimed: OpenType fonts with CFF outlines (most CJK fonts, such as the `.otf` Noto Sans CJK) are refused; a CJK font with TrueType outlines is accepted, its text is shaped,
  embedded and broken between characters by the Unicode rules, but no CJK text is in the test corpus. No vertical text, emoji or colour fonts.
- No dictionary line breaking for Thai, Lao, Khmer or Burmese: a run of such text without spaces is one word to the line breaker, so it breaks at spaces only, and a run wider than the line is cut between grapheme clusters, mid-word.
- No links on text, no table cell spans, no nested tables or columns, no hyphenation, no justification, no widow or orphan control, no synthetic bold or italic.
- A table's automatic column widths come from its header and first 100 rows; a table is never held whole, so give a column a width when later rows differ.
- The whole file is built in memory before it is written; copying text from a PDF can carry an invisible U+034F; `pdftotext`'s default mode can put spaces inside words of Arabic, Indic and Thai text.
- Pdfwright is not a PDF editor and does not read PDF files.

## Documentation

- [User guide](docs/guide.md): fonts, styles, headings and paragraphs, tables, columns and callouts, drawing, images, headers and footers, right-to-left text, metadata, diagnostics and limits.
- [Known limitations](docs/known-limitations.md)
- [Samples](samples/README.md)
- [Plan for 0.1](docs/plan-0.1.md): the scope, the public API, the test strategy, the measurements and the decision to write the PDF writer.
- [Plan for 0.2](docs/plan-0.2.md): CJK (a CFF subsetter, a CJK corpus, Japanese line breaking), then tagged PDF.
- [Baselines](docs/baselines.md): the measured alternatives and how to repeat them
- [Changelog](CHANGELOG.md), [Releasing](RELEASING.md), [NOTICE](NOTICE) (licences of what is used or carried)

## Build and test

<!-- snippet: none -->
```bash
dotnet build Pdfwright.slnx
dotnet test Pdfwright.slnx
```

The build uses the .NET 11 SDK (`global.json`) and the tests run on all four target frameworks. The suite opens what it writes: generated files are read back by PdfPig, checked by `qpdf --check` (install qpdf, or set
`PDFWRIGHT_REQUIRE_QPDF=1` on CI to make its absence a failure), compared byte for byte with golden files, and drawn by PDFium and compared with stored pictures. No container, no database, no network.
`PDFWRIGHT_SAMPLES_DIR=<dir>` keeps the sample reports as files to open in a viewer, and `PDFWRIGHT_DUMP=<dir>` keeps the twenty corpus documents.

## Measuring

`benchmarks/Pdfwright.Benchmarks` is the harness: it measures a fresh process (wall time and peak working set, median of N runs) and holds the data of the standard scenarios, and
`benchmarks/Pdfwright.Benchmarks/Baselines/` has the Playwright script the comparison table is made with.

## Contributing

Issues and pull requests are welcome. A change to what is written needs a test that reads the result back; a change to the public surface changes `tests/Pdfwright.Tests/PublicApi/Pdfwright.PublicApi.txt` in the same commit
(a test fails otherwise), and a change to a code block of the documentation changes the snippet it is tested by.

## Releasing

Tag it. Publishing to nuget.org uses Trusted Publishing rather than a stored API key, and the checks only a person can make come first; see [RELEASING.md](RELEASING.md).

## Licence

MIT, see [LICENSE](LICENSE). The fonts of the test corpus are SIL OFL 1.1, listed with their licences in [NOTICE](NOTICE) and `tests/Pdfwright.Tests/Fonts/`; the package ships no font.
