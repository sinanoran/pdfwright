# Plan: Pdfwright 0.2

> Written on 22 November 2026, before 0.1.0 was tagged. The maintainer decided that 0.1.0 ships without tested CJK and that 0.2 starts
> right after the 0.1.0 tag, with CJK first and tagged (accessible) PDF after it. The 0.1 plan, its measurements and its decisions are in
> [`plan-0.1.md`](plan-0.1.md); this plan only adds to it.

## 0. The answer in one page

1. **0.2 is CJK, then tagged PDF.** Chinese (Simplified and Traditional), Japanese and Korean text, written with the fonts people actually
   have (most of them OpenType fonts with CFF or CFF2 outlines), read back in logical order by the same independent readers as the other
   scripts; then a tagged PDF with a structure tree, alternative text and languages, validated by veraPDF.
2. **What 0.1 already does for CJK.** A CJK font with TrueType (`glyf`) outlines is accepted: its characters are mapped (character maps of
   format 4 and 12), shaped, embedded as a subset and broken between ideographs by UAX #14. Nothing about it is measured, because the test
   corpus has no CJK text. Fonts with CFF outlines (`OTTO`), which most CJK fonts are (Noto Sans CJK, Source Han Sans and Serif), are refused.
   Of a font collection (`.ttc`) only the first font is read. There is no vertical text. (See [`known-limitations.md`](known-limitations.md).)
3. **The CFF and CFF2 subsetter is not written by hand: it is HarfBuzz's own (`hb-subset`)**, reached through the native library
   Pdfwright already ships. The rule is to reuse permissive open source before writing from scratch: `hb-subset` handles CFF, CFF2 and
   TrueType, is the subsetter of Chrome and Google Fonts, and is under HarfBuzz's MIT licence. Its entry points (`hb_subset_input_create_or_fail`,
   `hb_subset_input_glyph_set`, `hb_subset_or_fail`, `hb_subset_input_set_flags`, the `hb_subset_axis_range_*` family for pinning a variable
   font at its default, `hb_subset_plan_create_or_fail` and `hb_subset_plan_old_to_new_glyph_mapping`) were found, by a search of the binaries'
   bytes, in every native asset of HarfBuzzSharp 14.4.0.200 (Windows x64, x86 and arm64; Linux glibc and musl on x64, arm64 and arm; macOS, a
   universal binary); a byte search is not a load test, which slice C1 makes. HarfBuzzSharp has no managed binding for them, so Pdfwright declares its own P/Invokes. The work left is the binding, extracting the CFF from the font it
   returns, and embedding it as `CIDFontType0` with a `FontFile3` (`/Subtype /CIDFontType0C`). The 0.1 plan's estimate of about 10 days for a
   hand-written CFF subsetter falls away. The TrueType subsetter Pdfwright wrote for 0.1 stays unless `hb-subset` is clearly better for
   TrueType too, which slice C2 measures (subset size, time, and the read-back results) before anything is switched.
4. **Same acceptance as 0.1:** every corpus string read back equal to its input by PdfPig and by Firefox's pdf.js, a visual golden through
   PDFium, `qpdf --check`, and the release checklist (native-speaker or equivalent review, viewer checks).
5. **No breaking change to the public API.** Everything below is additive: new fonts are accepted by the same `FontCollection.Add`, new
   options are `init` properties, tagging is switched on by a new `PdfDocumentOptions.Tagged` (see the note in section 6 of the 0.1 plan).

## 1. Scope of 0.2

**In:**
- CFF (OpenType `OTTO`) fonts, both name-keyed and CID-keyed, and CFF2 fonts at their default instance if `hb-subset` returns them as CFF
  (otherwise CFF2 fonts are refused in 0.2: PDF names no CFF2 font file subtype and the readers are not known to read one); embedding as
  `CIDFontType0` with a `FontFile3`; widths from `hmtx`; `ToUnicode` with one text per code as in 0.1.
