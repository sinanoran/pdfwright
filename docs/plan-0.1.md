# Plan: Pdfwright, our own PDF package

> Copied from the planning folder of Plinth, the first consumer, on 21 November 2026, when the repository was started; the package is called **Pdfwright** (decided by the maintainer the same day), and the placeholder names of section 5 read `Pdfwright` from here on. Sections that say "no repository yet" describe the state when it was written. Plinth's own plan for the adapter that uses it is `plan-q11-b1.md` in that repository. **Status lines in sections 2, 7 and 10 ("nothing is reserved", "in the session scratchpad", "no repository yet") describe 21 November before the repository existed; the repository now holds slice S0** (the harness, the corpus and fonts, the CI), and the baselines are in `baselines.md`.

**Status:** design for the maintainer, 21 November 2026. Nothing is reserved, created or published.
**Decision it serves:** the maintainer decided on 21 November that Plinth renders report PDFs through Playwright for now, behind an adapter,
and that we write our own PDF package in parallel, in its own repository like Sheetwright, to replace Playwright later. Performance and small
memory matter to him; Playwright's size is why.
**Model:** the Sheetwright repository (github.com/sinanoran/sheetwright: README, `RELEASING.md`, CI, tag-driven release to nuget.org through Trusted Publishing, MIT).
**Fits:** Plinth's plan Q11 with B1 (the `ReportDocument` contract of D8 and the page model of D11).

## 0. The answer in one page

1. **Build the PDF writer ourselves, as well as the layout.** The package is managed code on top of one native library, HarfBuzz (through
   `HarfBuzzSharp`, MIT), for shaping. It does **not** use SkiaSharp. The reason is evidence, not taste: a spike run today (section 2) shows that
   Skia's PDF backend writes Arabic and Hebrew as presentation forms in *visual* order, so the text cannot be extracted, searched or read by a
   screen reader; SkiaSharp exposes no structure tree, outlines, language or page labels, so tagged (accessible) PDF is impossible on it; and
   it adds a 13 MB native library per runtime. The earlier plan (`plan-q11-b1.md`, D9) recommended Skia and wrote its own exit criterion 1,
   "the text can be extracted back in logical order"; the spike fails that criterion, and the plan said that in that case the maintainer
   decides between an own writer and a commercial adapter. This is that decision, with numbers.
2. **Three layers, one public surface.** A PDF writer with embedded font subsets (internal), a text engine (itemize, bidi, shape, break;
   internal) and a document layer (flow, paragraphs, tables with pagination and repeated headers, header and footer, a drawing canvas for
   charts; **public**). The public API is small and written to stay: it is the part that is hard to change after release (section 5).
3. **Version 0.1 is what Plinth's reports need and nothing more:** heading, KPI row, table with pagination and repeated header, chart as vectors,
   note; header and footer with organisation, date, zone and filters; page breaks; embedded subset TrueType fonts; right-to-left and mixed
   direction; Arabic, Indic and Thai shaping. **Not in 0.1:** CJK (needs CFF fonts and a CFF subsetter), emoji and colour fonts, forms,
   encryption, PDF/A, tagged PDF (but the design carries it, section 6).
4. **Targets (section 7), against what Playwright costs today on this machine** (measured, section 2): a 1,000-row table over 20 pages renders in
   2.0 s with a 495 MB peak today; the target is **under 250 ms cold in a fresh process and under 60 ms warm, under 25 MB over the runtime's
   baseline, under 150 KB of output**.
5. **About 13 working weeks for one engineer-equivalent, plus or minus 40 percent** (section 9); with parallel agent sessions, 6 to 8 weeks of
   calendar if the three engine slices run side by side. The first useful checkpoint is after slice 3 (a one-page report in every
   script of the corpus, with correct text extraction), at about five weeks of effort.

## 1. What Plinth's reports need (from `plan-q11-b1.md`)

The adapter implements `IReportPdfRenderer { Task<ReportPdf> RenderAsync(ReportDocument, ReportRenderContext, CancellationToken) }` over a
neutral `ReportDocument`: **heading, KPI row, table, chart, note** (D8). The adapter, not the package, knows dashboards and widgets. The page
model of D11, which the package must be able to produce:

