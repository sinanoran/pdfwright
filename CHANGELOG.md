# Changelog

All notable changes to this project are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- The repository skeleton (slice S0 of `docs/plan-0.1.md`): the build for `net8.0` to `net11.0`, the CI
  and release workflows, the test corpus in nine scripts and mixed-direction text with its fonts and their licences on record, and
  the benchmark and process harness with the recorded baselines. No writer and no public API yet.

- Slice S1 of the plan, the PDF writer core (internal): PDF objects and their serializer, the file structure with a
  cross-reference stream and a content-based document id, a content-stream builder, a document writer (page tree,
  Info and XMP metadata, language, URI and page links, per-page fonts, opacities and XObjects), Flate compression or none,
  output to a stream that need not be seekable. Tested by reading generated files back with PdfPig, checking them with qpdf,
  and comparing the uncompressed bytes with a golden file.

- Slice S2 of the plan, the font engine (internal): a TrueType reader that checks every table, glyph range, composite and
  character map against the file when it is parsed (formats 4 and 12), the embedding licence rules of OS/2 `fsType`, a subsetter
  that keeps glyph ids, and the embedded font object (Type 0 / CIDFontType2 with `Identity-H`, widths, `ToUnicode`, a
  deterministic subset tag). Tested against nine fonts in nine scripts, with HarfBuzz as a second reader, PdfPig and qpdf.

- Slice S3a of the plan, the bidirectional algorithm (internal): the Unicode 18.0.0 data pinned in `data/unicode` with its hashes and licence,
  a generator (`tools/Pdfwright.UnicodeGen`) that makes the class, bracket and mirroring tables, and UAX #9 (explicit levels, isolates, weak and
  neutral types, paired brackets, implicit levels, L1 and L2). It passes every row of `BidiTest.txt` (770,241) and `BidiCharacterTest.txt` (91,707).

- Slice S3b of the plan, line breaking and grapheme clusters (internal): the line break class, grapheme break class and the properties their rules
  ask about, generated into one packed table; UAX #14 (rules LB1 to LB31 with the Unicode 18 additions) and UAX #29 extended grapheme clusters
  (GB1 to GB999). They pass every row of `LineBreakTest.txt` (19,346) and `GraphemeBreakTest.txt` (853).

- Slice S3c of the plan, script itemization (internal): the script and Script_Extensions of every code point with the ISO 15924 codes, and
  an itemizer after UAX #24 that splits text into runs of one script (common and inherited characters join their run, a closing bracket
  returns to the script of its opening bracket, a character used with several scripts stays in the run it is in).

- Slice S5a of the plan, the public API and the simple block flow: `Length`, `PageSize`, `Margins`, `Color`, `FontCollection` (thread-safe, family,
  weight and style read from the font, fonts that forbid embedding refused with `FontNotEmbeddableException`), `TextStyle`, `TextDirection`, `TextAlignment`,
  the blocks `Heading`, `Paragraph` (with styled runs), `Spacer` and `PageBreak`, `PdfDocumentOptions`, `PdfDocument` (`Save`, `SaveAsync`),
  `TextMeasurer`, `DocumentDiagnostics` and the limits `MaxPages` and `MaxBytes` (`PdfLimitExceededException`). The paragraph layout now takes
  runs of different size, font, language and colour in one line (a colour does not split the shaping of a word). Values are checked where they are set, with the property as the parameter name; `MaxBytes` holds for the finished file and a limit that is hit
  writes nothing to the stream (`PdfLimitExceededException.Limit` is a `DocumentLimit`); `FontCollection` is `IDisposable`; a family that is not in the collection is listed in `DocumentDiagnostics.MissingFamilies`. The public surface (with nullability) is recorded in `tests/Pdfwright.Tests/PublicApi/`.

- Slice S5b of the plan, the table: the block `Table` with `TableColumn` (`ColumnWidth.Fixed`, `Fraction`, `Auto`; logical alignment), `TableRow`, `TableCell` (paragraphs or text, padding, fill, alignment, style), `Borders` and `BorderLine`,
  header and zebra fills, header rows repeated on every page, rows that are never split unless taller than a page (then between the lines of the cells), right-to-left column order, automatic widths measured from the header and the first 100 rows.
  Rows are an `IEnumerable<TableRow>` enumerated once for each save and never held whole. `DocumentLimits.MaxTableRows` (`DocumentLimit.MaxTableRows`) stops a save; `Table.MaxRows` cuts a table and lists it in `DocumentDiagnostics.TruncatedTables`.

- Slice S5c of the plan, the rest of the block flow: the blocks `Columns` (1 to 4 equal cells of blocks, kept together on a page, right-to-left order), `Callout` (fill, border, padding, corner radius; split between pages with a box on each, or `KeepTogether`),
  `Drawing` (a box and a callback on the public `DrawingCanvas`: points as `double`, origin top left; paths, fills, strokes with dashes, clip, transforms, opacity, text through the document's fonts with missing glyphs reported, images) and `Image` (with the new `PdfImage.FromBytes` for JPEG and PNG),
  `Paragraph.KeepTogether`, and `PdfDocumentOptions.Header` and `Footer` built per page from a `PageContext` (number, count, first and last page) and drawn inside the margins after the body, in one pass: the count is always known and a streamed table is still enumerated once.
  The internal namespace `Pdfwright.Drawing` is now `Pdfwright.Graphics`. Links on text are not public yet (`docs/known-limitations.md`).
  After review: a heading is kept with the whole of a `KeepTogether` block that follows; a callout whose padding leaves no room, a gap that leaves a cell under 4 points, and a header or footer that cannot be laid out (naming the page) fail the save with an `InvalidOperationException`; `Image.Source` is the picture; `DrawingPen` has value equality and read-only dashes; a `Drawing` draws text in the direction of its `Columns` or `Callout`.

- Slice S6a of the plan, script conformance (tests and tools): a page corpus in ten script groups (Latin Extended, Greek, Cyrillic, Arabic, Hebrew, mixed direction, Devanagari, Bengali, Tamil, Thai), each a short page and a long
  page with a 100-row table, as data files under `tests/Pdfwright.Tests/Corpus/pages`; a read-back test through the public API in which every heading, paragraph and table cell (3120 strings counting the repeated rows, 308 distinct) comes back from PdfPig, and from
  Firefox's pdf.js when measured by hand, equal to its input in logical order with its spaces and string boundaries (and the right-to-left runs laid out right to left); failure cases for every script (a missing glyph, a font that may not be embedded, an unknown family); golden files for each script group and the
  README report, compared by SHA-256 and written without compression, with `tools/Pdfwright.GoldenDiff` to see what differs and `PDFWRIGHT_UPDATE_GOLDENS=1` to regenerate them; seeded property and fuzz tests (random mixed-script
  text never throws or leaves the page, a paginated table keeps every row once and its header on every page, equal bytes twice). `tools/pdfjs-check` reads whole documents.
  Fixed: a glyph drawn as `.notdef` (a mark with no glyph in the font of its base, such as a Hebrew point after a Latin digit) is kept on the baseline; HarfBuzz's fallback mark positioning had put it below the line.

[Unreleased]: https://github.com/sinanoran/pdfwright/commits/main
