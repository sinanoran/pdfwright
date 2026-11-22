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
   font at its default) were found exported by `libHarfBuzzSharp` in `harfbuzzsharp.nativeassets.win32` 14.4.0.200 (win-x64); HarfBuzzSharp has
   no managed binding for them, so Pdfwright declares its own P/Invokes. The work left is the binding, extracting the CFF from the font it
   returns, and embedding it as `CIDFontType0` with a `FontFile3` (`/Subtype /CIDFontType0C`). The 0.1 plan's estimate of about 10 days for a
   hand-written CFF subsetter falls away. The TrueType subsetter Pdfwright wrote for 0.1 stays unless `hb-subset` is clearly better for
   TrueType too, which slice C2 measures (subset size, time, and the read-back results) before anything is switched.
4. **Same acceptance as 0.1:** every corpus string read back equal to its input by PdfPig and by Firefox's pdf.js, a visual golden through
   PDFium, `qpdf --check`, and the release checklist (native-speaker or equivalent review, viewer checks).
5. **No breaking change to the public API.** Everything below is additive: new fonts are accepted by the same `FontCollection.Add`, new
   options are `init` properties, tagging is switched on by a new `PdfDocumentOptions.Tagged` (see the note in section 6 of the 0.1 plan).

## 1. Scope of 0.2

**In:**
- CFF (OpenType `OTTO`) fonts, both name-keyed and CID-keyed, and CFF2 fonts at their default instance; embedding as `CIDFontType0` with a
  `FontFile3`; widths from `hmtx`; `ToUnicode` with one text per code as in 0.1.
- Font collections (`.ttc` and `.otc`): a font is chosen by index or by name (`FontCollection.Add(bytes, index)` or similar, additive).
- CJK text in horizontal layout: Simplified Chinese, Traditional Chinese, Japanese (kanji, hiragana, katakana, including the small kana and the
  prolonged sound mark) and Korean (Hangul syllables and jamo); full-width punctuation; mixed CJK and Latin in one line; numbers.
- **Japanese line-breaking rules (kinsoku shori):** no line starts with a closing bracket, a small kana, the prolonged sound mark, an iteration
  mark or a full stop or comma, and no line ends with an opening bracket; UAX #14 already gives most of this (classes CL, CP, NS, OP and CJ,
  with CJ resolved as NS as in 0.1); what UAX #14 leaves to the application is decided and tested here (strict versus normal for CJ, and
  whether to hang or push the punctuation).
- Language-dependent glyphs: the same code point drawn with the Simplified Chinese, Traditional Chinese, Japanese or Korean form, chosen by
  `TextStyle.Language` (`zh-Hans`, `zh-Hant`, `ja`, `ko`) through the OpenType `locl` feature, which HarfBuzz applies when the language is set.
- **Tagged PDF**, after CJK (section 4).

**Out of 0.2, named so nobody assumes them:** vertical text (`vert`/`vrt2`, vertical metrics, rotated Latin); ruby (furigana); emphasis marks;
CJK justification and inter-character spacing adjustment (JIS X 4051 in full); variable-font instances other than the default; emoji and
colour fonts (COLR, SVG, sbix, CBDT); dictionary line breaking for Thai, Lao, Khmer and Burmese (a candidate for a later minor version).

## 2. Slices and estimate

Effort in working days of one engineer-equivalent, plus or minus 40 percent, as in the 0.1 plan; each slice ends with a reviewed pull request
and green CI on the three operating systems and both Linux runtime images.

| Slice | What | Days (the estimate) |
|---|---|---|
| **C1** | **`hb-subset` available everywhere:** prove that the `hb_subset_*` exports exist in every native asset of HarfBuzzSharp 14.4.0.200 that Pdfwright ships (Windows x64, x86 and arm64; Linux glibc and musl on x64 and arm64; macOS), by a test that calls them on each CI runner and inside the two .NET runtime images of the CI job `linux-runtime-images`; the P/Invoke declarations (`hb_subset_input_create_or_fail`, `hb_subset_input_glyph_set`, `hb_set_add`, `hb_subset_input_set_flags` with retained glyph ids, `hb_subset_or_fail`, the face and blob release calls), a managed wrapper that owns the native handles, and a refusal with a clear message if an export is missing on some platform. If an asset lacks the exports, stop and decide before C2 (a newer HarfBuzzSharp, or the platform listed as unsupported for CFF) | 1 |
| **C2** | **CFF embedding through `hb-subset`:** subset with the glyph ids kept (so the shaper's ids and the `ToUnicode` of 0.1 still hold; a `CIDFontType0` has no `CIDToGIDMap`, so the code written for a glyph is its CID: the glyph id for a name-keyed CFF, and for a CID-keyed CFF the CID its charset gives that glyph), take the `CFF ` table from the result (a CFF2 font pinned at its default instance and checked to come back as CFF, or, if `hb-subset` keeps CFF2, embedded as `FontFile3` with `/Subtype /OpenType`, which PDF 2.0 allows; decided by what the readers accept), write `CIDFontType0` with `FontFile3` and `/Subtype /CIDFontType0C`, widths from `hmtx`; the CFF reader needed for the font's metrics and for validation at `Add` (not for subsetting); `qpdf --check`, PdfPig and pdf.js read-back on a CFF Latin font against the Latin corpus before any CJK; and the measurement of `hb-subset` against the own TrueType subsetter (size, time, read-back) that decides whether TrueType moves too. fontTools (MIT) may be used as a test-time reference, never shipped | 3 |
| **C3** | **Font collections** (`.ttc`, `.otc`): choose a font by index or by name; the public addition and its tests | 1 |
| **C4** | **CJK corpus and read-back:** Simplified Chinese, Traditional Chinese, Japanese and Korean, a short page and a long page with a table each (as the 0.1 corpus), with fonts under an open licence in the test project (Noto Sans CJK in its OpenType CFF form, subset for the repository if the full fonts are too large; their licence and source recorded as for the 0.1 fonts); PdfPig and pdf.js read-back with the same strict matcher and position check as 0.1; byte goldens and PDFium render goldens | 4 |
| **C5** | **Japanese line breaking and language forms:** the kinsoku rules of section 1 decided and tested on the corpus; `locl` forms by language checked by render goldens (one code point in four languages) | 2 |
| **T1** | **Tagged PDF:** structure tree, marked content with MCIDs, role map, parent tree, `/Alt` from `AltText`, `/Lang` on spans, header and footer as artifacts, table header cells with scope, `OutputIntent`; a `Tagged` option; veraPDF (a test-time process) for PDF/UA-1 on the whole corpus, no conformance claimed until it passes | 6 |
| **T2** | Documentation, the comparison table and the release checklist updated; **0.2.0** (tagged by the maintainer) | 2 |
| | **Total to 0.2.0** | **19 days** |

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
`Tagged = true`, and no PDF/UA claim is made until it passes everywhere. Lists (`ItemList`) are tagged as `L`, `LI`, `Lbl` and `LBody`.

## 5. Open points for the maintainer

1. **Which CJK fonts go into the test project** (Noto Sans CJK SC, TC, JP and KR in their OTF form are about 16 MB each; a repository subset
   of the corpus's characters is the recommendation, with the full-font test run only locally).
2. **Kinsoku: strict or normal** for Japanese (whether a small kana may start a line). *Recommended: strict by default, an option later if
   asked.*
3. **Performance.** The 0.1.0 measurements (`docs/baselines.md`) miss the 0.1 plan's section 7 targets; whether a performance slice comes
   before, beside or after CJK is the maintainer's choice (a CJK document has more glyphs per page, so the cost per glyph matters more).
