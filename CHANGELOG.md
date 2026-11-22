# Changelog

All notable changes to this project are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- **WebP pictures.** `PdfImage.FromBytes` now reads a WebP, lossy (VP8), lossless (VP8L) or extended (VP8X, with an alpha plane of either method and any of the four filters), as 8-bit RGB with a separate alpha that is written as a soft mask, as for a PNG. The decoder is managed code with no new dependency: a C# port of the WebP decoder of golang.org/x/image v0.48.0 with the YUV to RGB conversion and the upsampling of libwebp v1.6.0, so that the lossy colours are libwebp's formulas (the BSD-3-Clause licences and the patent grants of both projects are in `NOTICE`; the decision and the rejected alternatives are in `docs/decisions/0001-webp-decoder.md`). An **animated WebP is refused** with a message that says so, and the colour profile is ignored. The pixel limit is checked right after each header and before any allocation, the number of Huffman groups (2,600) and the memory of the Huffman tables (64 MB) are capped, a picture must be the size its `VP8X` header gives, and every failure is an `ArgumentException` from `PdfImage.FromBytes`; the three 2026 advisories of the Go decoder (a canvas size of 32 bits, a canvas smaller than the picture, Huffman groups without a limit) each have a regression test. No public type or member changed.

### Changed

- **Writing the content of a page allocates far less; the output is unchanged.** Numbers, operators and glyph codes are now written straight into the content stream's byte buffer instead of through a string for each, and the glyphs of a run are ordered without LINQ. Every byte of every golden file is the same as before, and a test holds the new writer to the old one over random operators and over many doubles.

## [0.1.1] - 2026-11-22

### Fixed

- **A line break or another special character in a string no longer makes a save throw.** `DrawingCanvas.DrawText` threw an `ArgumentException` ("A paragraph separator ... is not the last character") for a string with a line feed, carriage return, U+0085, U+2029 or U+001C to U+001E in it, which is how a multi-line label drawn in a `Drawing` failed. All text is now read by one rule wherever it is laid out (paragraphs, headings, table and header cells, list items, headers and footers, canvas text, `TextMeasurer`): every line break (`\n`, `\r\n`, `\r`, U+0085, U+000B, U+000C, U+001C to U+001E, U+2028, U+2029) is a hard line break inside the same block, never a new paragraph; other control characters (U+0000 to U+001B, U+001F, U+007F, U+0080 to U+009F, except the line breaks above) are removed instead of being drawn as an empty glyph box; a surrogate without its pair is replaced by U+FFFD. The caller's strings are not changed. What changed for U+001C, U+001D and U+001E (the information separators): in 0.1.0 they were treated as paragraph separators (Unicode bidirectional class B) and broke the line inside a paragraph, and in a canvas string, a measured string or a cell they threw or were drawn as a box; now they are line breaks everywhere, so text that worked in 0.1.0 is laid out as before. U+000B (vertical tab) and U+000C (form feed) were not line breaks in 0.1.0 and are now. A break at the very end of a text adds no empty line, and a `\r\n` is one break. In the metadata (title, subject, author, creator, language) a line break or tab becomes a space, controls are left out, a lone surrogate becomes U+FFFD, white space at the ends is trimmed, and an entry that is empty after that (a title of only control characters) is left out of both the Info dictionary and the XMP packet (before, a line break was kept and a lone surrogate dropped). `TextStyle.Family` and `TextStyle.Language` are read by the metadata rule too before they are used or reported (`MissingGlyph.Language`, `DocumentDiagnostics.MissingFamilies`, the language handed to the shaper), so `"en-US\n"` is `"en-US"`; a value that is empty only after that counts as not given. A long run of empty lines (100,000 line feeds) is laid out in linear time (it took minutes). Text without these characters is laid out and written as before. `docs/guide.md` and `docs/known-limitations.md` state the rule.

### Internal

Groundwork for 0.2: an internal shaping seam (`IShaper`) with HarfBuzz as its one implementation, and a binding to the `hb-subset` entry points of the native HarfBuzz library, not used by the writer yet (`docs/plan-0.2.md`, section 5 and "C1 status").

## [0.1.0] - 2026-11-22

The first release of Pdfwright: a PDF writer for reports, with a layout engine, tables that paginate, vector drawing, embedded font subsets and right-to-left, mixed-direction and complex-script text that reads back in logical order. Everything below is the 0.1.0 surface.

### Known limitations of this release

