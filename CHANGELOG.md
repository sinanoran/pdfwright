# Changelog

All notable changes to this project are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

Version 0.1.0 is complete in this repository and is not released: nothing is published to nuget.org (see `RELEASING.md`). Everything below is what 0.1.0 will contain.

### Added

- **A document layer for reports.** `PdfDocument` is made from `PdfDocumentOptions` (fonts, default style, page size, margins, metadata, direction, header and footer, limits) and saved with `Save` or `SaveAsync`.
  Blocks flow down the page and break onto new pages: `Heading` (levels 1 to 6), `Paragraph` (styled runs, logical alignment, line spacing, keep-together), `Spacer`, `PageBreak`, `Table`, `ItemList`, `Columns`, `Callout`, `Drawing` and `Image`.
  Values are `Length` (points, millimetres, centimetres, inches; no implicit conversion from a number), `PageSize`, `Margins` and `Color`, and are checked where they are set.
- **Tables** with fixed, fractional and automatic column widths (measured from the header and the first 100 rows), cell padding, borders, header, zebra, row and cell fills, logical alignment, right-to-left column order, a header repeated
  on every page, and rows that are never split unless taller than a page. Rows are an `IEnumerable<TableRow>` enumerated once for each save and never held whole; `Table.MaxRows` cuts a table and reports it.
- **Lists**: `ItemList` with bullet, decimal, letter and Arabic-Indic markers, a start number, an indent and a marker gap, item spacing, a hanging indent (wrapped lines align with the text), one level of nested lists, and the marker on the right in a right-to-left item. Items split between pages by their lines with the marker kept with the first line (a paragraph with `KeepTogether` is kept whole); a list may be in a callout, a column cell, a header or a footer. A bullet falls back to U+25CF, U+2219, U+00B7 or a hyphen-minus when no font has U+2022, and a marker larger than the text makes the first line of its item taller.
- **Columns and callouts**: one to four equal cells for a KPI row, and a box with a fill, border, padding and corner radius that is split between pages or kept together.
- **A drawing canvas** for charts as vectors: paths (lines, rectangles, rounded rectangles, ellipses, arcs, curves), fills with either rule, strokes with dashes, caps and joins, clipping, transforms, opacity, text through the
  document's fonts, and images, in points with the origin at the top left. **`PdfImage.FromBytes`** reads JPEG (passed through) and PNG (decoded, with transparency).
- **Headers and footers** built for each page from a `PageContext` (number, count, first and last page), drawn inside the margins after the body is laid out, so "page x of y" needs no second pass.
- **`TextMeasurer`**, which measures text with the engine that draws it, for placing labels.
- **Fonts supplied, never discovered**: `FontCollection` (thread-safe, `IDisposable`) takes TrueType bytes, reads family, weight and style from the font, refuses fonts that forbid embedding with `FontNotEmbeddableException`,
  and chooses a font for each grapheme cluster by family, weight, style and coverage. Only the glyphs used are embedded, as subsets.
- **Right-to-left and mixed-direction text** by the Unicode bidirectional algorithm, with Arabic, Hebrew, Devanagari, Bengali, Tamil and Thai shaping through HarfBuzz and Latin, Greek and Cyrillic. Text is written to the file in logical
  order, so it can be searched, copied and read by a screen reader. Measured by reading the files back: 3120 of 3120 strings of the page corpus through PdfPig and through Firefox 157.0's pdf.js (21 November 2026), and 61 of 61 mixed-direction lines through PdfPig and through pdf.js 6.3.335 (21 November 2026, compared in NFD with white space, U+034F and the bidirectional controls removed);
  xpdf's position-ordered `pdftotext` reads 45 of 61, a floor the tests hold (`docs/known-limitations.md`).
- **Deterministic output** (`Deterministic`, `Creation`, `Compress`): the same document is the same bytes, and with compression off the same bytes on every .NET version. Metadata: title, subject, author, creator, language.
- **Diagnostics and limits**: `PdfDocument.Diagnostics` lists the characters no font has (with language and page), the families that were not found, the tables that were cut, the page count and the byte count;
  `DocumentLimits` (`MaxPages`, `MaxBytes`, `MaxTableRows`) stop a runaway document with a `PdfLimitExceededException`, and nothing is written to the stream when one is hit.
- **Documentation and samples**: a user guide (`docs/guide.md`), the known limitations, the baselines of the alternatives that were measured, and a runnable sample report (`samples/Pdfwright.Samples.Report`). Every code block of the
  README and the guide is compiled and run by the tests, which fail when a block and its tested code differ.
- **A package** for `net8.0`, `net9.0`, `net10.0` and `net11.0` that depends only on `HarfBuzzSharp` and its native assets, ships no font, and carries the README, the LICENSE and the NOTICE.

### Not in this version

CJK, emoji and colour fonts, forms, encryption, PDF/A, tagged (accessible) PDF, links on text, hyphenation, justification, table cell spans, widow and orphan control, synthetic bold and italic, and any use of system fonts.
Each is named, with the reason, in `docs/known-limitations.md`.

### Internal, not public API

The PDF writer (objects, cross-reference stream, content streams, Flate, Info and XMP metadata, document id), the font engine (a validating TrueType reader, the embedding licence rules, a glyph-closure subsetter, Type 0 fonts with
`ToUnicode`), and the text engine (Unicode 18.0.0 tables and their generator, the bidirectional algorithm UAX #9, line breaking UAX #14, grapheme clusters UAX #29, script itemization, shaping and font fallback, paragraph layout).
The text engine passes every row of `BidiTest.txt` (770,241), `BidiCharacterTest.txt` (91,707), `LineBreakTest.txt` (19,346) and `GraphemeBreakTest.txt` (853).

### Tests and tooling

- The public surface (with nullability) is recorded in `tests/Pdfwright.Tests/PublicApi/Pdfwright.PublicApi.txt`, and a test fails when it changes without that file changing.
- Golden files for each script group and the README report, compared by SHA-256, with `tools/Pdfwright.GoldenDiff` to see what differs; visual golden files, every page drawn by PDFium and compared with a stored picture;
  seeded property and fuzz tests; failure cases for every script; `qpdf --check` on every generated file in CI; `tools/pdfjs-check` for Firefox's pdf.js; the benchmark and process harness (`benchmarks/`).
- CI on Linux, Windows and macOS, and a release workflow that publishes with Trusted Publishing and no stored key.

[Unreleased]: https://github.com/sinanoran/pdfwright/commits/main