- A4 portrait by default, Letter for some countries, landscape for a wide pivot; page one has a header (organisation name and logo, the
  dashboard's title, "as of" in the report's zone and the recipient's language, the filters in force, "Prepared for {name}"); later pages a thin
  running header and "page x of y".
- Body in the dashboard's order; `kpi` and `gauge` up to four per row, everything else full width; **a chart is never split across pages**;
  **a table repeats its header on each page**, at most 200 rows, then a "N more rows" line.
- Limits (100 pages, 20 MB, 10 minutes), a cell shortened at 1,000 characters, a stable failure reason when a limit is hit.
- Metadata: title, language, creator "Plinth", no author, no query text, no internal ids.
- Fonts embedded, never looked up in the operating system; a character no loaded font has is drawn as the replacement box and **reported**
  (never a failure of the report): the run page says which language had missing characters.
- Charts: the adapter draws bar, line, area, donut, gauge, pivot as vectors from data, with the kit's design tokens as colours (D12). So the
  package must offer a **drawing canvas** and **text measurement**, not a chart library.

The package never learns the words "dashboard", "widget", "organisation" or "report": the adapter maps `ReportDocument` onto the public API of
section 5. That keeps the package usable for invoices later (Billing), which D8 already names.

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
    Title = "Open invoices", Language = "tr", Creator = "Plinth",
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
doc.Add(new Callout(new Paragraph("This section is only available in Plinth.")));

await doc.SaveAsync(stream, cancellationToken);                  // streams pages out when it can
DocumentDiagnostics report = doc.Diagnostics;                    // missing glyphs by language, pages, bytes
```

**What the sketch fixes, and why each is expensive to change later:**

1. **Blocks are immutable records** (`Heading`, `Paragraph`, `Table`, `Columns`, `Drawing`, `Callout`, `PageBreak`, `Spacer`) in a **closed**
   set, so the layout's `switch` has no discard arm and an adapter's mapping is checked by the compiler. New blocks are added, never changed.
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
8. **Table rows are streamed** (`IEnumerable<TableRow>` or `IAsyncEnumerable`) so a 1,000-row table is never held twice; auto column widths
   come from the header and a bounded sample, said in the documentation.
9. **Versioning:** 0.x may break between minors with a changelog; the section 5 surface is reviewed line by line before 1.0, and the
   blocks above carry `[Experimental]`-free names from the start so that 0.1 consumers (Plinth) do not rename anything later.

**Mapping from the adapter's `ReportDocument`:** `ReportHeading` to `Heading`; `ReportKpiRow` to `Columns` of small `Paragraph`s on a filled
`Callout`; `ReportTable` to `Table` (header repeated, the 200-row limit and the "N more rows" line are the adapter's); `ReportChart` to
`Drawing` (the adapter draws the chart with the canvas and the measurer, from the shared vectors of D12); `ReportNote` to `Callout`;
page-one header and the running header to `Header`; "page x of y" to the footer's `PageContext`. The adapter owns Plinth's limits and words.

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
  on the corpus. Target: the version after 0.1 (0.2), once the engine slices are stable. The product note for Plinth: 0.1 reports are not
  tagged (as `plan-q11-b1.md`, D11, already says); the workbook and the screen are the accessible forms.

## 7. Performance targets and how they are measured

**Baselines (section 2):** Playwright, 1,000-row table: 2,048 ms, 495 MB, 154 KB. Skia-backed spike: 279 ms, +7 MB, 590 KB.

| Scenario | Target (warm, steady state) | Target (cold, fresh process) | Memory | Output size |
|---|---|---|---|---|
| **20-page report**: heading, 4 KPI boxes, three charts, two 40-row tables, notes, header and footer, Latin | **under 40 ms** | under 200 ms | managed allocation **under 10 MB**; working-set increase over the runtime baseline **under 25 MB** | under 120 KB |
| **1,000-row table, 6 columns, about 20 pages, repeated header** | **under 60 ms** | **under 250 ms** | allocation under 20 MB; increase under 25 MB, and **flat as rows grow** (rows are streamed, a page is released when emitted) | under 150 KB (Playwright: 154 KB) |
| **100 pages, 12 charts, 20 tables** (the P0 criterion of `plan-q11-b1.md`: under 5 s and under 200 MB with Skia) | under 400 ms | under 700 ms | under 60 MB | under 600 KB |
| Arabic or Devanagari table of the same size | at most **2 times** the Latin time | | same | same |
| Fonts | a Latin report embeds **only the glyphs it uses**: Noto Sans subset under 25 KB | | | |

**How it is measured, so a regression is visible and the numbers are not lore:**
- A `benchmarks/` project (BenchmarkDotNet, `MemoryDiagnoser`) for warm time and allocations of each scenario above, run in CI on the release
  runner as a **non-blocking** job that posts a table and fails only on a regression of 25 percent or more against the last tagged release.
- A **process harness** for the cold numbers and the working-set increase (it starts a fresh process per run, reads `PeakWorkingSet64`
  and wall time, runs 10 times, reports the median), the method of the spike in section 2, which also measures Playwright on the same
  machine for the comparison table in the README. The harness is a console project in the repository, so the comparison can be re-run by
  anyone.
- **Allocation budgets as tests:** a unit test that renders the 1,000-row table and fails when `GC.GetAllocatedBytesForCurrentThread` exceeds
  the budget by 25 percent (allocation counts are stable across machines where times are not).
- The numbers go in `docs/baselines.md` (linked from the README) with the machine described, as Sheetwright's README states its own limits.

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
   it is used) compared with a stored PNG by a perceptual tolerance, so a shaping regression that extraction would miss (a mark drawn
   in the wrong place) is caught. Native-speaker review of the Arabic, Hebrew, Devanagari and Thai pages before 0.1.0 (a release-checklist
   line, not a test).
6. **Structure and viewers:** `qpdf --check` on every generated file in CI (a process, Apache-2.0); a release checklist that opens the
   golden set in Chrome, Edge, Acrobat Reader and macOS Preview.
7. **Property and fuzz tests:** random mixed-script strings never throw and never draw outside the page; table pagination invariants (every
   row appears once, the header repeats on every page after the first it starts on, no row is split unless taller than a page); layout is
   deterministic (two runs, equal bytes).
8. **The adapter's own tests** (in Plinth, at integration time): the shared chart vectors of D12 and a golden report of the dashboard
   fixture; the same fixtures are rendered through Playwright and the package during the parallel period so differences are visible.

## 9. Slices and estimate

Effort in **working days of one engineer-equivalent** (an agent session plus review counts as the engineer for the estimate); the range is
plus or minus 40 percent because shaping and font bugs are the part nobody can estimate from outside. Slices are small and ordered; each ends
with a green CI and a reviewed pull request, as in Sheetwright.

| Slice | What | Days |
|---|---|---|
| **S0** | Repository skeleton (as Sheetwright: Directory.Build.props, central packages, CI, RELEASING, README stub), the corpus and fonts, the spike results of section 2 as a documented baseline, the benchmark and process harnesses | 3 |
| **S1** | **PDF writer core:** objects, stream-form cross-reference, content-stream builder, Flate, page tree, Info and XMP, annotations (URI, destination), deterministic mode, `qpdf --check` in CI | 8 |
| **S2** | **Font engine:** TrueType parser, glyph-closure subsetter (keeps glyph ids, drops unused outlines; composite glyphs followed), CID fonts with `Identity-H`, widths, **`ToUnicode` from clusters, one text per code**, `fsType` check; extraction test for Latin | 10 |
| **S3** | **Text engine:** Unicode tables and their generator, script itemization, bidi (UAX #9) and mirroring, font fallback runs, HarfBuzz shaping with features, grapheme-safe breaking, line breaking (UAX #14), the conformance files | 12 |
| **S4** | **Paragraph layout and the drawing canvas**: lines, alignment (logical), runs, the public `TextMeasurer`, paths, clip, transforms, opacity, JPEG and PNG | 8 |
| **S5** | **Document layer:** the block set, flow and page breaks, keep-together, columns, **tables with pagination, repeated header, streamed rows, auto widths**, header and footer with page count (two passes only when the footer asks for it) | 10 |
| **S6** | **Script conformance:** the corpora for Arabic, Hebrew, Devanagari, Bengali, Tamil, Thai, mixed direction; extraction and visual goldens; fixes in S2 to S4; native-speaker review | 8 |
| **S7** | Documentation, samples, the README comparison table, `RELEASING.md`, Trusted Publishing, the CHANGELOG, **0.1.0** | 5 |
| | **Total to 0.1.0** | **64 days, about 13 weeks** |
| **A1** | (in Plinth, not the package) the adapter `Plinth.Modules.Dashboards.Pdf.Package` implementing `IReportPdfRenderer` and the chart drawing (replaces the Playwright adapter's engine); the font pack | 5 |

**Order and parallel work:** S0 first; then **S1 and S2 can run beside S3** (the text engine has no dependency on the writer until S4);
S4 needs S2 and S3; S5 needs S4; S6 runs from S3 on as the corpus grows and finishes last. With three parallel sessions the calendar is
**6 to 8 weeks**; the critical path is S0, S3, S4, S5, S6, S7. **First checkpoint** (a one-page report in every script, text extracted
correctly): after S4, about **5 weeks of effort**. A **go or stop point** after S2 and S3's first half (week 3 to 4): if Arabic extraction and a
Devanagari conjunct are not correct by then, the contingency of section 3 is chosen.

**Known limits of the S1 writer, to be lifted where a later slice needs it:** the writer writes to its stream synchronously and unbuffered, so the
asynchronous save of S5 must buffer (or write through a pipe) rather than call it from an async method; the page tree is flat (one `/Pages` node holding
every page), which viewers handle well into the thousands of pages and which a balanced tree would improve; and a page's content stream is held in
memory, with a few copies while it is compressed, so one very large page costs several times its own size at completion.
A document's other pages are written as they end and cost nothing more.

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
| pdf.js (Firefox's viewer) | logical order and mixed direction correct; 27 of 61 clean, **every other line differs only by junk characters from codes whose `ToUnicode` entry is empty**; mapping those codes to U+034F removes the junk but leaves invisible characters in the text and pdf.js still adds spaces inside Indic clusters, so it is not the fix |
| xpdf `pdftotext` default (position order) | 36 of 61 exact after removing the direction marks it adds itself, 40 of 61 ignoring the order of combining marks; the rest differ by spaces inside clusters (one `Tm` per glyph) and by spaces lost or moved where the direction changes |
| xpdf `-raw` | spaces between letters (one `Tm` per glyph) |

So the remaining work is exactly the two open defects, now with a measuring harness (`PDFWRIGHT_DUMP=<folder>` keeps the files, then read them with the readers above):
S4 writes runs with `TJ` adjustments and one `Tm` per run (removes the spaces), and decides the `ActualText` question for the continuation glyphs of a cluster
(measured on pdf.js, which respects an explicit mapping). The criterion for S4 is the table above with every row at 61 of 61, modulo characters the reader adds itself.

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
| **Pagewright** | yes (404, 0 search hits) | a maker of pages; the obvious sibling of Sheetwright | **One syllable from Playwright** (Microsoft's tool this package replaces): easy to mistype, easy to confuse in search and in conversation ("Page" and "Play" differ by two letters). Distinct enough in writing, risky in speech |
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
   This changes `plan-q11-b1.md` D9's recommendation; the spike evidence is in section 2.
2. **Is "no CJK in 0.1" acceptable?** The Q11 plan allowed CJK through a font directory; that needs CFF (OpenType) fonts, so a CFF subsetter
   (about 10 days) is the first item after 0.1 (call it 0.2 with tagged PDF). TrueType-flavoured CJK fonts would work in 0.1 without it.
   *Recommended: yes, CJK in 0.2.*
3. **Tagged PDF in 0.2, not 0.1** (section 6). *Recommended: yes, and written as a known limitation of Plinth's reports until then.*
4. **The name** (section 10). *Recommended: Pdfwright (the name says what it does, in Sheetwright's pattern).*
5. **Effort.** 13 weeks of one engineer-equivalent, 6 to 8 calendar weeks with parallel sessions, plus 1 week to adopt it in Plinth. Is that the right
   priority beside the Playwright adapter that ships first?
6. **Where the code lives and who may publish:** `sinanoran/<name>` and the maintainer's nuget.org profile, as for Sheetwright (a publishing
   policy is created by him, not by an agent).
