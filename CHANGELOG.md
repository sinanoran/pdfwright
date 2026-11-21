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
  runs of different size, font, language and colour in one line. The public surface is recorded in `tests/Pdfwright.Tests/PublicApi/`.

[Unreleased]: https://github.com/sinanoran/pdfwright/commits/main