- **Dense codes for large fonts.** A subset is made without retained glyph ids (`hb-subset` would otherwise fill the CharStrings up to the
  highest id kept, about 60,000 entries for a CJK font) and the old ids are mapped to the new through `hb_subset_plan_old_to_new_glyph_mapping`,
  so the codes written are dense. This also removes 0.1's limit for fonts near 65,535 glyphs (Noto Sans CJK has 65,535), where a glyph drawn
  for a second text, or two different missing characters, fails the save; the TrueType path gets the same once it goes through the seam.
- Font collections (`.ttc` and `.otc`): a font is chosen by index or by name (`FontCollection.Add(bytes, index)` or similar, additive).
- CJK text in horizontal layout: Simplified Chinese, Traditional Chinese, Japanese (kanji, hiragana, katakana, including the small kana and the
  prolonged sound mark) and Korean (Hangul syllables and jamo); full-width punctuation; mixed CJK and Latin in one line; numbers.
- **Japanese line-breaking rules (kinsoku shori):** no line starts with a closing bracket, a small kana, the prolonged sound mark, an iteration
  mark, a full stop or comma, an exclamation or question mark or an ellipsis, and no line ends with an opening bracket; and a two-em dash is
  not split. UAX #14 already gives most of this (classes CL, CP, NS, EX, IN, B2, OP and CJ). 0.1 already behaves as the strict rule, because CJ
  is resolved as NS; what is left to decide and test is whether a "normal" mode (CJ as ID, a small kana may start a line) is offered, and
  whether punctuation is pushed or hung.
- **Korean line breaking:** UAX #14 breaks between Hangul syllables; Korean text is usually broken at spaces (CSS's `keep-all`). 0.2 decides
  which is the default for `ko` and tests it.
- Language-dependent glyphs: the same code point drawn with the Simplified Chinese, Traditional Chinese, Japanese or Korean form, chosen by
  `TextStyle.Language` (and `PdfDocumentOptions.Language`, which already flows to the runs) through the OpenType `locl` feature, which HarfBuzz
  applies when the language is set. The tags tested: `zh-Hans` (ZHS), `zh-Hant` (ZHT), `zh`, `zh-TW`, `zh-HK` (ZHH), `ja` (JAN), `ko` (KOR).
- Variation sequences (character map format 14, for ideographic variants), which HarfBuzz already applies when the font has them.
- Line height in a line that mixes a Latin font and a CJK font (the line takes the larger ascent and descent, as for any mixed run in 0.1; the
  corpus checks it).
- **Tagged PDF**, after CJK (section 4).

**Out of 0.2, named so nobody assumes them:** vertical text (`vert`/`vrt2`, vertical metrics, rotated Latin); ruby (furigana); emphasis marks;
CJK justification and inter-character spacing adjustment (JIS X 4051 in full); variable-font instances other than the default; emoji and
colour fonts (COLR, SVG, sbix, CBDT); dictionary line breaking for Thai, Lao, Khmer and Burmese (a candidate for a later minor version).

## 2. Slices and estimate

Effort in working days of one engineer-equivalent, plus or minus 40 percent, as in the 0.1 plan; each slice ends with a reviewed pull request
and green CI on the three operating systems and both Linux runtime images.

