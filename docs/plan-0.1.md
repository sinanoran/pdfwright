# Plan: Pdfwright, our own PDF package

> Written on 21 November 2026, when the repository was started; the package is called **Pdfwright** (decided by the maintainer the same day), and the placeholder names of section 5 read `Pdfwright` from here on. **Status lines in sections 2, 7 and 10 ("nothing is reserved", "in the session scratchpad", "no repository yet") describe 21 November before the repository existed;** the repository has grown since (section 9, "Where it stands"), and the baselines are in `baselines.md`.

**Status:** design for the maintainer, 21 November 2026. Nothing is reserved, created or published.
**Goal:** a .NET PDF writer for business documents (reports, invoices, statements) that gets text right in many scripts and in mixed direction,
needs no system fonts, and writes the PDF itself, in its own repository like Sheetwright. Speed and small memory matter: a headless browser
is the usual alternative and its size is the reason to write something smaller.
**Model:** the Sheetwright repository (github.com/sinanoran/sheetwright: README, `RELEASING.md`, CI, tag-driven release to nuget.org through Trusted Publishing, MIT).
**Fits:** a small, independent package: managed code, one native dependency (HarfBuzz), a public API that is small and written to stay.

## 0. The answer in one page

1. **Build the PDF writer ourselves, as well as the layout.** The package is managed code on top of one native library, HarfBuzz (through
   `HarfBuzzSharp`, MIT), for shaping. It does **not** use SkiaSharp. The reason is evidence, not taste: a spike run today (section 2) shows that
   Skia's PDF backend writes Arabic and Hebrew as presentation forms in *visual* order, so the text cannot be extracted, searched or read by a
   screen reader; SkiaSharp exposes no structure tree, outlines, language or page labels, so tagged (accessible) PDF is impossible on it; and
   it adds a 13 MB native library per runtime. Skia was the obvious first candidate, with the exit criterion "the text can be extracted back
   in logical order"; the spike fails that criterion, so the choice was between an own writer and a commercial library. This is that decision,
   with numbers.
2. **Three layers, one public surface.** A PDF writer with embedded font subsets (internal), a text engine (itemize, bidi, shape, break;
   internal) and a document layer (flow, paragraphs, tables with pagination and repeated headers, header and footer, a drawing canvas for
   charts; **public**). The public API is small and written to stay: it is the part that is hard to change after release (section 5).
3. **Version 0.1 is what a typical business report needs and nothing more:** heading, KPI row, table with pagination and repeated header, chart as vectors,
   note; header and footer with a title, a date and page numbers; page breaks; embedded subset TrueType fonts; right-to-left and mixed
   direction; Arabic, Indic and Thai shaping. **Not in 0.1:** CJK (needs CFF fonts and a CFF subsetter), emoji and colour fonts, forms,
   encryption, PDF/A, tagged PDF (but the design carries it, section 6).
4. **Targets (section 7), against what a headless browser (Playwright, Chromium) costs today on this machine** (measured, section 2): a 1,000-row table over 20 pages renders in
   2.0 s with a 495 MB peak that way; the target is **under 250 ms cold in a fresh process and under 60 ms warm, under 25 MB over the runtime's
   baseline, under 150 KB of output**.
5. **About 13 working weeks for one engineer-equivalent, plus or minus 40 percent** (section 9); with parallel agent sessions, 6 to 8 weeks of
   calendar if the three engine slices run side by side. The first useful checkpoint is after slice 3 (a one-page report in every
   script of the corpus, with correct text extraction), at about five weeks of effort.

## 1. What a typical use needs (reports, invoices, statements)

The typical use is a report built from a few kinds of content: **heading, KPI row, table, chart, note**. The package knows these as blocks
and layout, not as any application's concepts. The page model it must be able to produce:

- A4 portrait by default, Letter for some countries, landscape for a wide table; page one has a header (an organisation name and logo, the
  document's title, an "as of" date in a given zone and language, the filters in force, "Prepared for {name}"); later pages a thin
  running header and "page x of y".
- Body in the order given; KPI boxes up to four per row, everything else full width; **a chart is never split across pages**;
  **a table repeats its header on each page**; a caller that wants a row limit (for example 200 rows, then a "N more rows" line) applies it
  before the table, and the package offers a limit that stops the run (section 5).
- Limits (pages, bytes), a cell shortened at a given length, a stable failure reason when a limit is hit.
- Metadata: title, language, creator, no author unless given, nothing the caller did not set.
- Fonts embedded, never looked up in the operating system; a character no loaded font has is drawn as the replacement box and **reported**
  (never a failure of the document): the diagnostics say which language had missing characters.
- Charts: the caller draws bar, line, area, donut, gauge and pivot charts as vectors from data, with its own colours. So the package must
  offer a **drawing canvas** and **text measurement**, not a chart library.
- The scripts of the corpus: Latin (with Turkish, Polish, Vietnamese, German), Greek, Cyrillic, Arabic, Hebrew, Devanagari, Bengali, Tamil,
  Thai, and lines that mix directions.

The package never learns the words "dashboard", "widget" or "organisation": the caller maps its own model onto the public API of section 5.
That keeps the package usable for invoices, statements and other business documents.

## 2. Evidence gathered for this plan (21 November 2026, this machine)

All numbers are one run each, on the maintainer's Windows machine, in `Release`, and are **baselines to beat, not benchmarks**. The scratch
projects are in the session scratchpad, not in a repository.

| Question | Result |
|---|---|
| Does Skia's PDF backend (SkiaSharp 4.157.0 with `SkiaSharp.HarfBuzz` 4.157.0) keep shaped text extractable? Read back with PdfPig 0.1.16 | **Latin, Turkish (ğüşıöç İ) and Thai: yes**, logical order, correct. **Arabic: no.** "مرحبا بالعالم" comes back as presentation forms in visual order ("ﻢﻟﺎﻌﻟﺎﺑ ﺎﺒﺣﺮﻣ"). **Hebrew: reversed** ("םולש" for "שלום"). No `/ActualText` in the file; the `ToUnicode` map is glyph to presentation code point |
| Does feeding Skia the text and the HarfBuzz clusters (`SKTextBlobBuilder.AllocatePositionedTextRun`) fix it? | **No.** The pure right-to-left lines are unchanged. The stock `SKShaper` also does no bidirectional itemization: a mixed line "abc مرحبا 123 def" was shaped left to right, so the Arabic word was drawn in isolated forms. A layout engine of our own is needed whichever PDF backend is used |
| Does SkiaSharp expose what tagged PDF needs? | **No.** `SKDocumentPdfMetadata` has title, author, subject, keywords, creator, producer, dates, raster dpi, `PdfA`, encoding quality. No structure tree, no language, no outlines, no page labels, no marked content. Link and named-destination annotations exist |
| Native size | `libSkiaSharp.dll` 13.2 MB and `libHarfBuzzSharp.dll` 2.2 MB (win-x64). The Skia runtime package for Linux without dependencies is a 63 MB `.nupkg` (every architecture); HarfBuzz for Linux is 16.8 MB (every architecture) |
| Skia backend, 1,000-row table, six cells per row (the spike draws text only) | 20 pages, **279 ms** (cold process), peak working set 31 MB (7 MB over the process baseline), **590 KB** of output. The output is nearly the size of the whole font, so fonts were not subset well in this build (not investigated further) |
| Playwright (`chromium_headless_shell-1255`) today, the same table as HTML, `page.pdf` A4 with header and footer | **1,000 rows: 2,048 ms total (launch 442, content 901, pdf 705), peak working set of the process tree 495 MB, 154 KB output.** 300 rows: 1,164 ms, 382 MB, 70 KB. The browser itself is **288 MB** on disk (headless shell) or 453 MB (full Chromium) |

Two readings. The Skia path is fast and light but fails the extraction criterion for exactly the languages the maintainer's rule ("any
language, never just en/tr") exists for. Chromium extracts text well and makes small files, and costs half a gigabyte and two seconds.
The own package has to be Chromium-correct and Skia-light.

## 3. Layering and the question of writing the PDF writer ourselves

### Layers

<!-- snippet: none -->
```
public    Document layer      blocks, styles, tables, pagination, header/footer, Canvas, TextMeasurer, SaveAsync
internal  Text engine         itemization (script, direction, font fallback), bidi (UAX #9), shaping (HarfBuzz), line breaking (UAX #14),
                              justification (none in 0.1), glyph runs with clusters
internal  Font engine         TrueType parser, subsetter, metrics, CID font objects, ToUnicode (ActualText where a reader needs it)
internal  PDF writer          objects, cross-reference (stream form), content-stream builder, Flate, images, annotations, metadata (Info and XMP)
          one native library  HarfBuzz through HarfBuzzSharp (shaping); no other native code
```

The writer is behind an internal `IPageSink`-like boundary only as far as testing needs (a recording sink for layout tests); we do **not** build
a second backend.

### Option A: our layout over SkiaSharp's PDF backend; Option B: our layout and our own writer (recommended)

| | A. Skia PDF backend | B. Own writer |
|---|---|---|
| Time to a first PDF | Fastest: Skia draws, embeds fonts, encodes images, does clipping and paths | Slower: about 8 days for the writer core and 10 for the font engine (section 9) |
| Extraction of Arabic, Hebrew, ligatures | **Fails** (section 2); would need post-processing of Skia's output (rewriting font dictionaries), which is fragile | **Correct by construction**: `ToUnicode` with multi-character mappings from the shaper's clusters, one text per code (a glyph drawn for a second text gets its own code); `/ActualText` where a reader needs it (open: see the go or stop assessment) |
| Tagged (accessible) PDF, outlines, page labels, language, XMP | **Impossible** through SkiaSharp's binding | Ours: additive, and designed in from the first slice (section 6) |
| Size on disk of a deployment | +13 MB native per runtime (+ HarfBuzz 2 MB) | HarfBuzz only (about 2 MB per runtime); no Skia, no fontconfig, no libfreetype question |
| Memory and speed | Good (spike: 7 MB over baseline) | Equal or better: no canvas abstraction, no native copies; a page is a byte buffer of operators |
| Byte-stable output (golden files) | Skia writes a document id and dates; stability not controllable | Fully ours: fixed ids and dates give identical bytes, so **golden PDFs compare by hash** |
| Fonts | Skia decides how to subset (spike: it did not) | Ours: glyph-closure subsets, tested sizes |
| Risk | Dependency on a large native binding that changes major versions every few months (4.x now) | **We own PDF-spec bugs**: a viewer that rejects a file is our defect. Mitigated by a small, fixed subset of PDF, `qpdf --check`, three viewers in the release checklist |
| AOT and trimming | Native Skia and P/Invoke surface | Managed except HarfBuzz; trimming-friendly |
| Licences | Skia BSD-3, FreeType, ICU parts: to audit | Our code MIT; HarfBuzz "old MIT"; Unicode data under the Unicode licence (NOTICE) |

**What we gain by writing the writer: correct text, accessibility, tiny deployments, deterministic bytes.** **What we lose: about three weeks of
effort and the risk of PDF bugs.** The three weeks buy the three things the maintainer cares about (languages, size, memory) plus the one the
European Accessibility Act will ask for, so B is recommended. **Contingency:** if slices 1 and 2 overrun by more than 50 percent, ship 0.1 on
Skia as the writer for left-to-right scripts only, behind the same public API, and say the limitation; the API in section 5 does not change.

### What we still depend on, and what we write

- **HarfBuzz** (shaping of Arabic, Indic scripts, Thai, Latin kerning and ligatures): not rewritten, ever. `HarfBuzzSharp` 14.4.0.200 (MIT).
  It exposes `Face`, `Font`, `Buffer`, `Shape` and metrics; it exposes **no subsetter**, so the font subsetter is ours.
- **Unicode data** (bidi class, script and script extensions, line-break class, mirroring, grapheme break): .NET exposes none of these but
  general category and `StringInfo` (extended grapheme clusters). Generated tables from the Unicode Character Database, produced by a build
  tool in the repository, checked in as compact binary resources (about 100 KB), regenerated per Unicode release.
- **Bidi (UAX #9) and line breaking (UAX #14):** written by us against the Unicode conformance test files (`BidiTest.txt`,
  `BidiCharacterTest.txt`, `LineBreakTest.txt`), which give an objective pass rate.
- **Thai, Lao, Khmer, Burmese word breaking:** needs a dictionary that .NET does not offer. 0.1 breaks at spaces and punctuation and, for a word
  wider than its box, between grapheme clusters (`overflow-wrap: anywhere` behaviour); a `ILineBreaker` hook lets an application supply a
  dictionary breaker. Said plainly in the limitations.
- **Images:** a logo is a JPEG or PNG. JPEG is passed through (DCT); PNG is decoded by a small managed decoder (zlib through
  `System.IO.Compression`). Nothing else in 0.1.

## 4. Scope of version 0.1, as a list a reviewer can tick

**In:** (a) block flow: heading (levels 1 to 6), paragraph with runs (bold, italic, size, colour, language), callout (the "note"), spacer,
page break, keep-together, columns (the KPI row: equal-width cells, 1 to 4); (b) **table**: columns fixed, fractional or auto (measured from the
header and the first 100 rows), cell padding, borders and fill, alignment as *start/centre/end* (logical, so right-to-left works), header rows
repeated on every page, rows never split across pages unless taller than a page, a row limit; (c) **header and footer** as blocks built per
page from a `PageContext` (page number, page count, first-page flag), including a logo and a date; (d) **drawing canvas** for charts (paths,
fills, strokes, dashes, clipping, transforms, opacity, text at a point, images) and a public text measurer; (e) fonts: the application hands
over TrueType font data (regular, bold, italic, per script); the package picks by family, weight, style and coverage, with **per-run
fallback**; nothing from the operating system, ever; (f) scripts: Latin (extended), Greek, Cyrillic, Arabic (joining, marks, digits as
given), Hebrew (marks), Devanagari, Bengali, Tamil, Thai, with mixed direction in one paragraph; (g) metadata (title, subject, language,
creator, dates, XMP with the same), a fixed `/ID` option for deterministic output; (h) diagnostics: missing glyphs, fonts without embedding
permission (OS/2 `fsType`: **refused**), a table that exceeded a limit.

**Out, named so nobody assumes them:** CJK, emoji and colour fonts (CFF subsetting and COLR/SVG are separate work); vertical text; hyphenation;
justified text; table cell spans; floating images; links beyond external URI and internal destinations (a link is in, a table of contents
is not); forms; encryption and signatures; PDF/A and PDF/UA conformance claims (tagging is designed, section 6); gradients and shadings;
variable-font instances other than the default; any use of system fonts.

## 5. Public API sketch (hard to change after release)

Names are placeholders under the package's root namespace `Pdf` (the real name is the maintainer's choice, section 10). Coordinates and
sizes are `Length` values (a `readonly struct` with `Length.Pt(12)`, `Length.Mm(20)`, no implicit conversions from numbers, so a unit is
never guessed). The canvas origin is the **top left, y down** (what authors of charts expect; the writer flips once).

