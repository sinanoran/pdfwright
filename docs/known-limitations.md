# Known limitations

What Pdfwright does not do, or does in a way that a reader of the output can notice. Each entry says why. (Pre-release: nothing is published yet.)

## Text extraction and copying

- **A reader that works from the position of glyphs (xpdf's `pdftotext` in its default mode, which Evince and Okular also use for copy) puts spaces inside words in some
  Arabic, Indic and Thai text.** Cause: it inserts a space where a glyph is drawn to the left of the end of the glyph before it, and shaped text does that by design
  (a vowel sign that is drawn before its consonant, a mark placed by an offset). Three ways of writing the glyphs were measured and none removes it (one `Tm` per glyph 36 of 61
  corpus lines read exactly, one `Tm` per run with `TJ` 29, marks first 25). The file is correct: readers that follow the content stream (PdfPig, pdf.js, Acrobat's
  tagged reading) get the text exactly. The floor, 45 lines with the present writer (36 with the first one), is kept in `XpdfRegressionFloorTests`.
- **Copying text out of a PDF made by Pdfwright can carry an invisible character, U+034F COMBINING GRAPHEME JOINER, after some letters of Arabic, Indic and Thai text.** Cause: a letter
  that the font draws as several glyphs (an Arabic letter and its dots, a consonant and a vowel sign) has one glyph that carries the text and the others map to this
  character, because a reader that is given nothing for a code (pdf.js) shows the code itself as a junk character. The joiner is invisible and does not break the joining of Arabic letters;
  what it does to searching and comparing copied text is described in the last section.

## Line breaking

- **A word wider than its box is cut between characters, with no hyphen.** Lines break at the opportunities of UAX #14 (spaces, hyphens, punctuation); a word, a number or a
  run of Thai text with no space that is wider than the width is cut between grapheme clusters as far as fits on each line, and a single glyph cluster wider than the width (one glyph or ligature, such as ffi, or a conjunct a font draws as one, which can span several grapheme clusters) is
  left whole and overflows. There is no hyphenation and no dictionary line breaking for Thai, Lao, Khmer or Burmese in 0.1.
- **A soft hyphen (U+00AD) is never a place to break.** Nothing would draw the hyphen at the end of the line, so the character stays invisible inside its word, and a word that
  has one is cut like any other long word.
- **A paragraph is shaped once, whole, so kerning across a place where a line breaks is not undone.** A pair of letters on either side of a hyphen that the font kerns keeps its
  adjustment at the end of the line and the start of the next.
- **A tab is laid out as one space.** Tab stops belong to the block layer; the character itself is kept in the text that readers extract, and the bidirectional algorithm still sees it as a tab (a segment separator, which rule L1 puts at the paragraph level).

## Writing

- The writer writes to its stream synchronously and unbuffered, and holds one page's content stream in memory; the page tree is flat (see the plan). The document layer builds the whole file in memory before it writes it to the caller's stream.

## The document layer (S5a)

- **`Save` and `SaveAsync` hold the whole file in memory.** The file is made in a buffer (`SaveAsync` on a thread-pool thread) and then copied to the stream, with the token for `SaveAsync`, so that a failed save or a limit that is hit writes nothing; the memory is about the size of the file
  (plus a page's content while it is built).
- **A paragraph is split between lines with no widow or orphan control**: one line of a paragraph can be left at the bottom of a page or carried to the top of the next. A heading is kept with the first line of the next block and the space before it, and only that; there is no keep-together block yet (S5b).
- **There is no synthetic bold or italic.** A family without a bold font draws bold text in its nearest weight; a family without an italic font draws it upright. Supply the fonts of the styles you use.
- **A font is chosen per grapheme cluster and not per word**: a cluster takes the first font of its style's order that covers all of it, and when none does, the first that covers its first character, drawing the rest as missing glyphs (reported in `DocumentDiagnostics`). The order is the family named by the style first,
  then every other font of the collection, so a Latin family with an Arabic font in the collection draws Arabic in that font in the style's weight when one exists, else the nearest.
- **A heading's space before and a paragraph's space after are dropped at the page edge**, not carried over; a spacer is always kept (at the top of a page too: at the start of the document and after a page break) and a spacer taller than the room left is cut to the page.
- **A line break in a run (`\n`, `\r\n`, U+2029, U+0085) is a hard line break inside the paragraph**, not a new paragraph with its own spacing and direction; the bidirectional algorithm treats the paragraph as one. Split text into several `Paragraph`s to get paragraph spacing and a direction for each.
- **A deterministic document with compression on is the same bytes only on the same .NET runtime**, because the zlib behind Flate differs between versions; set `Compress = false` for golden files that must match everywhere.
- **The text measurer and the flow share one engine, but a paragraph in a document is measured at the content width**; `TextMeasurer.Measure` knows no page, so give it the width you mean.
- **At most 1,000 missing glyphs are listed** in the diagnostics (each once per language and page).
- **A `FontCollection` holds native copies of its fonts** for the shaping engine and is `IDisposable`: make one for the life of the application and share it. Disposing it while a document is being saved with it is not safe (the save is not stopped); after it, saving, measuring and adding throw `ObjectDisposedException`.
- **A family that is not in the collection is replaced, not refused**: the text is set in the fonts of the collection in their order of preference, and the family is listed in `DocumentDiagnostics.MissingFamilies`.
- **A document's measurement by `TextMeasurer` matches only if made the same way**: the same fonts, default style, language, direction and line spacing as the document (the measurer takes `language` and `lineSpacing`).
- **`Length` arithmetic can still overflow to infinity** (`Length.Pt(double.MaxValue) * 2`); the factories refuse a number that is not finite, and every place that takes a length refuses one that is not.

## Tables (S5b)

- **Automatic column widths come from a sample**: the header and the first 100 rows. A cell lower down that is wider than its column wraps (a word wider than the column is cut between characters); nothing is re-measured later, because the rows are streamed and a page already written cannot change. Give the column a fixed width or a fraction when the later rows differ from the first.
- **The minimum width of an automatic column is its widest whole word**, measured alone in the style of its run; a place where a word could break (a hyphen) is not used to go narrower, and a word that spans runs of different styles is measured in parts.
- **There are no cell spans** (neither columns nor rows), and no vertical alignment: text is at the top of the cell.
- **A row taller than a page is split between lines**, at one height for all its cells; a paragraph's lines are not kept together, a split can fall anywhere between two lines of a cell, and the padding at the top of the cell is repeated on each page it continues on. A row that is not taller than a page is never split: it moves whole to the next page, which can leave a gap at the bottom of the previous one.
- **A header taller than half a page is not repeated**, and a table with several header rows repeats all of them or none.
- **The rows are enumerated at save time, once for each save**, so a sequence that can be enumerated only once (a reader, a one-shot iterator) works for one save only. `IAsyncEnumerable` is not accepted. Because the rows are never held, a table does not know its length: `Table.MaxRows` reads one row more than it draws, to know that there are more.
- **A heading before a table is not kept with it**: the table can start on the next page and leave the heading at the bottom of the previous one.
- **Each page's part of a table gets its own outer top and bottom line**, not an open edge; a row split between pages has them where it is cut.
- **A cell's padding is physical** (`Margins`), unlike its alignment, which is logical.

## Images and the canvas

- **The colour management of a PNG is ignored.** The `gAMA`, `iCCP`, `sRGB` and `cHRM` chunks are skipped, and the samples are written as device RGB, device gray or an indexed device RGB palette, so a PNG that
  carries a profile or a gamma other than the display's is drawn with its raw numbers. A 16-bit sample is rounded to 8 bits, and a gray of 1, 2 or 4 bits is stretched to 8.
- **Limits of an image: 65,535 pixels on a side and 100 million pixels in all.** Anything larger is refused with an exception before memory is allocated, and so is a header that promises more rows than its compressed data can hold.
  A PNG is decoded whole: besides the file itself, the reader holds the compressed image data once (a copy of the IDAT chunks of exactly their size, up to about the size of the file) and the decoded samples
  (4 bytes a pixel for an RGBA picture, so about 400 MB for one of 100 million pixels, and 300 MB for RGB), and one row at a time otherwise.
- **A chunk of a PNG that the reader does not know and that is critical** (its type begins with a capital letter) is refused with an `ImageFormatException`, as the format says a
  decoder must; an unknown ancillary chunk is skipped.
- **The canvas writes the operands of a transform (`cm`) with ten significant digits, and everything else it writes (path coordinates, line widths, dash lengths) with five decimals.** A transform that
  shrinks or turns by a tiny amount keeps its value, and one that cannot be undone (a determinant of zero, or beyond the range of a number) is refused. But a path under a large scale, in data units with
  fractions below 0.00001, loses them (`Scale(1e6)` with a coordinate of 0.000003 draws it at 0): scale the data into units of about 1 or more before drawing,
  and keep the canvas scale near 1. A rectangle of 1e8 under a scale of 1e-6 is exact.
- **A JPEG is passed through, not decoded.** Its markers are read for the size, the number of components and the Adobe marker, and the file is written as it is; a reader that cannot decode a file the
  walk accepted will not show it. Arithmetic coding, 12-bit samples, lossless and hierarchical modes and a height given in a later segment are refused; an Adobe CMYK file is assumed to be inverted
  (`/Decode [1 0 1 0 1 0 1 0]`). The embedded colour profile and the orientation of a JPEG are ignored.
- **A PNG's samples are compressed by the document, not by the image.** With the compression of the document switched off (`PdfCompression.None`) they are written as they are, and a very small image
  (under 64 bytes) is not compressed in any case.
- **The canvas assumes the graphics state of the page is at its defaults when it starts**, so a page that set an opacity or a colour itself before starting a canvas has that state inside it.

## Copied text and the combining grapheme joiner

- A reader that shows the Unicode of a code that stands for no text of its own (pdf.js) would show the code itself, so such codes map to U+034F, and copying text out of a
  Pdfwright PDF carries it (34 of the 61 corpus lines). Beyond what is said above: it breaks plain string matching (`Contains`, `grep` over `pdftotext` output, tokenizers
  that keep it inside a word, so searching for an Arabic word in copied text can find nothing), and, because its combining class is 0, a position-ordered reader that
  places it between a letter and its own marks (a Hebrew point, an Arabic diacritic) makes the text differ from its canonical form. Reducing it needs fonts or shaping
  that draw a letter as one glyph; it is not planned for 0.1. `ActualText` instead was tried and pdf.js ignores it.
- **A zero width non-joiner (U+200C) in Persian text is copied as U+200C followed by U+034F.** pdf.js drops a glyph whose text ends with a format character, so the text of such a glyph
  gets the joiner appended; copying from Firefox's viewer then keeps the ZWNJ, and every reader sees the extra invisible character.

## Making pdf.js read the logical order

- **Glyphs with a combining mark, a text that ends with a format character, or that only continue a cluster are shown under a font size of 1 or 2 with a scaled text matrix.** The drawing is the same
  (the real size is the font size times the matrix), but the content stream has more `Tf` operators, and a tool that reads the font size from `Tf` alone (a text-style inspector, some
  accessibility checkers) sees 1 or 2 for those glyphs. Cause: pdf.js merges a glyph with a mark into the open text item and then reverses right-to-left items, and starts a new item
  only when the size changes.
- **Right-to-left runs are shaped at HarfBuzz cluster level 1 (monotone characters); left-to-right runs stay at the default level.** Level 1 gives every mark its own cluster, which pdf.js
  needs; on left-to-right text it cost xpdf five Indic lines, so it is not used there. A right-to-left run therefore has slightly more clusters than HarfBuzz's default would give.
- **pdf.js reads the order of marks on one letter only up to canonical equivalence.** In the Hebrew line with two points on one letter (U+05B8 U+05C1) pdf.js reverses them; the two orders
  are canonically equivalent (same text after NFC or NFD), but not byte-equal. PdfPig reads that line byte-exact.
