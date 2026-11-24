# Golden files

Each `.pdf` here is the exact output of a deterministic document, compared by **SHA-256** with what the code writes today
(`Golden.Check` in `tests/Pdfwright.Tests/Pdf/Golden.cs`). A golden file is a tripwire: any change to the shaping, the layout, the
font subsetter or the writer that moves a byte fails a test, and the person making the change decides whether the new bytes are right.

| File | Made by |
|---|---|
| `three-pages.pdf` | the writer test `PdfTestKit.ThreePages` (base-14 font, every operator the writer has) |
| `<group>-short.pdf` | the short page of each script group of `Corpus/pages` (`latin-extended`, `greek`, `cyrillic`, `arabic`, `hebrew`, `mixed`, `devanagari`, `bengali`, `tamil`, `thai`) through the public API, `GoldenDocumentTests` |
| `readme-report.pdf` | the sample report of the README (`SampleDocumentTests.ReadmeReport`: heading, paragraph, callout with columns, table over several pages, drawing, image, header and footer) |
| `<group>-short-tagged.pdf`, `readme-report-tagged.pdf` | the same documents tagged, the library's default (`PdfDocumentOptions.Tagged`) |

**Untagged and tagged.** The document goldens come in pairs. `<name>.pdf` is written with `Tagged = false`: it is the untagged output of the versions before tagged PDF (0.1.5 and earlier),
and its bytes have not changed since then, which is what proves that `Tagged = false` still writes them. `<name>-tagged.pdf` is the default output. Each tagged golden has a title and a language,
claims PDF/UA-1, passes veraPDF (`--flavour ua1`) and the structure checks of `StructureTreeReader.AssertConsistent` (`PdfUAValidationTests.The_tagged_goldens_are_pdf_ua`, which reads the committed files).
`three-pages.pdf` is made by the writer, below the document layer, and has no tagged twin.

**They are written with `Compress = false`.** The zlib behind Flate differs between runtimes (net8.0 to net11.0), so a compressed file has the same bytes
only on one of them; without compression the bytes depend on the library, the fonts and nothing else (the fonts are the Noto files of `tests/Pdfwright.Tests/Fonts`,
whose hashes `FontManifestTests` checks). `Deterministic = true` fixes the `/ID`, the dates and the order of objects.
Every golden file is stored as a binary file (`*.pdf binary` in `.gitattributes`), so that no newline conversion can change a byte.

## When a golden test fails

The message gives both hashes, writes the new bytes to the temporary folder and names the diff tool:

```
dotnet run --project tools/Pdfwright.GoldenDiff -- tests/Pdfwright.Tests/Golden/greek-short.pdf <the file the message names>
```

It prints which objects were added, removed or changed and, for a changed object, the lines of its dictionary and of its decoded content stream
(one operator for each line), or the first differing offset for a binary stream such as a font.
Read the diff: an intended change (a different glyph spacing after a shaping fix) looks like that; an unintended one (every `Tm` moved, a different subset tag) is the bug.

## Regenerating

If the change is intended, regenerate on purpose, look at the diff of the files, and **commit the regenerated goldens in a commit of their own**, with the reason in the message,
so that a reviewer sees the change of the code and the change of the output apart:

```
PDFWRIGHT_UPDATE_GOLDENS=1 tests/Pdfwright.Tests/bin/Debug/net10.0/Pdfwright.Tests -method "*golden_file*"
```

`-method "*golden_file*"` finds all twenty-three golden tests: the ten short pages and the README report in `GoldenDocumentTests`, each untagged and tagged, and `The_uncompressed_file_is_the_golden_file`
in `PdfDocumentTests`, which writes `three-pages.pdf`. `-class "*Golden*"` would miss that one (and would also run a test that regenerates nothing).
`PDFWRIGHT_UPDATE_GOLDEN=1` works too. **The update is refused when the `CI` environment variable is set** (every CI system sets it): a CI run that regenerated the goldens would turn each difference into a pass,
so the test fails with a message instead. On CI a failing golden test writes the new bytes to `PDFWRIGHT_ACTUAL_DIR` (the temporary folder when it is not set) as `pdfwright-actual-<name>.pdf`,
and the workflow keeps them as the artifact `golden-actual-<os>` when the test step fails, so the diff tool can be run on a failure that happened on another machine.
Run the golden tests once more without the variable, and on another runtime, before committing.
Never regenerate to make a red CI green without reading the diff.