This is a first release, and some of the checks that were planned were replaced by weaker ones (`RELEASING.md` has the record).

- **Language review.** The corpus text, the sample report and the guide were reviewed by an AI agent on 22 November 2026 (corrections applied), not by native speakers. The agent was less confident about Tamil, Bengali, Thai and Marathi (and the Hebrew article point). A native-speaker review stays recommended.
- **Viewers.** The golden pages were checked as PDFium renders (the engine of Chrome and Edge). Adobe Acrobat Reader and macOS Preview were not checked.
- **Performance.** The speed and memory targets of the plan are missed. A table of 1,000 rows (21 pages) takes a cold p50 of 2,095 ms and a warm p50 of 347 ms, allocates 188 MB and has a peak working set 30 MB over the runtime baseline, against targets of 250 ms, 60 ms, 20 MB and 25 MB (`docs/baselines.md`: method, machine and the caveat about free memory). Diagnosis and fixes are a candidate for after 0.1.0.
- **CJK.** A font with TrueType outlines is accepted (mapped, shaped, subset and line-broken), but CJK text is not tested. Fonts with CFF outlines (Noto Sans CJK, Source Han) are refused and come in 0.2 (`docs/plan-0.2.md`).

### Added

- **A document layer for reports.** `PdfDocument` is made from `PdfDocumentOptions` (fonts, default style, page size, margins, metadata, direction, header and footer, limits) and saved with `Save` or `SaveAsync`.
  Blocks flow down the page and break onto new pages: `Heading` (levels 1 to 6), `Paragraph` (styled runs, logical alignment, line spacing, keep-together), `Spacer`, `PageBreak`, `Table`, `ItemList`, `Columns`, `Callout`, `Drawing` and `Image`.
  Values are `Length` (points, millimetres, centimetres, inches; no implicit conversion from a number), `PageSize`, `Margins` and `Color`, and are checked where they are set.
- **Tables** with fixed, fractional and automatic column widths (measured from the header and the first 100 rows), cell padding, borders, header, zebra, row and cell fills, logical alignment, right-to-left column order, a header repeated
  on every page, and rows that are never split unless taller than a page. Rows are an `IEnumerable<TableRow>` enumerated once for each save and never held whole; `Table.MaxRows` cuts a table and reports it.
- **Lists**: `ItemList` with bullet, decimal, letter and Arabic-Indic markers, a start number, an indent and a marker gap, item spacing, a hanging indent (wrapped lines align with the text), one level of nested lists, and the marker on the right in a right-to-left item. Items split between pages by their lines with the marker kept with the first line (a paragraph with `KeepTogether` is kept whole); a list may be in a callout, a column cell, a header or a footer. A bullet falls back to U+25CF, U+2219, U+00B7 or a hyphen-minus when no font has U+2022, and a marker larger than the text makes the first line of its item taller.
- **Columns and callouts**: one to four equal cells for a KPI row, and a box with a fill, border, padding and corner radius that is split between pages or kept together.
- **A drawing canvas** for charts as vectors: paths (lines, rectangles, rounded rectangles, ellipses, arcs, curves), fills with either rule, strokes with dashes, caps and joins, clipping, transforms, opacity, text through the
  document's fonts, and images, in points with the origin at the top left. **`PdfImage.FromBytes`** reads JPEG (passed through) and PNG (decoded, with transparency), and no other format.
- **Headers and footers** built for each page from a `PageContext` (number, count, first and last page), drawn inside the margins after the body is laid out, so "page x of y" needs no second pass.
- **`TextMeasurer`**, which measures text with the engine that draws it, for placing labels.
- **Fonts supplied, never discovered**: `FontCollection` (thread-safe, `IDisposable`) takes TrueType bytes, reads family, weight and style from the font, refuses fonts that forbid embedding with `FontNotEmbeddableException`,
  and chooses a font for each grapheme cluster by family, weight, style and coverage. Only the glyphs used are embedded, as subsets.
- **Right-to-left and mixed-direction text** by the Unicode bidirectional algorithm, with Arabic, Hebrew, Devanagari, Bengali, Tamil and Thai shaping through HarfBuzz and Latin, Greek and Cyrillic. Text is written to the file in logical
  order, so it can be searched, copied and read by a screen reader. Measured by reading the files back: 3120 of 3120 strings of the page corpus through PdfPig and through Firefox 157.0's pdf.js, and 61 of 61 mixed-direction lines through PdfPig and through pdf.js 6.3.335 (pdf.js measured again on 22 November 2026 after the language review changed the corpus text; compared in NFD with white space, U+034F and the bidirectional controls removed);
  xpdf's position-ordered `pdftotext` reads 45 of 61, a floor the tests hold (`docs/known-limitations.md`).
