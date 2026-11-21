# Fonts of the test corpus

Nine Noto fonts, all **SIL Open Font License 1.1**, from the Noto project's `notofonts.github.io`
repository at commit `739604e080c772760ae37106fa0013805251eb49` (20 November 2026), the `hinted/ttf`
static builds. They are **test and corpus assets**: the package ships no font, and an application
supplies its own (section 5 of `docs/plan-0.1.md`).

`manifest.json` is the record: each font's family, style, source URL, copyright line (read from the font's
own name table), licence file and SHA-256. The tests (`FontManifestTests`) fail for a font that is not in
the manifest, a file whose hash changed, a missing licence file, and a font whose OS/2 `fsType` forbids
embedding.

The licence texts are the ones each script's repository ships (`OFL-<repository>.txt`); the copyright line
at the top of each names its project. The SIL OFL allows the fonts to be bundled and redistributed with
software and forbids selling them on their own, and it requires the licence text to travel with them, which is
why the texts live in this folder. The fonts are not renamed or modified. `NOTICE` at the repository root
lists them too.

| Font | Scripts the corpus draws with it |
|---|---|
| Noto Sans (Regular, Bold, Italic) | Latin (including Turkish, German, Polish, Vietnamese, French), Greek, Cyrillic, digits and punctuation |
| Noto Sans Arabic | Arabic and Persian |
| Noto Sans Hebrew | Hebrew |
| Noto Sans Devanagari | Hindi and Marathi |
| Noto Sans Bengali | Bengali |
| Noto Sans Tamil | Tamil |
| Noto Sans Thai | Thai |

CJK, emoji and colour fonts are not in the corpus: they are outside 0.1 (section 4 of the plan).