<!-- snippet: none -->
```csharp
// Fonts: supplied, never discovered. Thread-safe, shared, parsed once.
FontCollection fonts = new FontCollection()
    .Add(File.ReadAllBytes("NotoSans-Regular.ttf"))            // family, weight, style are read from the font
    .Add(...bold..., ...italic..., ...arabic..., ...devanagari...);

PdfDocument doc = new(new PdfDocumentOptions
{
    Fonts = fonts,
    DefaultStyle = new TextStyle { Family = "Noto Sans", Size = Length.Pt(10), Language = "tr" },   // BCP 47
    PageSize = PageSize.A4, Margins = Margins.Mm(top: 20, right: 15, bottom: 20, left: 15),
    Title = "Open invoices", Language = "tr", Creator = "Example",
    Direction = TextDirection.Auto,                              // the document's base direction; a block may override
    Header = page => new Paragraph(...), Footer = page => new Paragraph($"{page.Number} / {page.Count}"),
    Deterministic = true,                                        // fixed /ID and dates from Creation, for golden files
});

doc.Add(new Heading(1, "Open invoices"));
doc.Add(new Columns(new[] { kpi1, kpi2, kpi3 }));                // the KPI row
doc.Add(new Table(columns, header: headerRow, rows: rows)        // rows may be an IEnumerable (streamed)
{
    RepeatHeader = true, Borders = Borders.Horizontal, ZebraFill = ...,
});
doc.Add(new Drawing(Length.Mm(180), Length.Mm(70), canvas => { canvas.Path(...); canvas.Text(...); }) { AltText = "Bar chart of ..." });
doc.Add(new Callout(new Paragraph("This figure is provisional.")));

await doc.SaveAsync(stream, cancellationToken);                  // streams pages out when it can
DocumentDiagnostics report = doc.Diagnostics;                    // missing glyphs by language, pages, bytes
```

**What the sketch fixes, and why each is expensive to change later:**