- **Deterministic output** (`Deterministic`, `Creation`, `Compress`): the same document is the same bytes, and with compression off the same bytes on every .NET version. Metadata: title, subject, author, creator, language.
- **Cancellation**: `SaveAsync` takes a `CancellationToken` and checks it while the document is laid out, so a long streamed table stops within a row when cancelled, not only when a page is written.
- **Diagnostics and limits**: `PdfDocument.Diagnostics` lists the characters no font has (with language and page), the families that were not found, the tables that were cut, the page count and the byte count;
  `DocumentLimits` (`MaxPages`, `MaxBytes`, `MaxTableRows`) stop a runaway document with a `PdfLimitExceededException`, and nothing is written to the stream when one is hit.
- **Documentation and samples**: a user guide (`docs/guide.md`), the known limitations, the baselines (the alternatives, and Pdfwright's own table of 1,000 rows and ten-page report, cold and warm, from the harness in `benchmarks/`), and a runnable sample report (`samples/Pdfwright.Samples.Report`). Every code block of the
  README and the guide is compiled and run by the tests, which fail when a block and its tested code differ.
- **A pixel limit for pictures**: a JPEG or PNG of more than 40 million pixels (about 160 MB decoded for a PNG with transparency) is refused by `PdfImage.FromBytes` with an `ArgumentException` that names the picture's size, the limit and the option; the side limit of 65,535 is unchanged.
  `PdfImage.FromBytes(bytes, new PdfImageOptions { MaxPixels = n })` raises or lowers it, from 1 to 100,000,000.
- **A package** for `net8.0`, `net9.0`, `net10.0` and `net11.0` that depends only on `HarfBuzzSharp` and its native assets, ships no font, and carries the README, the LICENSE and the NOTICE.

### Not in this version

Tested CJK (a TrueType CJK font is accepted, not tested; fonts with CFF outlines, which most CJK fonts have, are refused and come in 0.2), vertical text, dictionary line breaking for Thai, Lao, Khmer and Burmese, emoji and colour fonts, forms, encryption, PDF/A, tagged (accessible) PDF, links on text, hyphenation, justification, table cell spans, widow and orphan control, synthetic bold and italic, and any use of system fonts.
Each is named, with the reason, in `docs/known-limitations.md`.

### Internal, not public API

The PDF writer (objects, cross-reference stream, content streams, Flate, Info and XMP metadata, document id), the font engine (a validating TrueType reader, the embedding licence rules, a glyph-closure subsetter, Type 0 fonts with
`ToUnicode`), and the text engine (Unicode 18.0.0 tables and their generator, the bidirectional algorithm UAX #9, line breaking UAX #14, grapheme clusters UAX #29, script itemization, shaping and font fallback, paragraph layout).
The text engine passes every row of `BidiTest.txt` (770,241), `BidiCharacterTest.txt` (91,707), `LineBreakTest.txt` (19,346) and `GraphemeBreakTest.txt` (853).

### Tests and tooling

- The public surface (with nullability) is recorded in `tests/Pdfwright.Tests/PublicApi/Pdfwright.PublicApi.txt`, and a test fails when it changes without that file changing.
- Golden files for each script group and the README report, compared by SHA-256, with `tools/Pdfwright.GoldenDiff` to see what differs; visual golden files, every page drawn by PDFium and compared with a stored picture;
  seeded property and fuzz tests; failure cases for every script; `qpdf --check` on every generated file in CI; `tools/pdfjs-check` for Firefox's pdf.js; the benchmark and process harness (`benchmarks/`).
- CI on Linux, Windows and macOS, a job that makes the sample report inside the slim .NET runtime images for glibc (Debian) and musl (Alpine), and a release workflow that publishes with Trusted Publishing and no stored key.
- A language review of the corpus text, the report sample and the guide by an AI agent (22 November 2026): corrections applied (the Arabic phrases of the sample and the guide, the corpus lines), goldens regenerated.

[Unreleased]: https://github.com/sinanoran/pdfwright/compare/v0.1.1...HEAD
[0.1.1]: https://github.com/sinanoran/pdfwright/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/sinanoran/pdfwright/releases/tag/v0.1.0
