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

[Unreleased]: https://github.com/sinanoran/pdfwright/commits/main
