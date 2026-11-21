# The corpus

One short line per case, in three files by script family. Format:

```
<case id>|<fonts, "+" separated, in fallback order>|<text>
```

A line that begins with `#` is a comment. The ids are unique across the three files.

**What the corpus is for.** Each case is a string the engine must draw with the listed fonts and a
PDF reader must be able to read back **in logical order**: that read-back is the acceptance test of the
whole package (section 8 of `docs/plan-0.1.md`), and the one Skia's PDF backend failed for Arabic and
Hebrew in the spike behind the plan. Until the writer exists, `CorpusTests` checks what can be checked
today: the files are strict UTF-8 with no byte-order mark, in Unicode normalization form C (so a case is
what it looks like), the ids are unique, no id or text is empty, every listed font is in the font manifest,
and **every character of every case has a glyph in at least one of the listed fonts** (a case a font cannot
draw would be a missing-glyph test by accident; spaces and the zero-width and direction marks U+200C to
U+200F need no glyph). A script font has no punctuation, so a case with a colon or a comma lists Noto Sans
too. Nukta forms such as फ़ are composition exclusions: their NFC form is the letter plus U+093C.

**What each group exercises.**

| File | Cases | What they try |
|---|---|---|
| `latin.txt` | Turkish (dotted and dotless I, in the text and as a pair), German, Polish, Vietnamese (stacked diacritics, capital Đ), French (a no-break space before `?`), bold and italic, numbers, Greek (final sigma), Cyrillic (Russian, Ukrainian with the apostrophe), kerning pairs and fi/fl ligature candidates, very long unbreakable words | Latin Extended coverage and the case pairs that differ by locale as glyph coverage only (nothing in a writer changes case), kerning and ligatures, weights and styles, a word wider than its line |
| `arabic-hebrew.txt` | Arabic (joining, lam-alef in joined and hamza forms, harakat, shadda and dagger alef, tatweel, Arabic-Indic digits, Persian letters and the zero-width non-joiner), Hebrew (letters, points with dagesh, final forms, maqaf and gershayim), and **mixed direction** (English in Arabic and Arabic in English, numbers with signs, Arabic-Indic and Western digits together, a right-to-left paragraph that begins with a Latin word after U+200F, brackets and quotes inside right-to-left text, a Latin phrase between Hebrew words, a phone number, three scripts in one line), and long space-less words | joining and ligatures, mark placement, bidirectional reordering, mirrored brackets, numbers inside right-to-left text, logical-order extraction, a word wider than its line |
| `indic-thai.txt` | Devanagari (conjuncts, reordering signs, nukta, chandrabindu, reph, a long conjunct word), Bengali (conjuncts, reph, pre-base vowels), Tamil (split vowel signs, simple pre-base signs, Grantha letters), Thai (stacked marks), native digits of the four scripts, each also beside Latin | reordering, half forms and conjunct ligatures, mark stacking, grapheme-cluster-safe breaking; Thai has no spaces between words (word breaking is a documented limit of 0.1) |

**Reading a mixed-direction case.** The correct rendering of some of them looks wrong to a reader who expects
the order they typed, and is right under the Unicode bidirectional algorithm: digits after Arabic letters are
numbers that keep their order inside a right-to-left run, a minus sign before them takes the direction of the
run it follows (so `-15%` shows as `%15-`), a date written `2026-12-31` shows reordered, and the `+` of
`+972-3-1234567` ends up on the right of the number in a Hebrew sentence. These are the cases where the
extraction in logical order matters most, and where a reviewer should compare the picture with the *Unicode
Bidirectional Algorithm* (UAX #9) and not with the line in this file. The file format has no base-direction
column: a case takes the direction of its first strong character, which is why one case begins with U+200F.

The strings are short and ordinary on purpose: a phrase a speaker of the language reads at a glance, so a
native-speaker review before 0.1.0 (a release-checklist item) is a matter of minutes per script. They are
original texts or common phrases, not copied passages; religious and political phrases are left out on
purpose.

## The page corpus (slice S6a): `pages/`

The lines above are single strings. `pages/<group>.txt` are whole documents: one file for each script group (`latin-extended`, `greek`, `cyrillic`, `arabic`, `hebrew`,
`mixed`, `devanagari`, `bengali`, `tamil`, `thai`), each giving a **short page** (a heading and a few paragraphs) and a **long page** (a heading, paragraphs and a
three-column table of about 100 rows, which runs over several pages and repeats its header). The text is data, not code, so a reviewer who reads the language can fix it in a diff.
The tests build the documents through the public API only (`PdfDocument`, `FontCollection`, `Heading`, `Paragraph`, `Table`) with all nine Noto fonts in the collection and the family of the
file as the default style: `PageCorpus` in `tests/Pdfwright.Tests/Conformance`.

```
# a comment
@group arabic              the group's name (also the name of its golden file)
@family Noto Sans Arabic   the default family of the document; the other fonts of the collection draw what it lacks
@direction rtl             ltr, rtl or auto: the direction of the document
@rows 100                  the table has this many rows: the `row` lines are repeated in turn until there are so many
@language tr               from here on, the language of each text (`@language -` for none): it selects the shaping features (Turkish i, Persian forms)
<page>|<kind>|<text>...    page is short or long; kind is heading, heading2, para, head (the table header) or row; head and row have one text for each column, separated by |
```

A text may not contain `|`; every file is strict UTF-8, in Unicode normalization form C, with no byte-order mark (the loader asserts it).

**What the tests do with them.**

- `ScriptConformanceTests`: PdfPig reads each document back and every heading, paragraph and table cell must be equal to its input, in logical order. The normalisation is exactly this: every
  character for which `char.IsWhiteSpace` is true is removed from both sides, and so is U+034F COMBINING GRAPHEME JOINER (which Pdfwright writes after some Arabic, Indic and Thai letters, see
  `docs/known-limitations.md`). Nothing else is touched: the right-to-left mark, the zero-width non-joiner and the order of combining marks are compared as they are. A string passes when
  the text read starts with it where the previous string ended; the header of a table, drawn again at the top of each later page, may stand between two strings and is counted separately
  (a long page with *n* pages must repeat it *n* − 1 times).
- `FailureCaseTests`: a character no font has (the diagnostics name it, the language and the page), a font that may not be embedded (`FontNotEmbeddableException`), a family that is not in the collection
  (`MissingFamilies`), for every script.
- `GoldenDocumentTests`: the short page of each group is a golden file (`tests/Pdfwright.Tests/Golden/README.md`).
- `PropertyTests`: random strings from these files' alphabets (`PageCorpus.Alphabet`).
- With `PDFWRIGHT_DUMP=<folder>` the extraction test keeps each document as `<group>-<short|long>.pdf` with its strings in `<group>-<short|long>.expected.txt`, which
  `tools/pdfjs-check` reads with Firefox's pdf.js (see its README).

**Which scripts have no font here.** None of the groups of the plan: Latin Extended, Greek and Cyrillic use Noto Sans; Arabic, Hebrew, Devanagari, Bengali, Tamil and Thai have a Noto font each.
Only the Regular weight exists for the six script fonts (Noto Sans has Regular, Bold and Italic), so a heading in those scripts is drawn in the nearest weight, which is Regular.
