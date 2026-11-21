# Golden files

Each `.pdf` here is the exact output of a deterministic document, compared by **SHA-256** with what the code writes today
(`Golden.Check` in `tests/Pdfwright.Tests/Pdf/Golden.cs`). A golden file is a tripwire: any change to the shaping, the layout, the
font subsetter or the writer that moves a byte fails a test, and the person making the change decides whether the new bytes are right.

| File | Made by |
|---|---|
| `three-pages.pdf` | the writer test `PdfTestKit.ThreePages` (base-14 font, every operator the writer has) |
| `<group>-short.pdf` | the short page of each script group of `Corpus/pages` (`latin-extended`, `greek`, `cyrillic`, `arabic`, `hebrew`, `mixed`, `devanagari`, `bengali`, `tamil`, `thai`) through the public API, `GoldenDocumentTests` |
| `readme-report.pdf` | the sample report of the README (`SampleDocumentTests.ReadmeReport`: heading, paragraph, callout with columns, table over several pages, drawing, image, header and footer) |

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

`-method "*golden_file*"` finds all twelve golden tests: the ten short pages and the README report in `GoldenDocumentTests`, and `The_uncompressed_file_is_the_golden_file`
in `PdfDocumentTests`, which writes `three-pages.pdf`. `-class "*Golden*"` would miss that one (and would also run a test that regenerates nothing).
`PDFWRIGHT_UPDATE_GOLDEN=1` works too. **The update is refused when the `CI` environment variable is set** (every CI system sets it): a CI run that regenerated the goldens would turn each difference into a pass,
so the test fails with a message instead. On CI a failing golden test writes the new bytes to `PDFWRIGHT_ACTUAL_DIR` (the temporary folder when it is not set) as `pdfwright-actual-<name>.pdf`,
and the workflow keeps them as the artifact `golden-actual-<os>` when the test step fails, so the diff tool can be run on a failure that happened on another machine.
Run the golden tests once more without the variable, and on another runtime, before committing.
Never regenerate to make a red CI green without reading the diff.
