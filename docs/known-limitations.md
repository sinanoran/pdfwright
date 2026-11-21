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

- The writer writes to its stream synchronously and unbuffered, and holds one page's content stream in memory; the page tree is flat (see the plan).

## Copied text and the combining grapheme joiner

- A reader that shows the Unicode of a code that stands for no text of its own (pdf.js) would show the code itself, so such codes map to U+034F, and copying text out of a
  Pdfwright PDF carries it (34 of the 61 corpus lines). Beyond what is said above: it breaks plain string matching (`Contains`, `grep` over `pdftotext` output, tokenizers
  that keep it inside a word, so searching for an Arabic word in copied text can find nothing), and, because its combining class is 0, a position-ordered reader that
  places it between a letter and its own marks (a Hebrew point, an Arabic diacritic) makes the text differ from its canonical form. Reducing it needs fonts or shaping
  that draw a letter as one glyph; it is not planned for 0.1. `ActualText` instead was tried and pdf.js ignores it.
- **A zero width non-joiner (U+200C) in Persian text is copied as U+200C followed by U+034F.** pdf.js drops a glyph whose text ends with a format character, so the text of such a glyph
  gets the joiner appended; copying from Firefox's viewer then keeps the ZWNJ, and every reader sees the extra invisible character.

## Making pdf.js read the logical order

- **Glyphs with a combining mark, after a format character, or that only continue a cluster are shown under a font size of 1 or 2 with a scaled text matrix.** The drawing is the same
  (the real size is the font size times the matrix), but the content stream has more `Tf` operators, and a tool that reads the font size from `Tf` alone (a text-style inspector, some
  accessibility checkers) sees 1 or 2 for those glyphs. Cause: pdf.js merges a glyph with a mark into the open text item and then reverses right-to-left items, and starts a new item
  only when the size changes.
- **Right-to-left runs are shaped at HarfBuzz cluster level 1 (monotone characters); left-to-right runs stay at the default level.** Level 1 gives every mark its own cluster, which pdf.js
  needs; on left-to-right text it cost xpdf five Indic lines, so it is not used there. A right-to-left run therefore has slightly more clusters than HarfBuzz's default would give.
- **pdf.js reads the order of marks on one letter only up to canonical equivalence.** In the Hebrew line with two points on one letter (U+05B8 U+05C1) pdf.js reverses them; the two orders
  are canonically equivalent (same text after NFC or NFD), but not byte-equal. PdfPig reads that line byte-exact.