| Slice | What | Days (the estimate) |
|---|---|---|
| **C1** | **`hb-subset` available everywhere:** prove that the `hb_subset_*` exports exist in every native asset of HarfBuzzSharp 14.4.0.200 that Pdfwright ships (Windows x64, x86 and arm64; Linux glibc and musl on x64 and arm64; macOS), by a test that calls them on each CI runner and inside the two .NET runtime images of the CI job `linux-runtime-images`; the P/Invoke declarations (`hb_subset_input_create_or_fail`, `hb_subset_input_glyph_set`, `hb_set_add`, `hb_subset_input_set_flags`, `hb_subset_plan_create_or_fail`, `hb_subset_plan_old_to_new_glyph_mapping`, `hb_subset_or_fail`, the face and blob release calls), a managed wrapper that owns the native handles, and a refusal with a clear message if an export is missing on some platform. If an asset lacks the exports, stop and decide before C2 (a newer HarfBuzzSharp, or the platform listed as unsupported for CFF). Also the shaping seam of section 5: an internal `IShaper` with HarfBuzz as its one implementation and a font handle without HarfBuzz types, with no change in behaviour (corpus and goldens unchanged) | 2 |
| **C2** | **CFF embedding through `hb-subset`**, behind the internal `IFontSubsetter` seam of section 5 (the own TrueType subsetter as one implementation, `hb-subset` as the other): the `TrueTypeFont` reader (and `ShapingFont`, which require `glyf` and `loca`) generalised to an sfnt font with `glyf` or CFF outlines; subset without retained glyph ids and remapped through `hb_subset_plan_old_to_new_glyph_mapping` (section 1, dense codes); a `CIDFontType0` has no `CIDToGIDMap`, so the code written for a glyph is its CID (the new glyph id for a name-keyed CFF, and for a CID-keyed CFF the CID its charset gives that glyph, per ISO 32000-1 9.7.4.2), and a glyph drawn for a second text, which 0.1 gives an extra code mapped back to the glyph by `CIDToGIDMap`, gets a code of its own mapped to the same CID by an embedded CMap stream instead of `Identity-H`, with a second `ToUnicode` entry; take the `CFF ` table from the result (a CFF2 font pinned at its default instance and checked to come back as CFF; if it stays CFF2, CFF2 fonts are refused in 0.2), write `CIDFontType0` with `FontFile3` and `/Subtype /CIDFontType0C`, widths from `hmtx`; the CFF reading needed for the font's metrics and for validation at `Add` (not for subsetting); `qpdf --check`, PdfPig and pdf.js read-back on a CFF Latin font against the Latin corpus before any CJK; and the measurement of `hb-subset` against the own TrueType subsetter (size, time, read-back) that decides whether TrueType moves too. fontTools (MIT) may be used as a test-time reference, never shipped | 4 |
| **C3** | **Font collections** (`.ttc`, `.otc`): choose a font by index or by name; the face index passed to `new Face(blob, index)` (today always 0, `TextShaper.cs`) and to `hb-subset`; the public addition and its tests | 1 |
| **C4** | **CJK corpus and read-back:** Simplified Chinese, Traditional Chinese, Japanese and Korean, a short page and a long page with a table each (as the 0.1 corpus), with fonts under an open licence in the test project (Noto Sans CJK in its OpenType CFF form, subset for the repository if the full fonts are too large; their licence and source recorded as for the 0.1 fonts; a repository subset must keep every layout feature, `locl` alternates included, or the language-form goldens of C5 cannot pass); PdfPig and pdf.js read-back with the same strict matcher and position check as 0.1; byte goldens and PDFium render goldens | 4 |
| **C5** | **Japanese and Korean line breaking and language forms:** the kinsoku rules and the Korean default of section 1 decided and tested on the corpus; `locl` forms by language checked by render goldens (one code point in four languages) | 2 |
| **T1** | **Tagged PDF:** structure tree, marked content with MCIDs, role map, parent tree, `/Alt` from `AltText`, `/Lang` on spans, header and footer as artifacts, table header cells with scope, `OutputIntent`; a `Tagged` option; veraPDF (a test-time process that needs Java on the CI runners) for PDF/UA-1 on the whole corpus, no conformance claimed until it passes; a missing glyph drawn as `.notdef` fails PDF/UA-1 (Matterhorn 7.21.8), so a tagged document with missing glyphs is reported, not claimed | 8 |
| **T2** | Documentation, the comparison table and the release checklist updated; **0.2.0** (tagged by the maintainer) | 2 |
| | **Total to 0.2.0** | **23 days** |

**Order:** C1, C2 (the critical path), then C3, C4 and C5 (C4 needs C2; C3 and C5 can run beside C4), then T1 and T2. CJK is usable after C4;
a 0.2 preview package is the maintainer's choice.