1. **Blocks are immutable records** (`Heading`, `Paragraph`, `Table`, `Columns`, `Drawing`, `Callout`, `PageBreak`, `Spacer`) in a **closed**
   set (closed by an internal constructor and a runtime check; C# cannot check a `switch` over a class hierarchy for completeness, so the layout's `switch` throws for a block it does not know, and a caller's mapping is checked by a test, not by the compiler). New blocks are added, never changed.
2. **Styles are values, language is a first-class property** (BCP 47). Language selects the shaping features and the line-breaking
   behaviour; it also becomes the tagged PDF's `/Lang` later.
3. **Direction is logical everywhere** (`Start`/`End`, never `Left`/`Right`, except on the canvas, which is geometric).
4. **Fonts are supplied by the caller**, one `FontCollection` shared across documents (thread-safe, caches parsed tables); a document is not
   thread-safe; `SaveAsync` takes a `CancellationToken` and a `Stream` (no `byte[]` return that forces the whole file into memory).
5. **Determinism is an option, not an accident** (`Deterministic`), because golden tests and reproducible reports depend on it.
6. **Diagnostics are data, not exceptions:** a missing glyph, an unlicensed font or a limit hit is reported in `DocumentDiagnostics`
   and, for limits, can stop the run with a typed exception the caller chooses (`Limits.MaxPages`, `MaxBytes`).
7. **`TextMeasurer` is public** (`Measure(string, TextStyle, Length? maxWidth)` returning width, height, line count): chart authors need it,
   and it uses exactly the engine that draws, so measured and drawn text cannot disagree.
8. **Table rows are streamed** (`IEnumerable<TableRow>` or `IAsyncEnumerable`; `IAsyncEnumerable` was not built, see S5b deviation 2) so a 1,000-row table is never held twice; auto column widths
   come from the header and a bounded sample, said in the documentation.
9. **Versioning:** 0.x may break between minors with a changelog; the section 5 surface is reviewed line by line before 1.0, and the
   blocks above carry `[Experimental]`-free names from the start so that code written against 0.1 does not need renaming later.

**Mapping from a caller's own report model** (an example, not part of the package): a heading to `Heading`; a row of KPIs to `Columns` of small
`Paragraph`s on a filled `Callout`; a table to `Table` (header repeated; any row limit and "N more rows" line are the caller's); a chart to
`Drawing` (drawn with the canvas and the measurer); a note to `Callout`; the first-page header and the running header to `Header`; "page x of y"
to the footer's `PageContext`. The caller owns its limits and its words.

## 6. Tagged (accessible) PDF later, without rewriting

The first release does not claim PDF/UA, but it is built so that adding it is additive:

- Every block carries a `StructureRole` (`H1..H6`, `P`, `Table`, `TR`, `TH` with `Scope`, `TD`, `Figure` with the `AltText`, `Artifact` for
  header, footer and decoration); `TableCell`s have `Scope` and `Header` properties from 0.1, ignored by the writer until tagging is on.
- The writer wraps every drawn element in marked content (`BDC ... EMC`) with an `MCID` when `Tagged = true`; the content-stream builder
  allocates ids from day one behind that flag, so turning it on adds a `StructTreeRoot`, a parent tree and a role map, not a rewrite of the
  layout.
- `/Lang` on the catalog and on spans of another language (the run's `Language`), `DisplayDocTitle`, XMP metadata and an `OutputIntent`
  are the same flag's second half. Embedded fonts and logical-order extraction (this package's point) are what PDF/UA-1 also needs.
- **Validation:** veraPDF (a test-time tool, run in CI as a process) for PDF/UA-1 and PDF/A-2; no claim of conformance is made until it passes
  on the corpus. Target: the version after 0.1 (0.2), once the engine slices are stable. Until then, 0.1 documents are not
  tagged, which is a known limitation (`docs/known-limitations.md`); a caller that needs an accessible form offers another one next to the PDF.

**Room left in the 0.1 public API for tagging (checked 21 November 2026, before 0.1.0).** The design above was written before the public API; the API
as released differs from it, and tagging still needs no breaking change:

- There is **no `StructureRole` property and no `Scope` or `Header` on `TableCell`** in 0.1, contrary to the first point above. Neither is needed
  to start: the role of every block follows from its type (`Heading.Level` gives `H1` to `H6`, `Paragraph` gives `P`, `Table` gives `Table`, `TR`
  and `TD` with the rows of `Table.Header` as `TH` with column scope, `Image` and `Drawing` give `Figure`, `Callout` and `Columns` give `Div`), and
  the header and footer are drawn by the document, so they become `Artifact` without the caller saying so. Where a caller must override that (a row
  header, a decorative image), 0.2 adds optional `init` properties to the sealed block and cell types, which is not a breaking change.
- **Alternative text is already in 0.1:** `Image.AltText` and `Drawing.AltText` are stored and not written yet; 0.2 writes them as `/Alt` on the
  `Figure` element.
- **Part of the second half is already written in 0.1**, whatever the flag: the catalog's `/Lang` (from `PdfDocumentOptions.Language`),
  `DisplayDocTitle` whenever a title is set, and the XMP metadata. What waits for tagging is the `/Lang` of spans in another language (from the
  runs' `TextStyle.Language`, which 0.1 already carries) and an `OutputIntent`.
- **Turning it on** is a new `PdfDocumentOptions.Tagged` property (an `init` property, so additive); the content-stream builder already has the
  marked-content operator with an `MCID`, unused in 0.1. Contrary to the second point above, 0.1 allocates no ids and has no such flag yet; both
  arrive with tagging, inside the writer, with no change to the public API.
- The block set is closed, so a new structure (a list, a figure caption, a note) arrives as a new block type in a minor version, as the closed set
  intends; code written against 0.1 does not change.

## 7. Performance targets and how they are measured

**Baselines (section 2):** Headless Chromium through Playwright, 1,000-row table: 2,048 ms, 495 MB, 154 KB. Skia-backed spike: 279 ms, +7 MB, 590 KB.

| Scenario | Target (warm, steady state) | Target (cold, fresh process) | Memory | Output size |
|---|---|---|---|---|
| **20-page report**: heading, 4 KPI boxes, three charts, two 40-row tables, notes, header and footer, Latin | **under 40 ms** | under 200 ms | managed allocation **under 10 MB**; working-set increase over the runtime baseline **under 25 MB** | under 120 KB |
| **1,000-row table, 6 columns, about 20 pages, repeated header** | **under 60 ms** | **under 250 ms** | allocation under 20 MB; increase under 25 MB, and **flat as rows grow** (rows are streamed, a page is released when emitted) | under 150 KB (Playwright: 154 KB) |
| **100 pages, 12 charts, 20 tables** (a stress document; the Skia spike was meant to do it in under 5 s and under 200 MB) | under 400 ms | under 700 ms | under 60 MB | under 600 KB |
| Arabic or Devanagari table of the same size | at most **2 times** the Latin time | | same | same |
| Fonts | a Latin report embeds **only the glyphs it uses**: Noto Sans subset under 25 KB | | | |

**How it is measured, so a regression is visible and the numbers are not lore:**
- A `benchmarks/` project (BenchmarkDotNet, `MemoryDiagnoser`) for warm time and allocations of each scenario above, run in CI on the release
  runner as a **non-blocking** job that posts a table and fails only on a regression of 25 percent or more against the last tagged release.
- A **process harness** for the cold numbers and the working-set increase (it starts a fresh process per run, reads `PeakWorkingSet64`
  and wall time, runs 10 or 30 times, reports the median and the p95, and the bare runtime start as a baseline), the method of the spike in section 2, which also measures Playwright on the same
  machine for the comparison table in the README. The harness is a console project in the repository, so the comparison can be re-run by
  anyone.
- **Allocation budgets as tests:** a unit test that renders the 1,000-row table and fails when `GC.GetAllocatedBytesForCurrentThread` exceeds
  the budget by 25 percent (allocation counts are stable across machines where times are not).
- The numbers go in `docs/baselines.md` (linked from the README) with the machine described, as Sheetwright's README states its own limits.

**Measured on 22 November 2026 (written afterwards; `docs/baselines.md` has the method, the machine and the caveat that free memory was low and moved between 0.6 and 7.5 GB, which moves the cold times between about 1.5 and 2.1 s):** the time and memory targets above are **not met**, and the output target is met. Table of 1,000 rows (21 pages): cold p50 2,095 ms against 250 ms (the runtime's own start is about 150 ms of it); warm p50 347 ms against 60 ms (a full GC between runs); allocation 188 MB against 20 MB; output 514 KB against 150 KB (**now met**: 75,661 bytes after the glyphs were positioned with a relative `Td`, see the S4 experiments); peak working set 52 MB, an increase of 30 MB over the 22 MB runtime baseline against the 25 MB target. Ten-page report (half the size of the 20-page scenario; no 20-page scenario exists yet): cold p50 770 ms, warm p50 50 ms, allocation 24 MB, 103 KB (57 KB with the relative `Td`; under the 120 KB), working set 49 MB (27 MB over the baseline), against 200 ms, 40 ms, 10 MB and 120 KB for twenty pages. The Arabic table, the 100-page stress document and the allocation-budget test are not measured yet (they are since 23 November 2026, see the end of this paragraph). **Shaping fast paths (22 November 2026, output unchanged):** warm table 146 to 136 ms and 82.3 to 59.0 MB per run, warm report 34 to 22 ms and 11.3 to 8.1 MB; the targets are still not met (`docs/baselines.md`). **Layout state and content buffers (22 November 2026, output unchanged):** warm table 59.0 to 28.2 MB per run (target 20 MB), warm report 8.1 to 4.7 MB (target 10 MB for twenty pages); the times are within the noise of that sitting (a busier machine than the one above) and no speed-up is claimed: the warm table went from 168.2 and 170.5 ms to 165.8 and 156.9 ms (1 and 8 percent, 5 percent on the means), the cold table from 1,099 and 1,154 ms to 1,008 and 1,106 ms (8 and 4 percent, 6 percent on the means), while two sets of the same build differ by up to 10 percent; the 51 MB peak working set did not move. What is left is mostly HarfBuzz shaping (about a fifth to a third of the warm time) and the JIT (a ReadyToRun build was about a third faster cold, 1,053 against 1,575 ms on the means of two paired sets each; a packaging choice, not made here). The profile of the phases is in `docs/baselines.md`. **Word-by-word shaping (23 November 2026, output unchanged):** printable ASCII in a font whose layout rules never involve the space is shaped and cached word by word, so the table calls HarfBuzz about 1,000 times instead of 6,000; warm table 131.2 and 126.2 ms to 102.1 and 104.9 ms (about a fifth less; target 60 ms), cold table 1,078 and 1,073 ms to 977 and 977 ms (target 250 ms), the report level within noise (warm 21.4 and 21.0 ms to 22.4 and 22.2 ms, cold 576 and 580 ms to 588 and 584 ms), allocation unchanged (27.8 MB for the table, target 20 MB). The targets are still not met. **Glyph positions without the custom number format (23 November 2026, output unchanged):** warm table 73.5 to 48.4 ms in the set where both builds had about 7 GB free (**under the 60 ms target** there; 106.6 against 73.9 ms in a first set at uneven free memory), the report and the cold times level within noise (cold table 663 and 1,155 ms against 643 and 1,138 ms, mostly the JIT; target 250 ms), allocation unchanged (27.8 MB; target 20 MB). **Per-cell allocation (23 November 2026, output unchanged):** warm table 27.8 to **18.5 MB** per save (**under the 20 MB target**), warm report 4.8 to 3.4 MB; times level within noise. Still not met: the cold time (mostly the JIT) and the working-set increase. **ASCII paths (23 November 2026, output unchanged):** warm table 77.9 and 87.1 ms to **57.8 and 54.9 ms** (**under the 60 ms target** in both sets), warm report level (about 21 ms), cold table 1,092 and 1,458 ms to 896 and 1,240 ms, cold report 934 and 873 ms against 1,007 and 1,029 ms (noise at 2.6 to 4.9 GB free), allocation unchanged (18.5 MB). Met now: output size, warm time and allocation of the table. Not met: the cold time (target 250 ms; about 900 to 1,250 ms on this machine, mostly the JIT and the runtime start) and the working-set increase (31 to 33 MB over the runtime's 21 to 22 MB, target 25 MB). **ReadyToRun, paired (23 November 2026):** the same program published with and without ReadyToRun, alternating: cold table 607 and 549 ms to **378 and 370 ms**, cold report 432 and 393 ms to 264 and 251 ms, peak working set 3 to 5 MB lower; the runtime alone starts in about 110 ms. The 250 ms target for the table is still not met (370 ms with ReadyToRun), nor the working-set increase (for the table, 26 MB over the runtime's start with ReadyToRun and 28 MB without; target 25 MB). ReadyToRun is the application's setting, so the decision (22-23 November 2026, the maintainer) is to recommend it in the guide (section "Startup time") and not to ship precompiled code in the package. **The other scenarios and the allocation budgets (23 November 2026, release 0.1.4):** the 20-page report (20 pages, 87 KB, 6.2 MB: **size and allocation met**; the Noto Sans subsets 6.4 and 10.8 KB compressed, under 25 KB: **met**), the stress document (101 pages, 342 KB, 31.6 MB: **size and allocation met**), the Arabic and Hindi tables (43.2 and 37.9 MB, 2.3 and 2.0 times the Latin table: the "same memory" target is **not met**, and first noisy times put them at 3 to 8 times the Latin table against the 2 times allowed: **not met**), and the 10,000-row table (1.3 times the working set of 1,000 rows: rows are streamed). `AllocationBudgetTests` now fails a build that allocates more than 25 percent over the budgets of the table, the 20-page report and the stress document, and over guards at today's allocation of all five documents. The times of the new scenarios are still to be measured in a quiet sitting. **Cold start (23 November 2026, output unchanged):** less code to compile on the first save (arrays by glyph instead of dictionaries keyed by tuples, flags instead of sorted sets in the subsetter, the library's own scratch slots instead of the array pool's generic paths); paired at 12 GB free, the cold table from 492 to 419 ms with the JIT and from 310 to **241 ms with ReadyToRun (under the 250 ms target)**, the cold report from 357 to 316 ms and 222 to 183 ms. The working-set increase of the table is 27.5 MB (JIT) and 25.2 MB (ReadyToRun) over the runtime's start, against 25 MB. **Font bytes held once (23 November 2026, output unchanged):** HarfBuzz reads the collection's pinned copy of a font instead of a native copy of its own; with the cold start, the table's working-set increase is 25.7 MB (JIT) and **23.8 MB with ReadyToRun (under the 25 MB target)**, the ten-page report's 19.2 and 16.9 MB. **The scenarios in a quiet sitting (same day, 12 GB free):** warm table about 30 ms, the 20-page report 29 to 31 ms (**under 40 ms, met**), the stress document 109 to 124 ms (**under 400 ms, met**), the Arabic and Hindi tables 156 and 182 ms, about 5 and 6 times the Latin table (**not met**: at most 2 times), the 10,000-row table about 370 ms. Met now: the table's warm time, cold time with ReadyToRun, allocation, working set with ReadyToRun, and size; the 20-page report's warm time, allocation and size; the stress document's time, allocation and size; the subset size. Not met: the cold table with the default JIT (417 ms), and the Arabic and Hindi tables' time and memory.

## 8. Test strategy

1. **Golden PDFs, byte-stable.** `Deterministic = true` fixes `/ID`, dates and object order, so a golden file is compared by **hash** and a
   difference opens a diff of the decoded content stream (a small tool in `tools/`). Goldens are regenerated on purpose, in their own commit.
2. **Text extraction is the acceptance test.** For every string of every corpus, PdfPig (Apache-2.0; the extractor the spike used, test-only)
   reads the file back, and the result must equal the input in **logical order** after normalization. **This is the test Skia failed**
   (Arabic and Hebrew, section 2); it is written first and drives slices 2 and 3.
3. **A corpus per script**, each a short page and a long page with a table: Latin Extended (Turkish with dotless i and the İ pair, German,
   Polish, Vietnamese), Greek, Cyrillic, Arabic (joining, lam-alef, diacritics, Arabic-Indic and Western digits, numbers inside Arabic text),
   Hebrew (points, final forms), **mixed direction** (English inside Arabic, Arabic inside English, nested quotes, numbers with signs),
   Devanagari (conjuncts, reordering matras), Bengali, Tamil, Thai (stacked marks), plus the failure cases (a missing glyph, a font that may not be
   embedded). Fonts for the corpus are Noto (SIL OFL) and live in the test project, not in the package.
4. **Unicode conformance files** drive the bidi and line-break implementations (`BidiTest.txt`, `BidiCharacterTest.txt`, `LineBreakTest.txt`
   and the grapheme tests); the pass rate is printed by CI and must stay at 100 percent of the rows the implementation claims.
5. **Visual check:** a render of each golden page through PDFium (a test-time package such as `PDFtoImage`; its licence is checked before
   it is used) compared with a stored PNG by a tight per-channel tolerance, so a shaping regression that extraction would miss (a mark drawn
   in the wrong place) is caught. Native-speaker review of the Arabic, Hebrew, Devanagari and Thai pages before 0.1.0 (a release-checklist
   line, not a test).
6. **Structure and viewers:** `qpdf --check` on every generated file in CI (a process, Apache-2.0); a release checklist that opens the
   golden set in Chrome, Edge, Acrobat Reader and macOS Preview.
7. **Property and fuzz tests:** random mixed-script strings never throw and never draw outside the page; table pagination invariants (every
   row appears once, the header repeats on every page after the first it starts on, no row is split unless taller than a page); layout is
   deterministic (two runs, equal bytes).
8. **Comparison with a browser.** The same table rendered by headless Chromium and by the package (`benchmarks/`, `docs/baselines.md`) so
   differences in time, memory and size are visible and repeatable.

## 9. Slices and estimate

Effort in **working days of one engineer-equivalent** (an agent session plus review counts as the engineer for the estimate); the range is
plus or minus 40 percent because shaping and font bugs are the part nobody can estimate from outside. Slices are small and ordered; each ends
with a green CI and a reviewed pull request, as in Sheetwright.

| Slice | What | Days (the estimate) | Status, 21 November 2026 |
|---|---|---|---|
| **S0** | Repository skeleton (as Sheetwright: Directory.Build.props, central packages, CI, RELEASING, README stub), the corpus and fonts, the spike results of section 2 as a documented baseline, the benchmark and process harnesses | 3 | Done (commits on `main`, before the pull request workflow) |
| **S1** | **PDF writer core:** objects, stream-form cross-reference, content-stream builder, Flate, page tree, Info and XMP, annotations (URI, destination), deterministic mode, `qpdf --check` in CI | 8 | Done (commit on `main`, before the pull request workflow) |
| **S2** | **Font engine:** TrueType parser, glyph-closure subsetter (keeps glyph ids, drops unused outlines; composite glyphs followed), CID fonts with `Identity-H`, widths, **`ToUnicode` from clusters, one text per code**, `fsType` check; extraction test for Latin | 10 | Done: #1, and the go or stop assessment #2 |
| **S3** | **Text engine:** Unicode tables and their generator, script itemization, bidi (UAX #9) and mirroring, font fallback runs, HarfBuzz shaping with features, grapheme-safe breaking, line breaking (UAX #14), the conformance files | 12 | Done: #3 (S3a), #4 (S3b), #5 (S3c), #6 (S3d) |
| **S4** | **Paragraph layout and the drawing canvas**: lines, alignment (logical), runs, the public `TextMeasurer`, paths, clip, transforms, opacity, JPEG and PNG | 8 | Done: #7 (S4a), #8 (S4b), #9 (S4c, the pdf.js reading check), #10 (S4d) |
| **S5** | **Document layer:** the block set, flow and page breaks, keep-together, columns, **tables with pagination, repeated header, streamed rows, auto widths**, header and footer with page count (two passes only when the footer asks for it) | 10 | Done: #11 (S5a), #12 (S5b), #13 (S5c) |
| **S6** | **Script conformance:** the corpora for Arabic, Hebrew, Devanagari, Bengali, Tamil, Thai, mixed direction; extraction and visual goldens; fixes in S2 to S4; native-speaker review | 8 | Done: #14 (S6a), #15 (S6b); the native-speaker review is the maintainer's (release checklist) |
| **S7** | Documentation, samples, the README comparison table, `RELEASING.md`, Trusted Publishing, the CHANGELOG, **0.1.0** | 5 | Done: #17 (documentation, samples, comparison table, `RELEASING.md` and the CHANGELOG); 0.1.0 (22 November 2026) is the first release, tagged by the maintainer after the release-preparation pull request (Trusted Publishing and the tag are the maintainer's) |
| | **Total to 0.1.0** | **64 days, about 13 weeks** | S0 to S7 done; 0.1.0 is the first release (the performance targets of section 7 are not met) |

**Where it stands (22 November 2026).** Slices S0 to S7 are done, and version 0.1.0 (22 November 2026) is the first release. The writer, the font engine, the text engine, paragraph layout and the
canvas, the document layer (blocks including `ItemList`, tables, columns, callouts, drawings, images with `PdfImageOptions`, headers and footers), the script conformance corpus with its extraction and
visual goldens, and the documentation (the README with its comparison table, the user guide, the sample report, `RELEASING.md` and the CHANGELOG) are in the repository, with CI that also makes the sample report in the glibc and musl runtime images, the benchmarks of
`docs/baselines.md` and a language review of the corpus text by an AI agent (22 November 2026, pull request #27). The day counts above were the estimate; they are kept as written. **What 0.1.0 does not have, stated in `RELEASING.md` and the CHANGELOG:** a
native-speaker review (a language review by an AI agent replaced it; a native-speaker review stays recommended), a check in Acrobat Reader and macOS Preview (the viewer check was PDFium renders), the performance targets of section 7 (not met, see there; the
diagnosis and fixes are a candidate for after 0.1.0), and tested CJK (a TrueType CJK font is accepted, not tested; CFF outlines come in 0.2, `plan-0.2.md`). The tag, Trusted Publishing and the publication are the maintainer's.

**Order and parallel work:** S0 first; then **S1 and S2 can run beside S3** (the text engine has no dependency on the writer until S4);
S4 needs S2 and S3; S5 needs S4; S6 runs from S3 on as the corpus grows and finishes last. With three parallel sessions the calendar is
**6 to 8 weeks**; the critical path is S0, S3, S4, S5, S6, S7. **First checkpoint** (a one-page report in every script, text extracted
correctly): after S4, about **5 weeks of effort**. A **go or stop point** after S2 and S3's first half (week 3 to 4): if Arabic extraction and a
Devanagari conjunct are not correct by then, the contingency of section 3 is chosen.

**Known limits of the S1 writer, to be lifted where a later slice needs it:** the writer writes to its stream synchronously and unbuffered, so the
asynchronous save of S5 must buffer (or write through a pipe) rather than call it from an async method; the page tree is flat (one `/Pages` node holding
every page), which viewers handle well into the thousands of pages and which a balanced tree would improve; and a page's content stream is held in
memory, with a few copies while it is compressed, so one very large page costs several times its own size at completion.
A document's other pages are written as they end and cost nothing more. (S5a: `SaveAsync` buffers the whole file in memory and copies it asynchronously; see the S5a status.)

### Go or stop assessment (21 November 2026, interim: after S1 and S2, before S3)

**Verdict: go, on to S3, with one extraction defect that must be closed in S4 and a narrower claim than "Arabic works".**
The plan places the go or stop point after S2 *and S3's first half*; this is the part of it that can be measured before the text engine
exists, with the shaper called directly. Against the criteria:

- **Effort.** The contingency is "S1 and S2 overrun by more than 50 percent". S1 and S2 were planned at 8 and 10 working days (section 9)
  and were done in about one working session each, including an independent opus review of each and the fixes it found. No overrun.
- **Extraction in logical order, measured** (`ShapingSpikeTests`). Single-font, single-direction corpus lines of Arabic, Hebrew (with
  points), Devanagari (conjuncts, matras, nukta, reph), Bengali, Tamil (split vowels) and Thai are shaped by HarfBuzz, drawn with the S2
  font engine and read back with PdfPig: **28 lines, every one byte-exact**, accepted by qpdf, and every drawn code leads to the glyph
  the shaper chose (the glyph map is read back from the file). Method: glyphs are written in logical (cluster) order, each at the shaper's
  position; a cluster's text is attached to its first glyph; a glyph drawn for a second text gets its own code (a CID above the glyph
  count, sent to the glyph by `CIDToGIDMap`), so `ToUnicode` has one text per code. A first attempt with one `ToUnicode` entry per glyph
  failed 17 of the 29 lines for exactly that reason.
- **What the measurement does not cover.** (1) Mixed direction and numbers inside Arabic (bidi, the second half of S3): `arabic-indic-digits`
  is left out because it is drawn backwards without bidi. (2) Lines that need a second font (`bengali-reph-prebase`, the Hebrew maqaf line, the
  long unbreakable lines, `indic-numerals`): the Bengali reordering case among them. (3) **Nothing was rendered**: the places of the
  glyphs are the shaper's output, not a checked result; the image comparison of section 8 should move up to the moment S4 draws a page.

**Open defects found by the review of this step (the reason the verdict is not an unqualified go):**

1. **pdf.js** (Firefox's viewer) turns an empty `ToUnicode` entry into the code itself, so the continuation glyphs of a cluster (the dots of
   an Arabic letter, a Bengali vowel part, a Thai mark) copy as junk: measured in the pdf.js that ships with Playwright's Firefox, on most
   Arabic, Bengali, Tamil and Thai lines. PdfPig and poppler accept the empty entry. Omitting the entry (as before) gives the same result in
   pdf.js. The claim that "no reader needs `ActualText`" is therefore **not** established. To decide in S4 with pdf.js as a test-time reader:
   an `ActualText` span around the cluster, a mapping of continuation glyphs to an invisible character, or both. Until then pdf.js copy of
   these scripts is a known defect, and the release checklist (copy Arabic out of three viewers) must pass before 0.1.0.
2. **Readers that order by position** (poppler's default mode, which Evince and Okular use for copy) read these lines wrongly even when the
   direction is uniform, because every glyph has its own `Tm`: spaces appear inside words, Hebrew points move before their letter. PdfPig
   and poppler's `-raw` (content order) read them correctly. S4 should write glyph runs with `TJ` adjustments, with one `Tm` per run, and the
   measurement repeated with a geometry-ordering reader.
3. The cost of a glyph map is paid by any document in which one glyph is drawn for two texts (a Latin report included); it is written sparsely
   (unused entries are zero) so that it compresses, but the size budget of section 7 must be measured with it.

**Stop condition.** If extraction of mixed-direction lines in logical order cannot be made correct in S3's second half, if the visual check
shows misplaced marks, or if defect 1 has no fix that works in pdf.js, Acrobat, Preview and Chrome together, stop and choose the contingency
of section 3.

### Result of the mixed-direction extraction test (21 November 2026, after S3d)

With the shaping layer in place (bidi levels, script runs, font fallback over the corpus's font lists, HarfBuzz with the text around each run as
context), all 61 corpus lines (English in Arabic, Arabic in English, numbers, nested quotes, parentheses in Hebrew, Bengali reph and pre-base vowels,
four fonts in one line) are drawn with their runs at the places the bidi order gives and their glyphs written in logical order. **Verdict: no stop
condition is triggered; go to S4.** What each reader makes of the same 61 files:

| Reader | Result |
|---|---|
| PdfPig (content order) | 61 of 61 byte-exact, in the test suite |
| pdf.js (Firefox's viewer) | logical order and mixed direction correct; 27 of 61 clean, **every other line differs only by junk characters from codes whose `ToUnicode` entry is empty**; a first experiment mapping those codes to U+034F removed the junk (the next section measures it properly and keeps it) |
| xpdf `pdftotext` 4.06 default (position order) | about 36 of 61 exact once the embedding controls (U+202A to U+202C) it adds itself are removed and a no-break space is read as a space (23 of 61 without that), about 40 if combining marks may stand before their base, 48 not counting spaces; the rest differ by spaces inside clusters (one `Tm` per glyph) and by spaces lost or moved where the direction changes |
| xpdf `-raw` | spaces between letters (one `Tm` per glyph) |

So the remaining work is exactly the two open defects, now with a measuring harness (`PDFWRIGHT_DUMP=<folder>` keeps the files, then read them with the readers above):
The first S4 experiments (next section) tried `TJ` runs (worse in xpdf, not adopted) and `ActualText` (ignored by pdf.js, not adopted), and found the pdf.js fix in the mapping of the continuation codes.
The criterion for S4 is stated at the end of the next section, which supersedes the one that stood here ("every row at 61 of 61").

### What the first S4 experiments showed (21 November 2026, `GlyphRunWriter`)

Three ways of writing a shaped run were measured on the same 61 files, and one change to `ToUnicode`:

| Experiment | PdfPig | pdf.js | xpdf default |
|---|---|---|---|
| One `Tm` per glyph, logical order (kept) | 61 | 27 | 36 |
| One `Tm` per run, `TJ` adjustments | 61 | 27 | 29 |
| Glyphs without advance written first in a cluster | 61 | | 25 |
| HarfBuzz cluster level 1 (monotone characters; measured with the earlier writer: no U+034F mapping, no size switch) | 61 | 56 | 44 (joiner removed) |
| `ActualText` around multi-glyph clusters, continuation codes empty | not applicable (PdfPig ignores it) | 27, junk doubled | 35 |
| **Continuation codes mapped to U+034F instead of empty (kept)** | 61 (U+034F removed) | **56** | **44** with the joiner removed, 23 without |

**Relative positioning (22 November 2026).** The glyphs are positioned with a `Td` from the position last written (rounded to five decimals, so that errors do not add up) instead of an absolute `Tm`, keeping one operator and one `Tj` per glyph, in the same order and at the same positions; `Tm` stays for the first glyph of a text object and after the scaled matrices of the mark glyphs (a `Td` after them would be scaled). Measured unchanged: PdfPig 61 of 61 and 3,120 of 3,120 strings, pdf.js 61 of 61 and 3,120 of 3,120 (2 canonical), xpdf 45 of 61, every render golden pixel-identical (xpdf’s position-ordered text is unchanged on the 61 single lines (45 of 61) and on 261 of the 264 corpus documents; in three (arabic-long, bengali-long, layout-arabic-persian-200) it differs, because positions that differ by at most 3e-13 break exact ties between a mark and its base differently in that reader); the 1,000-row table went from 514 KB to 76 KB and the ten-page report from 103 KB to 57 KB.

- **pdf.js:** the junk was the whole problem. With the combining grapheme joiner (invisible, default-ignorable, transparent to Arabic joining) as the text of a code that
  continues a cluster, 56 of 61 lines come out right (ignoring the spaces its text layer does not mark, and the joiner). The lines still wrong then (all five fixed in the next subsection) were lines with stacked marks
  (`arabic-diacritics`, `arabic-kashida`, `arabic-shadda-dagger-alef`, `hebrew-points`), which pdf.js orders by position, and `arabic-persian`, where pdf.js drops
  the zero width non-joiner of "می‌خواهم" although the file maps its code to U+200C. `ActualText` was tried: pdf.js ignores it (and then shows the junk twice), and xpdf
  does not improve (35), so it is not preferred over the joiner. Cluster level 1 seemed to change little (31 files need the joiner instead of 34), but that was measured with the earlier writer, before the U+034F mapping and the size switch of the next subsection. With the final writer, level 1 on all runs costs five Indic lines (xpdf 40 against 45), which is why it applies only to right-to-left runs.
- **The price of the joiner:** every reader now sees U+034F in copied text (34 of the 61 lines carry it). It is invisible and does not break Arabic joining, but it breaks plain
  string matching (`Contains`, grep, tokenizers that keep it inside the word), and because its combining class is 0 it blocks canonical reordering when a position-ordered reader
  puts it between a letter and its own marks (normalised text then differs from the source). This is written into `docs/known-limitations.md`. Fewer continuation glyphs
  (fonts or shaping settings that draw a letter as one glyph) is the way to reduce it; it is not a 0.1 goal.
- **xpdf's position-ordered mode cannot be brought to 61 of 61 by the layouts tried**: it puts a space wherever a glyph is drawn to the left of the end of the glyph before it
  (a vowel sign drawn before its consonant, a mark placed by an offset), and none of the three orders removes that. This is a heuristic of one reader, not a defect of the
  file, so the criterion for S4 is **PdfPig 61 of 61 (byte-exact, joiner removed), pdf.js at 61 of 61 (reached in the next subsection), xpdf not below its floor (44 when this was written, 45 after the next subsection)
  (`XpdfRegressionFloorTests`, which runs where `pdftotext` is installed; the 36 first agreed is the floor measured with the empty mapping)**; poppler's own position-ordered mode
  (Evince, Okular) was never measured, and the viewers that matter to users (Acrobat, Chrome, Preview) are in the release checklist (section 8, point 6). The stop condition's
  "a fix that works in pdf.js, Acrobat, Preview and Chrome together" is read as: the pdf.js fix exists (the joiner), the others are checked at release.

### What reading pdf.js's source showed (21 November 2026, `GlyphRunWriter`, `TextShaper`)

The five lines pdf.js still got wrong after the joiner were explained by its source (6.3.335, which Firefox ships), and fixed:

- **Cause.** pdf.js tests a glyph's text with `/^(\s)|(\p{Mn})|(\p{Cf})$/u`, whose alternation makes a glyph with a non-spacing mark *anywhere* in its text a "zero-width diacritic":
  it skips the position check and is appended to the open text item. pdf.js then runs its bidi over each item and reverses right-to-left items character by character, expecting visual
  order; ours are in logical order (what PdfPig and copying need), so an item that holds several glyphs of a right-to-left run came out reversed. A glyph whose text *ends* with a format
  character (U+200C) is dropped. And a new item is started whenever the `Tf` font size changes.
- **Fix, all three parts needed.** (1) Right-to-left runs are shaped at HarfBuzz cluster level 1 (`MonotoneCharacters`), so each mark has a cluster and a text of its own; left-to-right runs
  stay at the default level, because level 1 there cost xpdf five Indic lines. (2) A glyph whose text holds a mark, ends with a format character, or is a continuation glyph is shown with
  `Tf` at size 1 and a text matrix scaled by the real size (the same drawing); two in a row alternate between sizes 1 and 2, so each starts an item of its own, and the next ordinary glyph
  restores `Tf` at the real size. (3) A cluster text that ends with a format character gets U+034F appended, so pdf.js keeps the glyph.

| Reader | Before | After |
|---|---|---|
| PdfPig (content order, U+034F removed) | 61 of 61 | **61 of 61** |
| pdf.js 6.3.335 (Firefox), NFD, white space, U+034F and bidi controls removed | 56 of 61 | **61 of 61** |
| xpdf `pdftotext` 4.06 default, joiner removed | 44 of 61 | **45 of 61** (`arabic-kashida` now reads exactly) |

pdf.js is at 61 of 61; it is measured by `tools/pdfjs-check` (not part of CI) and the xpdf number is the floor of `XpdfRegressionFloorTests`. The pdf.js comparison is canonical, not byte,
equality: in `hebrew-points` one glyph carries two points (U+05B8 U+05C1) and pdf.js reverses them, and the two orders are canonically equivalent. The size switch and the other costs are in
`docs/known-limitations.md`.

### S4d status: the drawing canvas and images (21 November 2026)

Built, all `internal` (the public surface is S5): `Drawing/Canvas` over a `PdfPageWriter` and a box (the box's left edge and top edge in page space, a width and a height). Its origin is the top left with y down, and
the flip is the one `cm` the canvas writes when it starts (`1 0 0 -1 left top cm`); `End` closes any save still open, and a `Restore` with none open is an error. `Save`/`Restore`, `Translate`, `Scale`, `Rotate` (degrees,
clockwise as seen on the canvas), `Transform`, `Clip` (non-zero or even-odd, until the matching restore), `Fill(Path, Brush)`, `Stroke(Path, Pen)`, `DrawText` and `DrawImage`. `Path` is immutable and built by `PathBuilder`
(move, line, cubic, quadratic raised exactly to a cubic, `Arc` in curves of at most 90 degrees, rectangle, rounded rectangle, ellipse, close; a coordinate that is not finite is an `ArgumentException`).
The canvas tracks the colours, the line style and the opacity it has written and writes an operator only when the value changes; fill and stroke opacity are one `ExtGState` for each (`ca`, `CA`) pair
(`PdfPageWriter.UseOpacity(fill, stroke)`). Text goes through `ParagraphLayouter` with no maximum width and `ParagraphLayout.Draw`, inside a `q` with a local flip back so that the glyphs are not mirrored; the
point is the start of the first baseline, and `Start`/`Center`/`End` place the text's block against it (logical, so `Start` is the right edge of a right-to-left paragraph).
Images (`Images/`): an `ImageSource` is parsed once and written once to a document (`PdfDocumentWriter.GetOrAddShared`, keyed by the object). A JPEG goes in as it came, with `DCTDecode`, after its markers have
been walked (baseline, extended and progressive Huffman, 8 bits; 1, 3 or 4 components; an Adobe CMYK file gets `/Decode [1 0 1 0 1 0 1 0]`; arithmetic coding, 12 bits, lossless and any file that is cut short
are refused). A PNG is read by an own decoder (every chunk CRC checked; all five filters; colour types 0, 2, 3, 4, 6 at the bit depths the format allows; Adam7), written as 8-bit device samples that
the document's compression turns into `FlateDecode`, with alpha (or a `tRNS`) as an `/SMask`, and a palette as `/Indexed` unless it has transparency. Limits: 65,535 pixels on a side and 100 million in all, checked
before anything is allocated; damage is an `ImageFormatException`. Left out of the brief: there is no `TextStyle` yet (the canvas takes a size, a brush and a language), and `ArcTo` (tangent arcs) was not built, only `Arc` by centre and angles.
The colour management of a PNG is ignored (`docs/known-limitations.md`).

### S5a status: the public API and the simple block flow (21 November 2026)

Public now, all in the root namespace `Pdfwright`, every member documented (the documentation warning is an error again, `CS1591` is no longer suppressed) and the surface written out in
`tests/Pdfwright.Tests/PublicApi/Pdfwright.PublicApi.txt`, which a test compares with the assembly, so a change of the surface shows in a diff: `Length` (Pt, Mm, Cm, In; arithmetic, comparison, `IsFinite`; no implicit conversion),
`PageSize` (A4, A5, Letter, Legal, custom, `Landscape()`), `Margins` (physical: top, right, bottom, left; Mm, Pt, Uniform), `Color` (the S4d value made public: RGB, gray, `FromHex`, a few constants), `TextStyle`, `TextDirection`,
`TextAlignment` (logical), `FontCollection` and `FontNotEmbeddableException`, the blocks `Heading`, `Paragraph` (with `TextRun`s), `Spacer` and `PageBreak`, `PdfDocumentOptions`, `PdfDocument`, `TextMeasurer` and `TextMeasurement`,
`DocumentDiagnostics` with `MissingGlyph`, and `DocumentLimits` with `PdfLimitExceededException`.

**What it does.** `Block` is an abstract record with an internal constructor and a runtime check, so the set is closed (C# cannot check a `switch` over a class hierarchy for completeness, so the flow's `switch` has a throwing default arm for a block it does not know, which only a block added later and not handled could reach); the table, columns, callout and drawing blocks of S5b and S5c are added as further sealed records.
The flow (`Document/DocumentRenderer`) lays blocks from the top of the content area down; a paragraph that does not fit is split between its lines (no widow or orphan control yet); a heading is kept with the first line of the block after it
(it moves to the next page when it, the space before the next block and that line do not fit; at the top of a page it is not moved, and space before is dropped there); a spacer is always kept, at the top of a page too (at the start of the document and after a page break), and cut at the bottom; a page break ends the page, two in a row leave a blank one.
A line break of any kind inside a run (`\n`, `\r\n`, U+2028, U+2029, U+0085) is a line break inside the paragraph. An empty document is one blank page.

**Runs of different styles in one line** needed the layout engine to change, not only the block layer: `ParagraphOptions` takes `Styles` and `Spans` (internal). `TextShaper` chooses fonts for each grapheme cluster from the font order of the style the cluster
is in (a cluster takes the style of its first character), ends a run where the font order, size or language of the style changes (not the colour), and shapes each run with its style's language; `ParagraphLayouter` measures each run at its style's size, takes a line's ascent and descent from its largest run, and
`ParagraphLayout.Draw` writes each run at its size, sets the fill colour of each stretch of glyphs of one colour (the colour of a glyph is that of the span its cluster starts in), and can draw a range of lines (the flow uses it to split a paragraph). A paragraph with no styles behaves exactly as before: the 61 corpus lines and all earlier tests are unchanged.

**Fonts.** `FontCollection.Add` parses once (family from name id 16 then 1, weight from OS/2 `usWeightClass`, italic from `fsSelection` bit 0, else `head.macStyle`), refuses fonts whose `fsType` forbids a subset (a typed `ArgumentException`, judged by the least restrictive usage bit), refuses CFF fonts, keeps a font added twice once,
and is thread-safe by copy on write: a save works from the snapshot taken when it began. The order of fonts for a style is the family first, then the others, each group ordered by italic match, then by nearest weight, then by the order added; per cluster the first font that covers it draws it.
There is no synthetic bold or italic.

**Saving.** `Save` and `SaveAsync` take a stream that need not seek and is not closed. `Save` and `SaveAsync` make the whole file in memory first (`SaveAsync` on a thread-pool thread, and then copies it asynchronously with the token, so the synchronous writer never touches the caller's
stream) and the memory held is about the size of the file (the pipe alternative would hold one page, but needs a thread blocked on the pipe; it can replace the buffer later without changing the API). The token is checked between blocks and pages and while copying. A document is not thread-safe: adding or saving while a save runs throws
`InvalidOperationException`; a document can be saved again, and with `Deterministic` the second file is the same bytes as the first. `Limits.MaxPages` is checked before a page is started and `MaxBytes` after each page and again when the file is complete (the fonts are written last); both throw `PdfLimitExceededException` with `Limit` = `DocumentLimit.MaxPages` or `MaxBytes`, and nothing has been written to the stream.

**Deterministic output.** The date is `Creation` whenever it is set; a deterministic document without it has no date (no clock is read) and any other the time of the save; and the `/ID` is the hash of the content. The Flate bytes depend on the runtime's zlib, so the option `Compress` (default on) exists: off, a file is the same bytes on every .NET version, which is what a golden file needs.

**Deviations from the sketch of section 5, and why.**

1. `TextMeasurer` is an instance made over a `FontCollection` (`new TextMeasurer(fonts, defaultStyle).Measure(text, style, maxWidth, direction)`), not a static method: a `TextStyle` names a family, not a font, so the measurer needs the collection. It uses `BlockText.Layout`, the function the flow uses.
2. `TextStyle` has all properties nullable (an unset property is inherited from the paragraph, then the document's default style, then the library's defaults) so that a run can say "bold" and nothing else; `Size` is a `Length`.
3. `Limits` is the type `DocumentLimits` and the option is `PdfDocumentOptions.Limits`; `Creation` is the sketch's "dates from Creation"; `Compress` and `HeadingStyles` are added (the headings' size and weight come from the options, as asked, with a default scale of 2.0, 1.6, 1.3, 1.15, 1.0, 1.0 bold).
4. The internal `TextMeasurer` of S4 is now `ParagraphMeasurer` and the writer's `PdfDocumentOptions` is `PdfWriterOptions`, because the public names are the sketch's; the internal `Color`, `TextDirection` and `TextAlignment` became the public types.
5. `Header`, `Footer` and `PageContext` (and `Table`, `Columns`, `Callout`, `Drawing`, keep-together) are not here: S5b and S5c.
6. Diagnostics report each missing character once for each language and page (at most 1,000 entries); a font that forbids embedding is refused at `Add` with an exception rather than being a diagnostic, as section 4(h) says "refused".
7. A family that is not in the collection is substituted (the fonts of the collection in their order of preference) but no longer silently: `DocumentDiagnostics.MissingFamilies` lists it. `DocumentDiagnostics`, `MissingGlyph` and `TextMeasurement` are only read, so their constructors are internal, and `PdfLimitExceededException.Limit` is the enum `DocumentLimit` (`MaxPages`, `MaxBytes`), not a string.
8. Values are checked where they are set, with the public property as `ParamName` (`Paragraph` and `Heading` alignment, direction, spaces and line spacing; `TextStyle` size and weight; the options' direction, margins, page size, limits and heading styles), and `Length.Pt`, `Mm`, `Cm` and `In` refuse a number that is not finite, so a bad document fails where it is made and not in `Save`. `HeadingStyles` is copied when it is set. `Paragraph` and `Heading` compare their runs one by one (they are records, and a list of runs would be compared by reference).
9. `MaxBytes` is checked after every page and once more when the file is complete (the fonts are written last, after the last page), and `Save`, like `SaveAsync`, builds the file in memory first, so a limit that is hit leaves the caller's stream untouched. `Save` therefore holds the whole file too; the 64 KB write-through buffer of the first S5a is gone.
10. A spacer is always honoured, at the top of a page too (it was dropped there); only the space before a paragraph or a heading is dropped at the top of a page. A spacer after a page break moves the text of the new page down. A heading is kept with the first line of the next block and with the space before it.
11. The colour of a run does not end a shaping run: runs are split where the font order, size or language changes, and a coloured word (an Arabic word with one red letter) is shaped, and so measured, as one. `ParagraphLayout.Draw` finds the colour of each glyph from its cluster and writes a run in parts, in logical order. The 61 corpus lines and the earlier tests are unchanged.
12. `FontCollection` is `IDisposable` (it holds native HarfBuzz copies of the fonts and is meant to live long); after `Dispose`, adding, measuring and saving throw `ObjectDisposedException`. `TextMeasurer` takes the `language` and `lineSpacing` of the document it is to match, and says that a measurement matches a document only when made the way the document is.
13. A font whose `fsType` has several usage bits is judged by the least restrictive one, as OpenType says: 0x0006 and 0x000A may be embedded, 0x0002 alone may not; no subsetting (0x0100) and bitmap only (0x0200) still refuse. `FontNotEmbeddableException.ParamName` is `fontData`, and its constructor is internal.
14. `Creation` is written whenever it is set (the sketch said "used only with `Deterministic`", which the code never did); `Deterministic` without `Creation` writes no date, and without both the date is the time of the save.
15. The set of blocks is closed by an internal constructor and a runtime check, not by the compiler: C# cannot check a `switch` over a class hierarchy for completeness, so the flow's `switch` throws for a block it does not know. Section 5, point 1 said the compiler would check the adapter's mapping; a test must.

### S5b status: the table (21 November 2026)

Public now, in the root namespace, documented and in the public API snapshot: the block `Table`, with `TableColumn`, `ColumnWidth` (and `ColumnWidthKind`), `TableRow`, `TableCell`, `Borders` and `BorderLine`; `DocumentLimits.MaxTableRows` (with `DocumentLimit.MaxTableRows`),
`Table.MaxRows` and `DocumentDiagnostics.TruncatedTables` (with `TruncatedTable`). `Table` is one more sealed member of the closed `Block` set; the flow's `switch` handles it (`Document/DocumentRenderer.Tables.cs`).

**What it does.**

- *Widths.* `ColumnWidth.Fixed(Length)`, `ColumnWidth.Fraction(weight)` and `ColumnWidth.Auto` (the default). The columns share the content width in this order: fixed columns take their length; each automatic column has a minimum (its widest word, measured with
  the layout engine, plus the cell padding) and a want (its longest unbroken line plus the padding), both taken over the header and the first 100 rows; a fraction column has no content minimum, only its padding. If the minimums of all columns do not fit, the room is shared by water-filling (a column whose minimum is within an equal share of what is left keeps it; only the wider ones are cut down, to an equal share of what the others leave, until stable), so one very long word narrows its own column and not the short ones, and text wraps (a word wider than its cell is cut between characters, as in any paragraph). The minimum of a fraction column is the table's `CellPadding`; a cell with a bigger padding of its own falls back to a 1-point inner width. When `MaxRows` or `DocumentLimits.MaxTableRows` is set, only the rows that will be drawn are measured, and at most one row more than that is read.
  Otherwise the automatic columns grow towards their wants (in proportion to how much each wants, when there is not room for all) and the fraction columns share what is left by weight (like CSS `fr`). A table without a fraction column is as wide as its columns; with one it fills the content width. The sample is the only thing buffered: when no column is automatic nothing is held, and when one is, the first 100 rows are read, measured, and then handed on to the flow, which continues the one enumeration.
- *Streaming.* `Rows` is an `IEnumerable<TableRow>`, enumerated once for each save (of each time the table is added), at save time, never materialised: a row is laid out, drawn and dropped. Tested with a counting enumerable and with 100,000 lazy rows whose early rows are collected while the save is still running.
- *Pagination.* A row goes whole to the next page when it does not fit. A row taller than a page (what a page can hold under the repeated header) is split between lines of its cells at one height for all cells, and the pieces continue on the following pages with the cell's padding at the top of each. The header rows are repeated on every page when `RepeatHeader` (default `true`; a header taller than half a page is not repeated, and is then drawn like any other rows). A table that starts on a page with content moves to the next page when its header and one row (or the first line of a taller row) do not fit.
- *Fills and borders.* `HeaderFill`, `ZebraFill` (the second, fourth, ... body row, counted over the pages), a row's `Fill`, a cell's `Fill`, in that order of precedence from the least to the most specific. `Borders` has four edge kinds (`OuterHorizontal`, `OuterVertical`, `InnerHorizontal`, `InnerVertical`, each a `BorderLine` of width and colour, or null) and the presets `None`, `All`, `Horizontal` and `Outer`.
  Each page's part of a table is closed with the outer horizontal line and opened with it again.
- *Direction.* `Table.Direction` (else the document's) decides the order of the columns: right to left puts the first column at the right edge and the table at the right of the content area. `TextDirection.Auto` has nothing to look at for columns and means left to right; the text of each paragraph still resolves its own direction, and an explicit table direction is the base direction of the paragraphs that name none.
- *Limits and diagnostics.* `DocumentLimits.MaxTableRows` stops the save with `PdfLimitExceededException` (`Limit` = `DocumentLimit.MaxTableRows`) when a table has more body rows; `Table.MaxRows` draws up to the limit and lists the table in `DocumentDiagnostics.TruncatedTables` (the block index and the limit), because plan section 4(h) wants "a table that exceeded a limit" reported as data, and the adapter's "N more rows" line is its own.
  When both are set the smaller applies. A missing glyph or family in a cell is reported like any other, with the page the cell is drawn on.

**Deviations from the sketch of section 5, and why.**

1. `header:` may be one `TableRow` or any sequence of them (three constructors, all with the parameter names `columns`, `header` and `rows`); the header rows are copied when the table is made, `Rows` is kept as given.
2. `IAsyncEnumerable<TableRow>` is not accepted. `SaveAsync` runs the synchronous renderer on a worker thread, so consuming an async sequence would mean blocking a thread on it; it can be added when the document layer has an asynchronous path.
3. Two tables are equal when their columns, header rows and settings are equal and their `Rows` are the same object: comparing rows would enumerate them.
4. `Borders` is a record of four edge kinds, not a flag enumeration, so that a width and a colour can be given per kind; `Borders.Horizontal` draws the horizontal lines (top, between, bottom) and no vertical ones.
5. Padding is physical (`Margins`, as for the page), because a cell's left and right padding are rarely different; a cell's text alignment is logical. A paragraph in a cell keeps its own alignment when it is not `Start`; otherwise the cell's, then the column's, applies.
6. `Table.Style` and `Table.HeaderStyle` (bold by default) are added so that a whole table or its header can be styled without styling every cell; the order is document default, table, header (in the header), cell, paragraph, run.
7. No cell spans, no vertical alignment of cells (text is at the top); `Columns`, `Callout`, header and footer, drawing and keep-together are S5c (below).
8. A heading before a table is not kept with its first row (the keep rule looks at text blocks only).

### S5c status: columns, callout, drawing, image, keep-together, header and footer (21 November 2026)

Public now, in the root namespace, documented and in the public API snapshot: the blocks `Columns`, `Callout`, `Drawing` and `Image`; `Paragraph.KeepTogether` and `Callout.KeepTogether`; `PdfImage` (`FromBytes`, `PixelWidth`, `PixelHeight`, `HasTransparency`);
the canvas `DrawingCanvas` with `DrawingPath`, `DrawingPathBuilder` and `DrawingPen` and the enumerations `FillRule`, `LineCap` and `LineJoin`; and `PdfDocumentOptions.Header`, `Footer`, `HeaderOffset` and `FooterOffset` with `PageContext`.
All four new blocks are sealed members of the closed `Block` set; the flow's `switch` handles them (`Document/DocumentRenderer.Blocks.cs`). The internal namespace `Pdfwright.Drawing` of S4d was renamed `Pdfwright.Graphics`, because the block the sketch calls `Drawing` needs the name.

**What it does.**

- *Items.* Every block that is not a table is built into an item with a height that is known when it is laid out (everything is laid out at a width that is known): a paragraph is an item that can be split between its lines; a drawing, an image, a row of columns, a spacer and a callout inside another block are whole.
  Between two items the space after the first and the space before the second are both kept (as in the top-level flow); the space before the first and after the last item of a container is dropped. The new blocks have `SpaceBefore` (default none, dropped at the top of a page) and `SpaceAfter` (default 0.6 of the document's font size).
- *Columns.* 1 to 4 equal cells (`Gap`, 12 points by default), each a list of blocks that flow down in the cell (paragraph, heading, spacer, drawing, image, callout; not a table, a page break, columns, or a callout that holds columns: one level of columns, checked where the block is made). The row is as high as its highest cell and is **kept together**: it moves to the next page when it does not fit, and a row
  taller than a page's content area fails the save with an `InvalidOperationException` (the choice was to move, and when moving cannot help to fail loudly rather than clip a KPI; nothing has been written to the stream). `Direction` (else the document's) decides the order of the cells: right to left puts the first at the right edge; `Auto` is left to right, as for a table.
- *Callout.* Blocks in a box with `Fill` (light gray by default), `Border` (a `BorderLine` from S5b; 0.75 pt mid gray by default; drawn inside the edge, so the box is as wide as the content area), `Padding` (physical, 8 and 10 points) and `CornerRadius`. It **is split between pages** between the lines of its paragraphs and between its blocks (decided so because a note that is a screen long should not leave a hole), and each page's part is a whole box with its own fill, border, corners and padding. `KeepTogether = true` moves a callout that would be split to the next page; one taller than a page is split anyway.
  A heading is not left at the end of a part, a paragraph with `KeepTogether` that fits a page starts a new part when it does not fit, and a drawing, an image or columns inside move to the next part whole (taller than a page: `InvalidOperationException`). A callout inside a column cell or another callout is not split.
- *Keep-together.* A property, not a wrapper block: `Paragraph.KeepTogether` and `Callout.KeepTogether` (a wrapper would have added a block type to a closed set for what a flag does). A paragraph taller than a page falls back to normal flow. Not on `Table` (its rows are streamed and cannot be measured before they are drawn) and not on `Heading` (kept with the block after it already).
  A heading is now also kept with a drawing, an image, a row of columns or a callout that follows it, by the height of the first line or the whole block.
- *Drawing.* `new Drawing(Length width, Length height, Action<DrawingCanvas> draw)` with `AltText`. The callback runs at save time, once for each place the drawing is placed. `DrawingCanvas` is the S4d canvas made public: **points as `double`** (not `Length`: chart code is arithmetic, and a unit type for every coordinate would be in its way; the origin is the top left, y down),
  `Save`/`Restore`, `Translate`, `Scale`, `Rotate`, `Transform`, `Clip`, `Fill(path, color, opacity, rule)`, `Stroke(path, pen)` and `Stroke(path, color, width)`, `DrawText(text, x, y, style, alignment, direction, opacity)` through the document's fonts and the same engine as paragraphs, and `DrawImage`. Fills and strokes take the pieces
  (`Color`, `DrawingPen`) rather than `Brush` and `Pen` objects, and the path types are `DrawingPath` and `DrawingPathBuilder`, to stay clear of `System.Drawing` and `System.IO.Path`. The canvas is valid only while the callback runs: afterwards every call throws `ObjectDisposedException`, and any `Save` the callback left open is closed. The box is a clip. A drawing is never scaled and never split: wider than the space or taller than a page, the save fails (`InvalidOperationException`).
  A missing glyph or an unknown family in drawn text goes to `DocumentDiagnostics` with the page. `AltText` is kept for tagged output and written nowhere in the file today.
- *Image and `PdfImage`.* `PdfImage.FromBytes` reads and checks a JPEG or PNG with the S4d decoders (an `ArgumentException`, parameter `bytes`, with the reason in the message and no inner exception: the decoder's exception type is internal), once; the same `PdfImage` may be in any number of documents and is written once to each. The `Image` block takes `Width` and/or `Height` (one given: the other follows the proportions; none: the natural size at 96 pixels to the inch), is scaled down in proportion to fit the available width and a page's height, and `Alignment` (logical) places a narrower one.
- *Header and footer.* `Header` and `Footer` are `Func<PageContext, Block?>`, called once for each page in page order. `PageContext` has `Number`, `Count`, `IsFirst`, `IsLast` and `PageSize`. They are drawn **inside the margins** (the body's area never changes): the header's top is `HeaderOffset` below the top edge (default half the top margin) and the footer's bottom `FooterOffset` above the bottom edge (default half the bottom margin);
  a block taller than what the margin leaves, a table or a page break fails the save with an `InvalidOperationException` that names the page and says whether it was the header or the footer (a block that cannot be laid out keeps the original exception as the inner one), and an offset larger than its margin is refused when the document is made. A block's own spaces before and after are ignored. A header or footer may be a paragraph, heading, columns (a logo on one side and a date on the other), callout, drawing, image or spacer; the callback should be a pure function of its argument.
  **One pass, always.** The plan said "two passes only when the footer asks for it"; there is no second pass at all. When a header or a footer is set, a page's body content stream is written as the page ends, as before, and the page's dictionary waits (a reference and its resources are all it holds) until the whole body is laid out; then the callbacks run with the final count and a header stream and a footer stream are written, and the page's `/Contents` is the array header, body, footer.
  So the count costs nothing, a streamed table is enumerated once, and text extraction reads header, body, footer in that order. A document with no header and no footer is written exactly as before. `MaxBytes` is checked after every body and at the end, as for any file; a page's header and footer add to the file after its body.

**Deviations from the sketch of section 5, and why.**

1. `Columns` takes `IEnumerable<Block>` (one block for each cell, the sketch's form) or `IEnumerable<IEnumerable<Block>>` (several); the sketch's `new Drawing(...) { AltText }` and `new Callout(...)` are as sketched, with `Callout(Block)` and `Callout(IEnumerable<Block>)`.
2. The canvas takes `double` points and has no `Brush` or `Pen` type (above). `Path(...)` and `Text(...)` of the sketch are `Fill`/`Stroke` with a `DrawingPath` and `DrawText`.
3. No two passes, as above. `PageContext.Count` is always the final count.
4. The internal namespace of the canvas is `Pdfwright.Graphics`.
5. Links (external URI and internal destinations, section 4(g)) are **not public in S5c**: the writer has them (S1), but a link on a run needs the layout to give the rectangle of the characters of a run on each line it wraps to, and bidirectional runs and runs shaped in parts make that more than a cheap addition. Listed in the known limitations.
6. `Image`, `Columns`, `Callout` and `Drawing` carry `SpaceBefore` and `SpaceAfter`; `Callout` and `Columns` take a `Direction`, so that a right-to-left row is one property.
7. `Image.Source` is the picture (not `Picture`, and not `Image.Image`). `DrawingPen` has value equality (its dashes are compared element by element, like `BorderLine`) and its `Dashes` is a read-only collection.

**Decided after review of the slice.**

- A heading before a block that asks for `KeepTogether` and fits a page (a paragraph or a callout) is kept with the whole of it, not only its first line; when the heading and the whole block do not fit one page it falls back to the first line.
- A column cell narrower than 4 points (a `Gap` that is too wide), a callout whose border and padding leave less than 4 points of width, or no room on a page for its first block, fails the save with an `InvalidOperationException`; so does a callout nested in another callout or in a column cell that is taller than a page (those are never split; only a callout in the flow is).
- `CornerRadius` is cut to half the shorter side of the box it is drawn on, so corners stay circular.
- A `Drawing` draws text in the direction of the `Columns` or `Callout` it is in (their `Direction`, else the document's). The public canvas and path builder check their arguments and report the public parameter names (`x`, `sx`, `width`, ...).
- `DrawingCanvas.DrawImage` stretches the picture to the rectangle it is given; the `Image` block keeps the proportions unless both `Width` and `Height` are set. `Image` and `Drawing` are also names in `System.Drawing`: alias one (`using Image = Pdfwright.Image;`) when both namespaces are imported.

### S6a status: script conformance by extraction, golden files, failure cases and property tests (21 November 2026)

Points 1, 2, 3 and 7 of section 8 are done. Point 5 (PDFium visual goldens) is S6b, below; the native-speaker review and the viewer checks are release-checklist items for the maintainer (`RELEASING.md`), not tests.

**The page corpus** (`tests/Pdfwright.Tests/Corpus/pages/*.txt`, format in `Corpus/README.md`; the 61 single-line cases and their tests are untouched). Ten groups: Latin Extended (Turkish with dotless ı and the İ/i pairs and `Language = "tr"`, German, Polish, Vietnamese), Greek,
Cyrillic (Russian, Ukrainian, Bulgarian), Arabic (joining, lam-alef, diacritics, tatweel, Arabic-Indic and Western digits, numbers inside Arabic text), Hebrew (points, final forms, maqaf, gershayim), mixed direction (English in Arabic, Arabic in English, nested quotes, numbers with signs, a phone
number, three scripts in a line, a paragraph that begins with U+200F), Devanagari (conjuncts, reordering matras, nukta, reph), Bengali, Tamil and Thai (stacked marks). For each, a short page (a heading and four to six paragraphs) and a long page (a heading, paragraphs and a table of 100 rows in three
columns, which runs over three to five pages and repeats its header), all through the public API with the nine Noto fonts in one `FontCollection`. **Every script of the plan has a font in the test project; nothing is listed as not covered for lack of a font.** The six script fonts have the Regular weight only.

**Extraction acceptance** (`ScriptConformanceTests`). PdfPig reads each document back; each heading, paragraph and table cell must be equal to its input in logical order. The normalisation is exactly: remove U+034F and turn every run of `char.IsWhiteSpace` characters into one space; nothing else (U+200F, U+200C and the order of marks are
compared as they are). The text is made from PdfPig's letters, with a marker where the geometry shows a line change or a gap of more than 0.45 em on one line (the next table cell, the jump between runs). A string passes when it is found *whole*, from where the previous one ended, with a space where it has a space, a plain space nowhere else, and a space, a line break, a gap or the end of the page on both sides; a line break may fall inside a string only between grapheme clusters (Thai), a gap only inside a string. So a swapped pair, a changed letter, extra text, a moved space, a lost space and a character that moved into the next string or the next cell all fail (unit tests of the matcher); the table's header repeated on a later page is counted apart and must occur once for each page after the first. **Geometry** (`Directions`): in the order of the stream, two letters of a right-to-left script (Hebrew, Arabic) that follow each other on a baseline must have a falling x and two letters of Latin, Greek or Cyrillic a rising x, which is what a run mirrored in place or a line that skipped rule L2 of UAX 9 would break while the text, in logical order, stays equal (marks, zero-advance glyphs, digits and the Indic and Thai letters, which are reordered inside a cluster, are left out, and so are pairs more than five em apart, which are table cells). What the acceptance proves: the text is the input in logical order, its spaces and string boundaries are where the input has them, and the glyphs of each directional run are laid out in the direction of the run. It does not prove the visual order of Indic clusters or the vertical position of marks (the fuzz test bounds that: a glyph is inside the margins' box give or take half an em). The corpus is 3120 strings counting the repeated rows of the long pages and **308 distinct strings** (arabic 30, bengali 29, cyrillic 32, devanagari 30, greek 28, hebrew 30, latin-extended 37, mixed 35, tamil 29, thai 28); the stricter comparison above reads all 3120 back.

| Group | PdfPig (short + long) | pdf.js (Firefox 157.0) |
|---|---|---|
| latin-extended | 313 of 313 | 313 of 313 |
| greek | 310 of 310 | 310 of 310 |
| cyrillic | 311 of 311 | 311 of 311 |
| arabic | 313 of 313 | 313 of 313 |
| hebrew | 312 of 312 | 312 of 312 (2 only up to canonical equivalence) |
| mixed | 317 of 317 | 317 of 317 |
| devanagari | 312 of 312 | 312 of 312 |
| bengali | 311 of 311 | 311 of 311 |
| tamil | 311 of 311 | 311 of 311 |
| thai | 310 of 310 | 310 of 310 |
| **all** | **3120 of 3120** | **3120 of 3120** |

The pdf.js column was measured by hand on 21 November 2026 (not a CI test): `PDFWRIGHT_DUMP=<dir>` keeps the twenty documents and their expected strings, and `node tools/pdfjs-check/pdfjs-check.js <dir> tests/Pdfwright.Tests/Corpus` reads them with Firefox 157.0's built-in pdf.js
through Playwright (the harness scrolls each page into view and joins the text layers in page order; it compares like the PdfPig test, and in NFD when a document is not byte-equal). The two strings that are only canonically equal are the case the known limitations already name (Hebrew points, here U+05B8 U+05C1 and U+05B5 U+05BC, whose
order pdf.js reverses). qpdf is not installed on the machine this was written on; the tests run `qpdf --check` on every corpus document (and on the failure cases and two paginated tables), and CI does.

**Failure cases** (`FailureCaseTests`, for each script): text with a glyph that no font has gives `MissingGlyphs` with the character, the language and the page (the text is on page 2, the language is the paragraph's) for exactly the characters that the font has no glyph for (the test reads its cmap with HarfBuzz), and the document still saves and passes qpdf; a font whose `fsType` forbids embedding (0x0002 and 0x0200),
for each of the nine fonts, is refused with a `FontNotEmbeddableException` that names the family and the permission, and adds nothing to the collection; a family that is not in the collection is listed in `MissingFamilies`, and the text is drawn by the other fonts and reads back.

**Golden files.** One for each script group (its short page) and the README's report, in `tests/Pdfwright.Tests/Golden`, `Deterministic = true`, **`Compress = false`** (the bytes of Flate depend on the runtime's zlib; without it they depend only on the library and the fonts), compared by SHA-256 (`Golden.Check`). They pass on all four runtimes on Windows;
the other two systems are proved by CI. A failure prints both hashes, saves the new bytes and names `tools/Pdfwright.GoldenDiff`, a console program in the solution that prints which objects differ and, for a changed object, the lines of its dictionary and of its decoded content stream (one operator a line), or the first differing offset of a binary stream. Goldens are regenerated on purpose with
`PDFWRIGHT_UPDATE_GOLDENS=1` (or `PDFWRIGHT_UPDATE_GOLDEN=1`, refused when `CI` is set; `-method "*golden_file*"` runs the twelve golden tests, `three-pages.pdf` included) and committed **in a commit of their own** (`Golden/README.md`); on CI a failure also leaves the new files as the artifact `golden-actual-<os>`. A smoke test runs `Pdfwright.GoldenDiff` on two files (exit 0, 1 naming a changed object, 2 naming a missing file). Two runs of any corpus document, compressed or not, are the same bytes (tested).

**Property and fuzz tests** (`PropertyTests`; fixed seeds, a few seconds): 60 random documents of mixed-script paragraphs from the corpus alphabets (random sizes, weights, directions, alignments, page sizes down to 200 points wide, a table in half of them) never throw and every glyph that PdfPig finds has its bounding box inside the media box and within half an em of the margins' box (a mark pushed an em down is caught); 12 random
tables of 30 to 90 rows (with a few rows taller than a page) on a 300-point page keep every word of every cell exactly once, every row that fits a page on one page, the rows in order, and the header exactly once on every page from the first to the last page that has rows, and at least three tall rows really were split between pages. `PDFWRIGHT_FUZZ_SEEDS=N` raises the number of seeds for a longer run by hand (6000 seeds of the first, 400 of the others, were run once without a failure).

**Library bug found and fixed.** The fuzz test, on seed 2517 of a longer run, found a glyph 1.3 points below the page: a Hebrew point that follows a Latin digit has no glyph in the digit's font, so it is drawn as `.notdef`, and HarfBuzz's fallback positioning of marks put that box below its base by its own height (0.8 to 1.6 em). A `.notdef` glyph now keeps the baseline
(`TextShaper`; the missing glyph is still reported in the diagnostics, and the known limitations say what it looks like).

**What is not covered.** Visual correctness (S6b); whether the words are right for a native reader and how Chrome, Edge, Acrobat Reader and macOS Preview show the files (the release checklist); Thai word breaking (a long run of Thai breaks between grapheme clusters, as documented); bold and italic of the six script fonts (not in the test project); a CJK, emoji or colour font; the golden bytes on Linux and macOS until CI has run.

### S6b status: visual golden files through PDFium (21 November 2026)

Point 5 of section 8 is done (the native-speaker review stays a release-checklist item). Every page of every golden document (the ten short script pages, the README report with 3 pages: 13 pages; `three-pages.pdf` is not drawn, see below) is drawn by PDFium and compared with a stored PNG in `tests/Pdfwright.Tests/Golden/Renders` (`RenderGolden`; the tests are `*_looks_like_its_stored_render`; they build the document again, so a change of the library fails the byte test and this one).

**Renderer (test-only; `src/Pdfwright` gains nothing).** `PDFtoImage` 5.4.0 (published 16 August 2026; David Sungaila), which calls PDFium and draws with SkiaSharp. Licences read from the `.nuspec` of each restored package: PDFtoImage **MIT**; `bblanchon.PDFium.Win32`, `.Linux`, `.macOS` 152.0.7961 (Benoit Blanchon) **Apache-2.0**
(the binaries are PDFium builds, BSD-3-Clause and Apache-2.0, bundling FreeType under the FreeType Licence); `SkiaSharp`, `SkiaSharp.NativeAssets.Win32`, `.macOS` and `.Linux.NoDependencies` 4.150.1 (Microsoft) **MIT**. Nothing is GPL, LGPL or unlicensed; the comments in `Directory.Packages.props` say the same. PDFtoImage ships assets for net8.0, net9.0 and net10.0 (net11.0 takes the net10.0 one), `linux-x64` and `linux-arm64` (glibc and musl) PDFium binaries,
the Skia Linux assets that link nothing but glibc (`NoDependencies`), and `osx-arm64` and `osx-x64`: nothing was installed on the runners, and `ubuntu-latest`, `windows-latest` and `macos-latest` (Apple silicon) all load it.

**Rendering and comparison.** 96 dpi (A4 is 793 x 1122 pixels), anti-aliasing on, white background, no annotations, stored as RGB PNG, 8 bits per channel (13 files, 703,952 bytes in all; with the three pages of `three-pages.pdf` it was 16 files and 753,108 bytes; the first version stored luminance only, 352 KB, and could not see a colour). A pixel differs when no pixel of the other image within one pixel is within 40 (of 255) of it on every colour channel (a shifted edge or another anti-aliasing is not a difference; a moved, mirrored or missing glyph, a light rule that is gone or another colour is); a page fails when **more than one pixel** differs, in total. The first rule, a share of 0.05 percent of the inked pixels, was an error of the first review: on a page of 20,000 inked pixels it let 10 differing pixels pass, which is a missing Arabic dot or a moved mark, and at threshold 96 a table rule of luminance 179 (672 pixels) was not ink at all. On failure `pdfwright-actual-<name>-p<n>.png` and `pdfwright-diff-<name>-p<n>.png` (stored page faded, differences red) go to `PDFWRIGHT_ACTUAL_DIR`, and CI keeps them with the artifact `golden-actual-<os>`. `PDFWRIGHT_UPDATE_GOLDENS=1` regenerates the PNGs (refused under `CI`), `PDFWRIGHT_REQUIRE_RENDER=1` (set in `ci.yml`) turns a renderer that cannot load into a failure, otherwise the tests skip with the reason. The PNGs were made on Windows (`Golden/README.md`).

**Measured basis of the tolerance.** PNGs made on Windows (x64, net10.0), compared on the first CI run with the render of `windows-latest`, `ubuntu-latest` and `macos-latest` (arm64): **0 differing pixels on every one of the 16 pages on all three systems** (measured with the first, looser rule; the figures for the rule below are in "Cross-OS measurement with the final rule"), so the cross-platform noise is 0 pixels and the limit of one pixel is a margin for a future PDFium, not for noise. Nothing in the measurement supports a share-of-ink limit; a real regression is a handful of pixels (one mark) as well as thousands (a mirrored run), and only an absolute limit sees both.

**Sensitivity proof** (temporary changes of `GlyphRunWriter`, reverted; "extraction" is `ScriptConformanceTests` and `Directions`, which passed all before):

| Change | Render test | Byte golden | Extraction |
|---|---|---|---|
| every zero-advance glyph moved right by 1/3 em | fails: arabic 6.3%, thai 5.8%, bengali 3.4%, devanagari 2.7% of the ink (mixed 1.9% and hebrew 0.28% are below 2 percent but over the final 0.05 percent limit, so they fail now) | fails (6 pages) | **passes** |
| x mirrored inside each right-to-left run | fails: arabic 27%, hebrew 23%, mixed 16% | fails (3) | fails (6 tests: the direction check of S6a catches it) |
| the x of two glyphs of one cluster swapped | fails: devanagari 25%, bengali 22%, thai 18%, tamil 16%, arabic 5.1%, mixed 2.2%, hebrew 0.07% | fails (7) | **passes** |

Two of the three changes read back correctly through the extractor, so only the pictures (and the bytes) catch them; the byte goldens catch every change of the output but say nothing about whether it looks right, which is what the PNGs are for when a change is meant.
**Sensitivity of the comparator on one mark (`RenderCompareTests`, net10.0, every connected ink component of every golden page).** Erasing one mark (the mark and its 1-pixel surround painted white): **4,119 of 4,119 marks of 2 pixels or more detected (100 percent)** on the 13 pages (the 423 marks of `three-pages.pdf`, measured before its render was dropped, were all detected too). Moving one small mark (2 to 15 pixels) 5 pixels to the right: **519 of 519 detected (100 percent)** on the script pages and the README report (Arabic 127, Bengali 17, Devanagari 29, Hebrew 65, mixed 128, Tamil 57, Thai 24, README p1 72). Not covered by design: a mark of a single pixel (6 of them: corners of table rules on the README page and two specks on the Hebrew page) is one pixel, and one pixel differing is tolerated; erasing it passes, moving it differs in 2 pixels and fails. The limit started at 2 pixels in total with a cluster rule; the sweep showed a moved single-pixel speck (2 isolated differing pixels) passing, so the limit is 1. 0 differing pixels were measured on all three systems for the looser rule; the final rule is measured below.

The comparator also fails: a blank page, an all-black page, a shift of 2 pixels in x or y, a light rule of luminance 179 that is gone (the first rule passed it with 0 differing pixels), and a black rectangle drawn red, blue, green, mid blue or grey. It passes: identical pages and a shift of 1 pixel. It gives the same count with the two pages swapped.

**Cross-OS measurement with the final rule (first CI run, 21 November 2026).** Windows, Linux and macOS: 0 differing pixels on all 13 pages with embedded fonts (the 10 script pages and the 3 README pages), at channel threshold 40 and a limit of 1 pixel. `three-pages.pdf` (not embedded Helvetica): Linux 48, 106 and 1,109 pixels, macOS 54, 114 and 1,126 pixels on its three pages, the largest touching group 2 to 10 pixels: a different substitute font, not noise, so the page was taken out of the render goldens rather than the limit loosened.

**Mutations of `GlyphRunWriter` with the final rule (net10.0, reverted).** Every zero-advance glyph moved right by 1/3 em: fails 6 of 12 pages (arabic 1,129 pixels, bengali 452, devanagari 435, mixed 698, thai 598, hebrew 87); x mirrored inside each right-to-left run: fails 3 (arabic 7,273, hebrew 6,078, mixed 7,903); the x of two glyphs of one cluster swapped: fails 7 (devanagari 5,435, tamil 3,771, bengali 3,593, thai 1,841, arabic 1,309, mixed 1,050, hebrew 11, the weakest case). All pages the change touches fail.

**Time.** The eleven render tests take about 1.7 seconds on one runtime (loading PDFium included) on the Windows machine of this work; the three CI jobs took 2 m 16 s (ubuntu), 2 m 54 s (windows) and 3 m 8 s (macos) in all, restore, build and every other test included.

**Not covered.** A PNG is the picture of one PDFium version at one resolution (PDFium 152 here; an update of the package may move glyph edges and is a reason to look at the diff images, not a reason to widen the tolerance); a colour or light-grey text page beyond the rectangles and rules of the comparator tests (the comparison is per channel, so it would be seen, but no golden page has colour); `three-pages.pdf` is not drawn: it names Helvetica without embedding it, so PDFium draws the substituted font of each system, and the first CI run of the final rule measured it differing by 48 to 1,126 pixels per page on Linux and 54 to 1,126 on macOS (Windows 0, where the PNGs were made), while the 13 pages with embedded fonts measured 0 on all three; the earlier luminance rule (threshold 96) had hidden this; only the short pages of the scripts are drawn, not the long ones.

### S7 status: documentation, samples and the release checklist (21 November 2026)

**README** (what it is and is not, install with the note that nothing is published, the quick start, the key concepts, the scripts with the measured read-back results, the comparison table, the limitations summary, links), **`docs/guide.md`** (the user guide, one section for each area of the
public API), **`samples/Pdfwright.Samples.Report`** (a multi-page report: heading, KPI columns in a callout, a bar chart, a paginated table, header and footer "Page x of y", Arabic and Latin text; in `Pdfwright.slnx`, so CI builds it; the Noto fonts are linked from the test project, not copied, and their OFL licence texts are copied beside the program),
`RELEASING.md` (the maintainer's checklist for 0.1.0 in Sheetwright's form) and the CHANGELOG (one coherent `[Unreleased]` section for the whole 0.1.0 surface, not one entry for each slice).

**How the documentation code is tested.** Every code fence of the README and of every `docs/*.md` file (any indent, any info string) has a line `<!-- snippet: name -->` above it (`<!-- snippet: none -->` for a block that is not code that runs: a shell command, an output, a diagram, and in this plan a sketch), and `tests/Pdfwright.Tests/Documentation/DocumentationSnippetTests.cs` holds the code between `// <snippet name>` and `// </snippet>`, which the tests compile and run (the quick start saves a report and reads it back; the others
build a document and save it, mostly through qpdf and PdfPig). `DocumentationSyncTests` fails when a fence has no marker, a snippet is in no document, or the two differ (compared line for line after the snippet's indent is set aside; the leading `using` lines of the block and the snippet, written `// using X;` at the top of the snippet, must be the same set; a line may differ only where the test lists that exact pair of lines with a reason, which is how a path or a stream of the test reads as the reader's own). So the code a reader copies is code that compiled and ran.
`ReportSampleTests` compiles `ReportSample.cs` into the test project and builds, saves and reads back the sample report. Both run on all four target frameworks.

**The comparison table** (README) states only what section 2 and `baselines.md` measured on 21 November 2026 (Skia's PDF backend, SkiaSharp 4.157.0 with PdfPig 0.1.16: Arabic as presentation forms in visual order, Hebrew reversed, no tagged-PDF surface, native sizes, 279 ms and 31 MB for the text-only spike; Playwright's Chromium headless shell: 2,048 ms, 495 MB, 288 MB on disk), what the Pdfwright tests
measure (PdfPig 3120 of 3120 and Firefox's pdf.js 3120 of 3120, 61 of 61 lines, xpdf at its floor of 45 of 61) and what Playwright documents (the `tagged` option of `page.pdf`, marked as not tested here). What nobody measured is written as "not measured here", including Chromium's Arabic extraction and Pdfwright's own timings (there is no benchmark scenario yet), and the closing paragraph says
that the two competitors' rows are spikes made for this plan and when to choose them instead.

**Package.** `Pdfwright.csproj` carries the description, the tags, the README (`PackageReadmeFile`), the MIT expression, the repository URL (from `Directory.Build.props`), and now `LICENSE` and `NOTICE` at the root of the package; there is no icon (Sheetwright has none). `dotnet pack` was run with `-nodeReuse:false -m:1`: `Pdfwright.nupkg` holds `lib/net8.0` to `lib/net11.0` (the dll and its XML documentation), `README.md`, `LICENSE` and `NOTICE`, and no font. The public XML documentation was scanned for names of slices and internal types, and the one stale comment (`LibraryInfo`, internal) was reworded.

**Not done, by design (the maintainer's):** the Trusted Publishing policy and the `NUGET_USER` secret, the native-speaker review, the viewer checks, the pdf.js re-check on the release candidate, the tag, the release and nuget.org. The version stays `0.0.1` in `Directory.Build.props`; the tag gives the release its version. (Written on 21 November; on 22 November the native-speaker review became an AI agent's language review and the viewer check became PDFium renders, see `RELEASING.md`.)

### List block (added before 0.1.0, 21 November 2026)

**Public surface.** `ItemList` (a sealed block in the closed set), `ListItem` (a record that is not a block: a first `Paragraph`, then paragraphs and nested lists) and `ListMarker` (`Bullet`, `Decimal`, `ArabicIndicDecimal`, `LowerAlpha`, `UpperAlpha`). `ItemList` has `Items`, `Marker`, `Start` (1 to 1,000,000), `Indent` (12 pt), `MarkerGap` (6 pt), `ItemSpacing`, `Style`, `MarkerStyle`, `Direction`, `SpaceBefore`, `SpaceAfter`. Values are checked at the init accessors with their own names; nesting deeper than one level is an `ArgumentException`; the lists and items compare element by element.
**The name.** Not `List`: with `using System.Collections.Generic;` (an implicit using in most projects) `List` would clash with `List<T>` in the very files that build the blocks, and it would need an alias like `Image` and `Drawing` do. `BulletList` would be wrong for a numbered list, so `ItemList` (not `ListBlock`: no other block carries the word).
**How it is laid out.** A list is not a new kind of item in the flow: it is built as a run of paragraphs (`TextItem`), each inset from the start edge by the room of its marker, and the first paragraph of an item carries a `ListPlacement` (inset, marker inset, side, the marker laid out alone) in its `StyledLayout`. `Draw` draws the marker when it draws line 0, so a marker is always with the first line of its item, an item splits between pages like a paragraph, and a callout or a column cell, which already stack text items, hold a list without any change to their splitting. The marker column is as wide as the widest marker of the list, so the text of every item starts at the same place; the marker is aligned to the start edge of the column and its baseline to the first line's.
**Direction.** The side of the marker is the resolved direction of the item's paragraph (its `Direction`, else the list's, else the enclosing block's, else the document's; with `Auto`, what the paragraph's text resolves to), and the marker text is laid out in that direction. The bidirectional algorithm makes `1.` in a right-to-left paragraph read digit first and then the full stop on its left, the conventional rendering for Arabic and Hebrew, so nothing is done to it; PdfPig reports that word as `.1`, in visual order, and the content stream holds `1.` in logical order.
**Not done.** No `KeepTogether` on a list, no Roman numerals or other digit sets, no list in a header or a footer (a list is several paragraphs), no widow or orphan control. Each is in `docs/known-limitations.md`.
**Checked.** Tests for the marker position in left-to-right and right-to-left lists, the hanging indent of wrapped lines in both, nesting, `Start`, spacing, pagination (marker with the first line, items split between pages), lists in a callout and a column cell, diagnostics with the page, extraction by PdfPig in Latin, Arabic and Hebrew, validation, equality and the public API snapshot (qpdf, where installed). A page with both directions was read in Firefox's pdf.js and looked as intended: bullets and numbers hanging, wrapped lines aligned with the text, the nested lettered list one indent in, and in the right-to-left lists the markers at the right edge with `.1` and `.١` shown full stop first on the left of the digit.

## 10. Repository, release and name

**Repository shape (as Sheetwright):** `src/<Name>`, `tests/<Name>.Tests`, `benchmarks/`, `tools/` (the Unicode table generator, the content-stream
diff), `Directory.Build.props` and `Directory.Packages.props` (central versions, warnings as errors, deterministic), `global.json`,
`.github/workflows/ci.yml` and `release.yml`, `README.md`, `RELEASING.md`, `CHANGELOG.md`, `LICENSE` (MIT), `NOTICE` (HarfBuzz, the Unicode
data licence, test fonts' OFL). **Targets:** `net8.0;net9.0;net10.0;net11.0` (managed code and `HarfBuzzSharp`; no per-TFM code expected).
**Release:** a tag `v0.1.0` is the version; the workflow builds, tests on every target, packs, publishes to nuget.org through **Trusted
Publishing** (a policy for the repository, workflow `release.yml`, the glob `<Name>*` allowed to publish new packages, a `NUGET_USER`
repository secret) and creates the GitHub release with `.nupkg` and `.snupkg`, exactly as `RELEASING.md` of Sheetwright says.
**Dependencies of the package:** `HarfBuzzSharp` and its `NativeAssets` for Windows, Linux (glibc and musl) and macOS; nothing else.
The package ships **no fonts**. Licence audit of the closure goes in `NOTICE` and is a CI check.

**Name candidates, in Sheetwright's style (a thing plus "wright", a maker).** Checked on 21 November 2026 against nuget.org's package index
(`api.nuget.org/v3-flatcontainer/<id>/index.json`: **404, not published**) and nuget.org search (**0 hits** for each). Nothing was reserved;
these are not guarantees: an id is free until somebody pushes it, and the check is repeated just before the repository is created. Names that
**are** taken, so not candidates: `Inkwright`, `Quillwright`, `Docwright`.

| Candidate | Id free? | Reading | Concern |
|---|---|---|---|
| **Pagewright** | yes (404, 0 search hits) | a maker of pages; the obvious sibling of Sheetwright | **One syllable from Playwright** (Microsoft's browser automation tool, a common way to make PDFs): easy to mistype, easy to confuse in search and in conversation ("Page" and "Play" differ by two letters). Distinct enough in writing, risky in speech |
| **Foliowright** | yes (404, 0 search hits) | a folio is a sheet folded for pages; a maker of folios | Longer, a little precious; nothing close to it on nuget.org |
| **Leafwright** | yes (404) | a leaf is a page of a book | Short and clear; "leaf" is also a plant, a mild ambiguity |
| Also free, not recommended | Printwright, Typewright, Pdfwright, Reportwright (404, 0 hits) | | "Pdfwright" names the format, not the craft; "Reportwright" is too narrow (invoices); "Typewright" suggests typesetting only |

**Update, 21 November (the maintainer wants the name to say that it is about PDF).** Names with `pdf` in them, checked the same way (index 404, search 0 hits, nothing reserved):

| Candidate | Free? | Reading |
|---|---|---|
| **Pdfwright** | yes | exactly Sheetwright's pattern (format plus maker); says what it does |
| Pdfsmith | yes | a smith of PDFs |
| Pdfloom | yes | a loom weaves: layout and text |
| Pdfpress | yes | a press prints |
| Pdfcraft | yes | craft |
| Pdfforge | **no** (`PdfForge` and its packages exist) | not a candidate |

**Decided: the package is called Pdfwright** (the maintainer, 21 November 2026; the id was re-checked free on nuget.org right after: index 404, 0 search hits; nothing reserved, no repository created yet). Placeholder names in section 5 read `Pdfwright` from here on. **Earlier recommendation, kept for the record: Pdfwright.** The earlier candidates stay in the table above for reference. The maintainer decides; a GitHub repository name and a nuget.org prefix reservation are separate checks done
when he has chosen.

## 11. Open points for the maintainer (the recommendation is first)

1. **Own writer (B) rather than Skia (A).** *Recommended: B, with the stop point after S2 and S3's first half and the contingency of section 3.*
   The spike evidence is in section 2.
2. **Is "no CJK in 0.1" acceptable?** CJK through a font directory needs CFF (OpenType) fonts, so a CFF subsetter
   (about 10 days) is the first item after 0.1 (call it 0.2 with tagged PDF). TrueType-flavoured CJK fonts are accepted in 0.1 without it, but untested: no CJK text is in the corpus (see `docs/known-limitations.md`).
   *Recommended: yes, CJK in 0.2.*
3. **Tagged PDF in 0.2, not 0.1** (section 6). *Recommended: yes, and written as a known limitation until then.*
4. **The name** (section 10). *Recommended: Pdfwright (the name says what it does, in Sheetwright's pattern).*
5. **Effort.** 13 weeks of one engineer-equivalent, 6 to 8 calendar weeks with parallel sessions, and a test period before a first release. Is that the right
   priority?
6. **Where the code lives and who may publish:** `sinanoran/<name>` and the maintainer's nuget.org profile, as for Sheetwright (a publishing
   policy is created by him, not by an agent).