## Renders (`Golden/Renders`)

The bytes say what the library wrote; they do not say what it **looks like**. A mark drawn in the wrong place or a mirrored glyph can leave the text that an extractor reads exactly as it was.
So every page of every golden document is also drawn by PDFium (the `PDFtoImage` package, test-only; the licences are in `Directory.Packages.props`) at **96 dpi**, and compared with the PNG stored in
`Golden/Renders/<name>-p<page>.png` (`RenderGolden` in `tests/Pdfwright.Tests/Pdf`; RGB, 8 bits per channel, 793 x 1122 pixels for A4, about 704 KB for the 13 files). The tests are `*_looks_like_its_stored_render`, next to the byte tests: they build the document again, so a change of the library fails both.
**The renders are drawn from the tagged documents** (the default) and compared with the PNGs, which were made from the untagged ones: tagging must not change a pixel, so there is one set of PNGs for both.

**The comparison is tight, not a hash.** A pixel differs when no pixel of the other image within one pixel of it is within 40 (of 255) of it on every colour channel: an edge that moved by one pixel and a different anti-aliasing are not differences,
a glyph that moved by more, a mirrored glyph, a missing dot, a light table rule that is gone and a changed colour are. A page fails when **more than one pixel** differs (the rule is absolute, not a share of the ink: a share let a missing Arabic dot pass; the numbers, the measured platform differences
and the sensitivity proof are in `docs/plan-0.1.md`, "S6b status"). `three-pages.pdf` has **no render golden**: it names the base-14 Helvetica without embedding it, so PDFium draws a font of its own system, and the first CI run with this rule measured 48 to 1,126 differing pixels per page on Linux and macOS against the Windows PNGs (its byte golden stays). Every page whose fonts are embedded measured 0 on all three systems.

**When a render test fails** the page as drawn (`pdfwright-actual-<name>-p<n>.png`) and a diff image (`pdfwright-diff-<name>-p<n>.png`: the stored page faded, the pixels that differ in red) are written to `PDFWRIGHT_ACTUAL_DIR`
(the temporary folder when it is not set), and the CI artifact `golden-actual-<os>` keeps them. Look at the diff image before deciding anything.

**Regenerating.** `PDFWRIGHT_UPDATE_GOLDENS=1` regenerates the PNGs with the PDFs (the same variable, refused under `CI` in the same way): `-method "*golden_file*"` for the PDFs, `-method "*stored_render*"` for the PNGs (eleven tests, which draw the tagged documents).
Regenerate the PNGs **in a commit of their own, apart from the PDFs, and from Windows** with **one runtime's test executable** (`PDFWRIGHT_UPDATE_GOLDENS=1 tests/Pdfwright.Tests/bin/Debug/net10.0/Pdfwright.Tests.exe -method "*stored_render*"`), never with `dotnet test` over several target frameworks at once (they would write the same files in parallel).
The stored files were made on Windows; Windows, Linux and macOS measured 0 differing pixels against them, so a PNG made on any of them is expected to pass on the others. Do not regenerate to follow a platform: if CI on one system fails a page that passes on another, look at the diff image; if it is a real change, regenerate, and if it is the platform, measure it and change `RenderGolden` with the numbers in the plan.
`PDFWRIGHT_RENDER_REPORT=<file>` appends one line for each page compared (differing pixels and the largest group of them touching); CI prints it.

**When PDFium cannot be loaded** (an architecture or an operating system with no native binary in the packages) the render tests are skipped, with the reason. `PDFWRIGHT_REQUIRE_RENDER=1` makes that a failure, and CI sets it on all three systems, as it does `PDFWRIGHT_REQUIRE_QPDF`.
