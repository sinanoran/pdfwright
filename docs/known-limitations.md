# Known limitations

What Pdfwright does not do, or does in a way that a reader of the output can notice. Each entry says why. (Version 0.1.0, 22 November 2026, is the first release.)

## Text extraction and copying

- **A reader that works from the position of glyphs (xpdf's `pdftotext` in its default mode) puts spaces inside words in some
  Arabic, Indic and Thai text.** Cause: it inserts a space where a glyph is drawn to the left of the end of the glyph before it, and shaped text does that by design
  (a vowel sign that is drawn before its consonant, a mark placed by an offset). Three ways of writing the glyphs were measured and none removes it (one `Tm` per glyph 36 of 61
  corpus lines read exactly, one `Tm` per run with `TJ` 29, marks first 25). The file is correct: readers that follow the content stream (PdfPig, pdf.js, Acrobat's
  tagged reading) get the text exactly. The floor, 45 lines with the present writer (36 with the first one), is kept in `XpdfRegressionFloorTests`. Poppler's own position-ordered mode (the library behind Evince and Okular) was not measured.
- **Copying text out of a PDF made by Pdfwright can carry an invisible character, U+034F COMBINING GRAPHEME JOINER, after some letters of Arabic, Indic and Thai text.** Cause: a letter
  that the font draws as several glyphs (an Arabic letter and its dots, a consonant and a vowel sign) has one glyph that carries the text and the others map to this
  character, because a reader that is given nothing for a code (pdf.js) shows the code itself as a junk character. The joiner is invisible and does not break the joining of Arabic letters;
  what it does to searching and comparing copied text is described in the last section.

## Line breaking

- **A word wider than its box is cut between characters, with no hyphen.** Lines break at the opportunities of UAX #14 (spaces, hyphens, punctuation); a word, a number or a
  run of Thai text with no space that is wider than the width is cut between grapheme clusters as far as fits on each line, and a single glyph cluster wider than the width (one glyph or ligature, such as ffi, or a conjunct a font draws as one, which can span several grapheme clusters) is
  left whole and overflows. There is no hyphenation and no dictionary line breaking for Thai, Lao, Khmer or Burmese in 0.1, so an unspaced run of those scripts breaks mid-word; dictionary breaking is a candidate for a later version.
- **A soft hyphen (U+00AD) is never a place to break.** Nothing would draw the hyphen at the end of the line, so the character stays invisible inside its word, and a word that
  has one is cut like any other long word.
- **A paragraph is shaped once, whole, so kerning across a place where a line breaks is not undone.** A pair of letters on either side of a hyphen that the font kerns keeps its
  adjustment at the end of the line and the start of the next.
- **A tab is laid out as one space.** Tab stops belong to the block layer; the character itself is kept in the text that readers extract, and the bidirectional algorithm still sees it as a tab (a segment separator, which rule L1 puts at the paragraph level).

## Writing

- The writer writes to its stream synchronously and unbuffered, and holds one page's content stream in memory; the page tree is flat (see the plan). The document layer builds the whole file in memory before it writes it to the caller's stream.

## The document layer

- **`Save` and `SaveAsync` hold the whole file in memory.** The file is made in a buffer (`SaveAsync` on a thread-pool thread) and then copied to the stream, with the token for `SaveAsync`, so that a failed save or a limit that is hit writes nothing; the memory is about the size of the file
  (plus a page's content while it is built).
- **A paragraph is split between lines with no widow or orphan control**: one line of a paragraph can be left at the bottom of a page or carried to the top of the next. A heading is kept with the first line of the next block (the whole of a drawing, an image or a row of columns), and with the whole of a `KeepTogether` paragraph or callout that follows it when that fits a page. It is not kept with a table: a heading directly above a table can be left at the bottom of a page with the table starting on the next. `Paragraph.KeepTogether` and `Callout.KeepTogether` keep a whole block on one page.
- **There is no synthetic bold or italic.** A family without a bold font draws bold text in its nearest weight; a family without an italic font draws it upright. Supply the fonts of the styles you use.
- **A font is chosen per grapheme cluster and not per word**: a cluster takes the first font of its style's order that covers all of it, and when none does, the first that covers its first character, drawing the rest as missing glyphs (reported in `DocumentDiagnostics`). The order is the family named by the style first,
  then every other font of the collection, so a Latin family with an Arabic font in the collection draws Arabic in that font in the style's weight when one exists, else the nearest.
- **A heading's space before and a paragraph's space after are dropped at the page edge**, not carried over; a spacer is always kept (at the top of a page too: at the start of the document and after a page break) and a spacer taller than the room left is cut to the page.
- **A line break in a run (`\n`, `\r\n`, `\r`, U+0085, U+000B, U+000C, U+001C to U+001E, U+2028, U+2029) is a hard line break inside the paragraph**, wherever text is laid out (paragraphs, headings, table cells and header cells, list items, headers and footers, canvas text, `TextMeasurer`), and never starts a new paragraph (U+2029 does not either). Other control characters are removed, a lone surrogate becomes U+FFFD, and in metadata (title, subject, author, creator, language) a line break becomes a space. It is not a new paragraph with its own spacing and direction; the bidirectional algorithm treats the paragraph as one. Split text into several `Paragraph`s to get paragraph spacing and a direction for each.
- **A deterministic document with compression on is the same bytes only on the same .NET runtime**, because the zlib behind Flate differs between versions; set `Compress = false` for golden files that must match everywhere.
- **The text measurer and the flow share one engine, but a paragraph in a document is measured at the content width**; `TextMeasurer.Measure` knows no page, so give it the width you mean.
- **At most 1,000 missing glyphs are listed** in the diagnostics (each once per language and page).
- **A `FontCollection` holds native copies of its fonts** for the shaping engine and is `IDisposable`: make one for the life of the application and share it. Disposing it while a document is being saved with it is not safe (the save is not stopped); after it, saving, measuring and adding throw `ObjectDisposedException`.
- **A family that is not in the collection is replaced, not refused**: the text is set in the fonts of the collection in their order of preference, and the family is listed in `DocumentDiagnostics.MissingFamilies`.
- **A document's measurement by `TextMeasurer` matches only if made the same way**: the same fonts, default style, language, direction and line spacing as the document (the measurer takes `language` and `lineSpacing`).
- **`Length` arithmetic can still overflow to infinity** (`Length.Pt(double.MaxValue) * 2`); the factories refuse a number that is not finite, and every place that takes a length refuses one that is not.

## Tables

- **Automatic column widths come from a sample**: the header and the first 100 rows. A cell lower down that is wider than its column wraps (a word wider than the column is cut between characters); nothing is re-measured later, because the rows are streamed and a page already written cannot change. Give the column a fixed width or a fraction when the later rows differ from the first.
- **When the minimums do not fit the page, the room is shared by water-filling**: a column whose minimum is within an equal share of what is left keeps it, and only the wider columns are cut down, to an equal share of what the others leave. Two columns that are both cut get the same width whatever they wanted. The minimum of a fraction column is the table's `CellPadding`; a cell with a bigger padding of its own falls back to an inner width of 1 point.
- **The minimum width of an automatic column is its widest whole word**, measured alone in the style of its run; a place where a word could break (a hyphen) is not used to go narrower, and a word that spans runs of different styles is measured in parts.
- **There are no cell spans** (neither columns nor rows), and no vertical alignment: text is at the top of the cell.
- **A row taller than a page is split between lines**, at one height for all its cells; a paragraph's lines are not kept together, a split can fall anywhere between two lines of a cell, and the padding at the top of the cell is repeated on each page it continues on. A row that is not taller than a page is never split: it moves whole to the next page, which can leave a gap at the bottom of the previous one.
- **A header taller than half a page is not repeated**, and a table with several header rows repeats all of them or none.
- **The rows are enumerated at save time, once for each save**, so a sequence that can be enumerated only once (a reader, a one-shot iterator) works for one save only. `IAsyncEnumerable` is not accepted. Because the rows are never held, a table does not know its length: `Table.MaxRows` reads one row more than it draws, to know that there are more.
- **A heading before a table is not kept with it**: the table can start on the next page and leave the heading at the bottom of the previous one.
- **Each page's part of a table gets its own outer top and bottom line**, not an open edge; a row split between pages has them where it is cut.
- **A cell's padding is physical** (`Margins`), unlike its alignment, which is logical.

## Lists

- **The marker kinds are a bullet, decimal numbers, Arabic-Indic decimal numbers and lower and upper case letters.** No Roman numerals, no other digit sets (Persian, Devanagari, Thai and so on), no marker text of your own and no suffix other than the full stop (a bullet has none). Letters run a to z, then aa, ab and so on.
- **Lists nest one level deep**: an item holds paragraphs and one or more lists, and a nested list's items may not hold a list; deeper is an `ArgumentException`. An item holds paragraphs and nested lists only: no heading, table, image or drawing.
- **A list may be in a header or a footer** (alone, or in columns or a callout there), in a callout and in a column cell. There is no `KeepTogether` on a list as a whole; a paragraph of an item with `KeepTogether` is kept whole (it moves to the next page when it fits one, marker included), and any other item splits between pages by its lines like a paragraph, with no widow or orphan control beyond the marker being drawn with the first line of its item.
- **A bullet is U+2022, or a fallback when no font has it**: the Arabic and Hebrew fonts have no U+2022, so a list in a collection of such fonts uses the first of U+25CF, U+2219, U+00B7 and the hyphen-minus that a font has (Noto Sans Arabic and Noto Sans Hebrew get the hyphen-minus). A marker larger than the text makes the first line of its item taller; the first paragraph's space before and the last one's space after are the larger of the list's and the paragraph's own.
- **A marker starts at the start edge of its column and is not aligned to its last digit**: in a long list `9.` and `10.` start at the same edge, and the text of both starts after the widest marker. The marker is one text laid out on its own, in the style of the item's first paragraph (and `MarkerStyle`); a character no font has is reported in the diagnostics like any other.

## Columns, callouts, drawings, images, headers and footers

- **There are no links yet.** The writer can write an external URI and an internal destination, but no block exposes them: a link on a run needs the rectangle of its characters on each line it wraps to, with bidirectional runs and runs shaped in parts. Not in 0.1.
- **A row of columns, a drawing and an image are never split.** One taller than a page (columns, drawing) fails the save with an `InvalidOperationException` and nothing is written; an image is scaled down to fit instead. A row that does not fit the rest of a page moves to the next, which can leave a gap.
- **A drawing is never scaled**, and one wider than the space it is in (the content width, a cell, a callout's inside) fails the save. Give it the size of its place; the canvas's units are points.
- **Columns are equal width and one level deep.** No unequal widths, no spans, no columns inside columns (also through a callout), no table in a cell or a callout. Cells are aligned at the top.
- **A callout inside a column cell or another callout is not split between pages**; only a callout in the flow is. A split callout draws each page's part as a whole box, rounded corners included.
- **A callout nested in a column cell or another callout that is taller than a page fails the save** with an `InvalidOperationException` (it is not split, and nothing is written); only a callout in the flow is split. The same holds for a callout whose border and padding leave no room on a page for its first block, or less than 4 points of width, and for columns whose gap leaves a cell less than 4 points.
- **`CornerRadius` is cut to half the shorter side of the box**, so a large radius gives a pill, not elliptical corners.
- **`KeepTogether` exists on `Paragraph` and `Callout` only**, not on `Table` (its rows are streamed) or `Heading` (always kept with what follows). In a column cell it has no effect. A block taller than a page is split whatever it asks.
- **A picture or a drawing without `AltText` is decorative.** In a tagged document (the default since 0.1.6) `Image.AltText` and `Drawing.AltText` become the `/Alt` of a `Figure`; without them (or with a blank one) the picture is an artifact that a screen reader skips, and nothing reports it, since a decorative picture is legitimate. A picture in a header or a footer is an artifact either way. An untagged document writes `AltText` nowhere.
- **The canvas text has no wrapping:** `DrawingCanvas.DrawText` breaks only at hard line breaks; use `TextMeasurer` to wrap or fit text.
- **A header or footer is one block drawn inside the margin**, with no splitting and no table; one taller than what the margin leaves (the margin minus the offset) fails the save, and so does a block that cannot be laid out there; the exception names the page and says whether it was the header or the footer. Its callback runs once for each page after the whole body is laid out, so it should be a pure function of the `PageContext`.
- **A document with a header or a footer holds a reference and the resources of every page until the body is laid out** (not the content, which is written as each page ends). `MaxBytes` is checked as the bodies are written and at the end; the header and footer streams are added after the last body.
- **Pictures are JPEG, PNG or WebP only.** `PdfImage.FromBytes` refuses GIF, BMP, TIFF, HEIC, SVG and every other format with an `ArgumentException`;
  convert them before. Arithmetic-coded, lossless, hierarchical and 12-bit JPEGs are refused too.
- **An animated WebP is refused**, with an `ArgumentException` that says so (the animation flag of `VP8X`, or an `ANIM` or `ANMF` chunk): its first frame is not shown, as that would be a picture that is not the one the author sent. Convert it to a still picture first.
- **The colour profile of a WebP (`ICCP`) is ignored**, as the profiles of PNG and JPEG are; `EXIF` and `XMP ` are skipped.
- **The colours of a lossy WebP are libwebp's to within one level, by construction and not by comparison.** The conversion of YUV to RGB and the "fancy" upsampling of the chroma planes are ported from libwebp v1.6.0 (scalar code), and the rest of the lossy decoder from the Go port of the VP8 specification; no libwebp was run to compare the two, so a pixel-exact claim is not made. The lossless decoder is exact: its output equals the PNG the test pictures were made from.
- **A WebP is decoded whole**: a lossless one of the pixel limit (40 million) needs about 320 MB while it is decoded and holds 160 MB when it is done; a lossy one about 220 MB while it is decoded. Lower `PdfImageOptions.MaxPixels` if that is too much. **A valid lossless WebP can be tiny and still decode to the whole limit:** a picture of one colour has trees of one symbol, which are read with zero bits, so a file of 34 bytes can claim 16,384 by 2,441 pixels (40 million) and decode to 160 MB (and about 320 MB at its peak). That file is legal, so no ratio of size to pixels is checked; for pictures from untrusted sources lower `MaxPixels`. A lossy WebP does not amplify like this: its data must cover every macroblock row, and a frame that ends early is refused at the end of the first row that lacks its data, with the planes allocated only after the headers are checked. The Huffman tables of one image are capped at 64 MB, and an image that names more than 2,600 Huffman groups is refused.
- **The picture of a `PdfImage` is held decoded** (a PNG's or a WebP's samples, a JPEG's bytes) for the life of the object, and is written once to each document that uses it.

## Tagged PDF and PDF/UA-1

Tagged output is on by default since 0.1.6 (see the guide, section "Tagged PDF and accessibility", and [plan-tagged-pdf.md](plan-tagged-pdf.md)).

- **The roles follow from the block types and cannot be set by the caller.** A `Heading` is `H1` to `H6`, a `Paragraph` a `P`, a `Callout` or `Columns` a `Div`, and so on; there is no property to make a paragraph a caption, a
  block quote or a note, to mark a picture with `AltText` as decorative, or to add a role map. Roles set by the caller are not in 0.1.6.
- **Table header cells are column headers only.** The cells of a table's header rows are `TH` with `/Scope /Column`; there are no row headers (`TH` with `/Scope /Row`), no `/Headers` attribute and no `Caption`, so a
  table whose first column names the rows is read as plain data cells there. A table with a header and no body row has its header rows straight under the `Table` (no `THead` without a `TBody`, which PDF/UA-1 forbids).
- **No links are tagged, because there are none.** The public API has no link on text (see "Columns, callouts, drawings, images, headers and footers" below), so there is no `Link` element or link annotation to tag.
- **Text drawn on a canvas has no `/Lang` of its own.** It is part of its drawing's `Figure`, which the `/Alt` stands for, or of an artifact. A chart's labels in another language than the document's are not marked as such.
- **A document claims PDF/UA-1 only with a title, a language, a glyph for every character and headings in order.** Without `PdfDocumentOptions.Title` or `PdfDocumentOptions.Language` (a language set only on `DefaultStyle`
  does not count), with a language that is not a language tag, with a character no font has, with a first heading that is not level 1 or with a skipped heading level, the file is written tagged but without the
  claim, and `DocumentDiagnostics.AccessibilityIssues` says why. Heading levels are reported, not remapped. A missing glyph is still drawn as the font's `.notdef`.
- **A tagged file is larger.** About 1.2 to 2.5 times the untagged file for the benchmark documents (compressed, .NET 10). The 1,000-row table is 186 KB tagged against 76 KB untagged, over the plan's 150 KB: the
  maintainer accepted about 186 KB for tagged output on 24 November 2026, as what is left is the structure of its 7,012 elements, numbers that Flate cannot predict; the 150 KB target applies to untagged output. The
  20-page report (100 KB) and the stress document (452 KB) stay under the plan's 120 and 600 KB. A `DocumentLimits.MaxBytes` chosen for the files of 0.1.5 can be exceeded; raise it, or set `Tagged = false`, which
  writes the untagged bytes of 0.1.5.
- **No `OutputIntent`.** PDF/A needs one and PDF/UA-1 does not; PDF/A is not claimed.
- **veraPDF is the only validator used.** CI validates tagged files against PDF/UA-1 with veraPDF (a pinned version, on the Linux leg), and the tests check that `IsPdfUA` agrees with it for every file. Other checkers
  (Adobe Acrobat's accessibility check, PAC) were not run, and **no screen reader was tried by hand**: that a screen reader reads the files well is expected from the structure, not observed.

## Fonts and scripts

- **CJK is not tested or claimed in 0.1.** OpenType fonts with CFF outlines are refused, and most CJK fonts (the `.otf` Noto Sans CJK, Source Han) are such fonts. A CJK font with TrueType
  outlines is accepted: its characters are mapped (the reader takes character maps of format 4 and 12), shaped, embedded as a subset and broken between ideographs by UAX #14, but no
  CJK text is in the test corpus, so nothing about it is measured. There is no vertical text. CFF support and a CJK corpus are not there yet.
  Of a font collection file (`.ttc`, such as many system CJK fonts), only the first font is read. A font with close to 65,535 glyphs has few codes left for a glyph drawn
  for a second text (or for several different missing characters), and the save fails with an `InvalidOperationException` when they run out.
- **A mark that follows a character from another script is drawn as a missing glyph.** The font is chosen for a whole grapheme cluster, so a Hebrew point after a Latin digit (or an Arabic mark after a Hebrew letter) is shaped with the font of its base, which does not have it: it is drawn as that font's `.notdef` box and listed in `DocumentDiagnostics.MissingGlyphs`.
  The box sits on the baseline (HarfBuzz's fallback positioning used to put it below the line, off the page on the last line). Put such text in a font that has both.
- **The six script fonts of the test project have the Regular weight only**, so a heading in Arabic, Hebrew, Devanagari, Bengali, Tamil or Thai is drawn in Regular (there is no synthetic bold); a real application supplies a bold font for each script it uses.
- **Reader limits measured over the script corpus of the test suite (3120 strings in ten script groups, 308 of them distinct, read by PdfPig and by Firefox 157.0's pdf.js):** PdfPig reads every string equal to its input after the documented normalisation (U+034F removed, runs of white space one space, each string whole and its spaces where the input has them); pdf.js reads every string, two of them (Hebrew points) only up to canonical equivalence (see the last section); its text and the input are compared in NFD with white space and U+034F removed. The 61 single lines of the mixed-direction corpus were read by pdf.js 6.3.335 on 21 November 2026 (the measurement of the mixed-direction work), 61 of 61 under that comparison with the bidirectional control characters also removed. No other reader limit was found.

## Images and the canvas

- **The colour management of a PNG is ignored.** The `gAMA`, `iCCP`, `sRGB` and `cHRM` chunks are skipped, and the samples are written as device RGB, device gray or an indexed device RGB palette, so a PNG that
  carries a profile or a gamma other than the display's is drawn with its raw numbers. A 16-bit sample is rounded to 8 bits, and a gray of 1, 2 or 4 bits is stretched to 8.
- **Limits of an image: 65,535 pixels on a side and, by default, 40 million pixels in all.** Anything larger is refused with an `ArgumentException` (naming the picture's size, the limit and `PdfImageOptions.MaxPixels`) before memory is allocated, and so is a header that promises more rows than its compressed data can hold.
  The pixel limit applies to every format (JPEG, PNG and WebP): a JPEG is passed through undecoded, so it costs little here, but a PDF reader decodes it to a bitmap of 3 or 4 bytes a pixel. `PdfImage.FromBytes(bytes, new PdfImageOptions { MaxPixels = n })` raises
  or lowers the pixel limit, from 1 to 100,000,000 (the side limit stays). A PNG is decoded whole: besides the file itself, the reader holds the compressed image data once (a copy of the IDAT chunks of exactly their size, up to about the size of the file) and the decoded samples
  (4 bytes a pixel for an RGBA picture, so about 160 MB at the default limit and 400 MB at the largest, and 3 bytes a pixel, 300 MB at the largest, for RGB), and one row at a time otherwise. The largest limit is chosen so that the arrays of the decoder (3 bytes a pixel in one array, at most 2,147,483,591 elements) stay far inside their size.
- **A chunk of a PNG that the reader does not know and that is critical** (its type begins with a capital letter) is refused (`PdfImage.FromBytes` throws an `ArgumentException`), as the format says a
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
