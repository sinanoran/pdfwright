# Plan: tagged (accessible) PDF in 0.1.6

> Written on 24 November 2026. The design is section 6 of [`plan-0.1.md`](plan-0.1.md), with the room the 0.1 API leaves for it; slices
> T1 and T2 of [`plan-0.2.md`](plan-0.2.md) were its first plan. On 23 November 2026 the maintainer moved tagged PDF ahead of 0.2: it ships
> as **0.1.6**. This plan replaces T1 and T2 there.

## 1. Decisions (the maintainer, 23 November 2026)

1. **Version 0.1.6.** The new public members are additive.
2. **On by default.** `PdfDocumentOptions.Tagged` is `true` unless the caller sets it to `false`. Every document's bytes change, so the
   CHANGELOG says so plainly. `Tagged = false` writes today's bytes: the existing golden files stay as they are, as the untagged goldens, and
   their tests set `Tagged = false`; tagged goldens are added beside them.
3. **veraPDF in CI** validates PDF/UA-1 on the corpus, the README report, the samples and the large tables. It is a test-time tool (Java),
   not a dependency of the library. No conformance is claimed until it passes.
4. **A document without a title or a language** (both required by PDF/UA-1) is written tagged, without the PDF/UA claim, and the gap is
   reported in the diagnostics.
5. **A missing glyph** is drawn as today (the font's `.notdef`), reported, and the document makes no claim (Matterhorn 7.21.8).
6. **An `Image` or a `Drawing` without `AltText`** is decorative: an artifact, which a screen reader skips.
7. **The claim** (`pdfuaid:part` 1 in the XMP metadata) is written automatically when the document is tagged and no accessibility issue was
   reported; the diagnostics say whether it was.

Settled with the recommended default, not asked: skipped heading levels are reported, not remapped; the allocation budgets stay those of
untagged output, and a guard is added for tagged output; row headers (`TH` with `/Scope /Row`) and roles set by the caller are not in 0.1.6;
no `OutputIntent` (PDF/A needs it, PDF/UA-1 does not).

## 2. Design

- **Elements are created when their content is first drawn.** Each block gets a light node at build time (its role, its parent, `Alt`,
  `Lang`, attributes); the structure element and its object are made when the first marked content for it is drawn, so the structure order is
  the drawing order, which is the reading order. **Elements are written when they close**, so the memory is the depth of the tree, not its size
  (a 10,000-row table is streamed as before).
- **Marked content.** Every piece of text goes through one place in the renderer, which wraps it in `/<role> <</MCID n>> BDC ... EMC`; a run
  in another language than its paragraph is a `/Span <</Lang (..)>>`. Text that continues on another page gets an MCID on that page under the
  same element (a marked-content reference). MCIDs are counted per page.
- **Artifacts:** the header and the footer (pagination artifacts), table fills and borders, callout boxes, header rows repeated on later
  pages (the first header row is the `TH` row), images and drawings without alternative text.
- **Roles:** `Document`; `H1` to `H6` and `P`; `Div` for a callout and for columns (and each column cell); `L`, `LI`, `Lbl` and `LBody` for
  lists, nested lists inside the item's `LBody`; `Table`, `THead`, `TBody`, `TR`, `TH` with `/Scope /Column`, `TD` (one `P` per paragraph when
  a cell has several; an empty cell is an empty `TD`); `Figure` with `/Alt` and a bounding box for an image or a drawing with alternative text,
  whose canvas text is part of the figure. Standard types only, so no role map.
- **The file:** `StructTreeRoot` with a flat parent tree, `/StructParents` and `/Tabs /S` on every page, `/MarkInfo <</Marked true>>`,
  `DisplayDocTitle` (already written when a title is set), `/Lang` on the catalog (already written) and on elements and spans of another
  language.
- **Public API (additive):** `PdfDocumentOptions.Tagged` (default `true`), `DocumentDiagnostics.IsPdfUA`,
  `DocumentDiagnostics.AccessibilityIssues`, `AccessibilityIssue` (kind, page, message) and `AccessibilityIssueKind`.

## 3. Slices, one pull request each

| Slice | What | Output |
|---|---|---|
| **T1a** | Writer plumbing behind an internal flag: marked-content operators, MCIDs per page, the structure tree writer, the parent tree, `StructParents`, `Tabs`, `MarkInfo`; a test reader of the structure tree | unchanged (every golden passes) |
| **T1b** | Flow text: `Document`, `P`, headings, paragraphs across pages, `/Lang` on elements and spans, header and footer artifacts, callouts and columns as `Div` | unchanged by default |
| **T1c** | Lists and figures | unchanged by default |
| **T1d** | Tables, fills and borders as artifacts, repeated headers, split rows, the 1,000-row table's memory | unchanged by default |
| **T1e** | veraPDF in CI (ubuntu, a pinned version, `--flavour ua1`), the accessibility issues and the claim; fixes for what veraPDF finds | unchanged by default |
| **T1f** | `Tagged` public and on by default, the diagnostics API, tagged goldens beside the untagged ones, samples with a title and a language | **changed**: every document is tagged |
| **T2** | Guide, known limitations, README comparison table, CHANGELOG, `RELEASING.md`, release notes; **0.1.6** after the maintainer's yes | |

Each slice: built in its own branch, reviewed independently, CI green on every job, merged. veraPDF runs from T1e on; until then the
structure is checked by the test reader and `qpdf --check`.

## 4. veraPDF in CI

On the ubuntu leg only (the goldens already prove the bytes are the same on every system): Java from `actions/setup-java` (Temurin 21), a
pinned veraPDF release installed from its installer with a checked SHA-256 and cached, with the retry loop of the qpdf install. The tests find
it through `PDFWRIGHT_VERAPDF` or the path and skip without it, as they do without qpdf; `PDFWRIGHT_REQUIRE_VERAPDF=1` on that leg makes its
absence a failure. One veraPDF process validates a batch of files (the JVM starts once), and a failure names the rules. The tests also check
that `IsPdfUA` agrees with veraPDF for every file.
