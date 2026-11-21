# Unicode data

The files in `18.0.0/` are the official files of the Unicode Character Database, version 18.0.0, downloaded from
`https://www.unicode.org/Public/18.0.0/ucd/` (found through `.../Public/UCD/latest/ucd/`, which held 18.0.0) on 21 November 2026 and kept byte for byte (`SHA256SUMS` records each
hash and a test checks it; `.gitattributes` turns line-ending conversion off for this folder).

| File | Used by |
|---|---|
| `UnicodeData.txt` | the generator: canonical equivalents of the brackets |
| `extracted/DerivedBidiClass.txt` (stored here as `DerivedBidiClass.txt`) | the generator: the bidirectional class of every code point |
| `BidiBrackets.txt` | the generator: paired brackets (mirroring is left to HarfBuzz) |
| `BidiTest.txt`, `BidiCharacterTest.txt` | the conformance tests of the bidirectional algorithm |
| `LineBreak.txt`, `EastAsianWidth.txt`, `emoji-data.txt` | the generator: line break classes, East Asian Width, Extended_Pictographic |
| `GraphemeBreakProperty.txt`, `DerivedCoreProperties.txt` | the generator: grapheme cluster break classes, Indic_Conjunct_Break |
| `LineBreakTest.txt`, `GraphemeBreakTest.txt` | the conformance tests of line breaking and grapheme clusters |

They are read as data by `tools/Pdfwright.UnicodeGen` and by the tests, and never executed. The tables the library uses are
`src/Pdfwright/Unicode/BidiTables.g.cs` and `CharacterTables.g.cs`, produced by the generator; a test regenerates them and compares.
`LICENSE-UNICODE.txt` is the Unicode License v3 that covers these files.

To raise the Unicode version: download the new files into a new folder, update `SHA256SUMS`, run

```
dotnet run --project tools/Pdfwright.UnicodeGen -- data/unicode/<version> src/Pdfwright/Unicode
```

and change the paths in the tests and the version in the generator.