## 3. Test strategy for CJK

The 0.1 strategy holds unchanged (plan-0.1 section 8, and the S6a and S6b status sections): every string of every corpus read back equal to
its input in logical order by PdfPig and checked by glyph position, the same read by pdf.js by hand before a release, byte goldens, PDFium
render goldens with the absolute one-pixel rule, `qpdf --check`, the seeded property tests extended to CJK text. In addition:

- **A Latin CFF font first.** CFF embedding is proven on a CFF Latin font against the existing Latin corpus before any CJK text, so a CFF bug
  is not confused with a CJK one.
- **The native subsetter on every platform.** The `hb-subset` call runs in the test suite on Windows, Linux and macOS runners and inside the
  glibc and musl .NET runtime images, so a missing export fails CI rather than a user's save.
- **Subset sizes.** A CJK font is large (tens of megabytes); the test asserts that a one-page CJK document stays small (the subset holds only
  the glyphs used and the subroutines they need) and records the size in `docs/baselines.md`.
- **Fuzzing** the CFF metrics reader, and the path into `hb-subset`, with damaged fonts, as the PNG decoder was fuzzed: only `ArgumentException`
  (or the documented exception) may escape, never an index or overflow exception, and a font `hb-subset` refuses (`hb_subset_or_fail`
  returns null) is refused at `FontCollection.Add`, not at save time.
- **Review:** a reader of each of the four languages checks the corpus pages before 0.2.0, or the review that replaces it is recorded
  honestly in `RELEASING.md` and the release notes, as for 0.1.0.

## 4. Tagged PDF after CJK

Section 6 of the 0.1 plan, with its note on the room the 0.1 API leaves, is the design. T1 adds the structure tree and the marked content in
the writer and switches them on with `PdfDocumentOptions.Tagged`; roles follow from the block types, the header and footer are artifacts,
`Image.AltText` and `Drawing.AltText` become `/Alt`, `TextStyle.Language` becomes the spans' `/Lang`. veraPDF runs in CI on the corpus with
`Tagged = true`, and no PDF/UA claim is made until it passes everywhere. Lists (the `ItemList` block, added just before 0.1.0) are tagged as
`L`, `LI`, `Lbl` and `LBody`.

## 5. HarfBuzz stays, behind internal seams

HarfBuzz stays as the shaper; an own shaper is not planned, and is revisited only if a user needs a build with no native code. Shaping and
subsetting are still kept behind internal seams, which cost nothing and keep the writer, the layout and the public API independent of the
native library:

- **Shaping.** HarfBuzzSharp is used in exactly one file today, `src/Pdfwright/Text/TextShaper.cs`; everything above it (line breaking,
  layout, `GlyphRunWriter`, the document layer) sees only Pdfwright's own types, `ShapedParagraph`, `ShapedRun` and `ShapedGlyph` (glyph id,
  cluster, advance and offsets in font units). The input side is not yet neutral: `ShapingFont` holds the HarfBuzz blob, face and font and is
  passed through `FontCollection`, the canvas and `ParagraphLayout`, and `TextShaper` is a static class. (The tests use HarfBuzzSharp too.) Slice C1 turns this into an internal seam: an `IShaper` (shape one run of text with a font, a script, a direction and a language into
  `ShapedGlyph`s) with the HarfBuzz implementation as the only one, and a font handle that does not expose HarfBuzz types. No behaviour changes,
  and the corpus and goldens prove it.
- **Subsetting.** `PdfEmbeddedFont` calls the own `TrueTypeSubsetter` directly today. Slice C2 puts subsetting behind an internal
  `IFontSubsetter` (the font and the glyph ids to keep, returning the bytes to embed, their kind, TrueType or CFF, and the old-to-new glyph map),
  with the own TrueType subsetter as one implementation and `hb-subset` as the other, chosen by the font's outline format.

### C1 status

Slice C1 is done: the shaping seam, the `hb-subset` binding, and the proof that its exports load. No behaviour changed (the corpus, the byte goldens and the render goldens are as they were, and the public API snapshot is unchanged).

