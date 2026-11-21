# pdfjs-check

Reads the PDFs that the mixed-direction test writes with Firefox's pdf.js (through Playwright) and compares the text layer with each corpus line.
It is a measuring tool, not part of CI.

1. Keep the files: run the test with `PDFWRIGHT_DUMP` set to a folder, for example
   `PDFWRIGHT_DUMP=/tmp/dump tests/Pdfwright.Tests/bin/Debug/net10.0/Pdfwright.Tests -class "*MixedDirection*"`.
2. Install Playwright with Firefox somewhere (`npm i playwright`, then `npx playwright install firefox`) and name its package folder:
   `PLAYWRIGHT_MODULE=/path/to/node_modules/playwright` (without it, `require('playwright')` is tried from the current folder upwards).
3. Run `node tools/pdfjs-check/pdfjs-check.js /tmp/dump tests/Pdfwright.Tests/Corpus [id-filter]`.

It prints `DIFF` with the code points of both sides for each line that differs, `EQUIV` for a line that differs only in the order of combining marks that are canonically
equivalent, and `pdf.js: N of M clean`. Compared: text without white space, U+034F and the bidirectional controls, in NFD.