- **Shaping seam.** `IShaper` (`src/Pdfwright/Text/IShaper.cs`) has two members: `CreateFont(bytes, unitsPerEm)`, which returns an opaque `ShaperFont` handle owned by the shaper, and
  `Shape(font, text, start, length, rightToLeft, scriptTag, language)`, which returns the `ShapedGlyph`s of one run with the text around it as context. `ShapingFont` keeps the `TrueTypeFont` (which answers glyph-for-code-point, so font choice
  stays in `TextShaper`) and the shaper's handle; it has no HarfBuzz type. `HarfBuzzShaper` and `HarfBuzzFont` (blob, face and font) are the one implementation, in `src/Pdfwright/Text/HarfBuzz/`; the files of that folder are
  the only ones of the library that name `HarfBuzzSharp`. The policy that was in `TextShaper` (monotone-character clusters for a right-to-left run, the baseline `.notdef`) moved into `HarfBuzzShaper` unchanged.
- **hb-subset binding.** `HarfBuzzSubsetNative` declares the P/Invokes in the library name HarfBuzzSharp declares for its own calls, `libHarfBuzzSharp` (the runtime resolves it to `libHarfBuzzSharp.dll`, `libHarfBuzzSharp.so` and
  `libHarfBuzzSharp.dylib`), so the same native file serves shaping and subsetting. `EnsureAvailable` checks every export with `NativeLibrary.TryGetExport` before the first call and throws `PlatformNotSupportedException` naming the
  library, the runtime identifier, the operating system and the missing exports; a `EntryPointNotFoundException` during a call is wrapped the same way. `HarfBuzzSubsetter` (`Subset(face or bytes, glyph ids, flags, pinAxesToDefault)`) owns the input, plan and result
  handles as `SafeHandle`s and returns the font bytes and the old-to-new glyph map (read from `hb_subset_plan_old_to_new_glyph_mapping`). It is not used by the writer.
- **`IFontSubsetter` is left to C2.** `PdfEmbeddedFont` takes bytes from `TrueTypeSubsetter` and nothing else; the seam's result (bytes, kind, old-to-new map) is C2's design, so it is not guessed at here.
- **Proof the exports load.** `HarfBuzzSubsetterTests` (not skipped anywhere) checks that every export is present and that a subset of Noto Sans has the glyphs asked for, their advances and outlines, and the right map; it runs in the `build` job on
  Windows, Linux and macOS and on every target framework. The job `linux-runtime-images` publishes `tools/Pdfwright.NativeProbe` and runs the same call inside `aspnet:10.0` (glibc) and `aspnet:10.0-alpine` (musl); the probe exits with 2 when an export is missing.
  CI result (PR 29): the tests passed on windows-latest, ubuntu-latest and macos-latest (all four target frameworks run in the one job), and the probe passed in `aspnet:10.0` (linux-x64, glibc) and `aspnet:10.0-alpine` (linux-musl-x64): every export, including `hb_subset_input_pin_all_axes_to_default`, is present in the native library on all five. The other native assets (x86, arm64, arm) are not run by CI.

## 6. Open points for the maintainer

1. **Which CJK fonts go into the test project** (Noto Sans CJK SC, TC, JP and KR in their OTF form are about 16 MB each; a repository subset
   of the corpus's characters is the recommendation, with the full-font test run only locally).
2. **Kinsoku: strict or normal** for Japanese (whether a small kana may start a line). *Recommended: strict by default, as 0.1 already
   behaves, and an option later if asked.*
3. **Korean: break at spaces or between syllables** by default. *Recommended: at spaces (`keep-all`), as Korean text is usually set.*
4. **Performance.** The 0.1.0 measurements (`docs/baselines.md`) miss the 0.1 plan's section 7 targets; whether a performance slice comes
   before, beside or after CJK is the maintainer's choice (a CJK document has more glyphs per page, so the cost per glyph matters more).
