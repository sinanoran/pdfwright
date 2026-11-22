# WebP test pictures

These files are test data for the WebP decoder (`src/Pdfwright/Images/Webp*.cs`, `Vp8*.cs`, `Vp8l*.cs`; see `docs/decisions/0001-webp-decoder.md`). They were downloaded once, byte for byte, from the `testdata` folder of
golang.org/x/image at the tag v0.48.0, are used as they are, and have the licence of that repository (BSD-3-Clause, Copyright 2009 The Go Authors; the text is in `NOTICE`). The maintainer approved exactly these files and no others. A file that has no
match in the table below is not from there. `.webp` and `.png` files are stored as binary files (`.gitattributes`).

| Picture | What it is | How the tests use it |
|---|---|---|
| `blue-purple-pink.lossless.webp` and `.png` | lossless, 1 colour table, predictor and cross-colour transforms | pixels equal the PNG |
| `yellow_rose.lossless.webp` and `.png` | lossless photograph | pixels equal the PNG |
| `tux.lossless.webp` and `.png` | lossless with transparency | pixels and alpha equal the PNG |
| `gopher-doc.1bpp`, `.2bpp`, `.4bpp`, `.8bpp` `.lossless.webp` and `.png` | lossless, colour-indexing transform with 1, 2, 4 and 8 bits for each pixel | pixels equal the PNG |
| `gopher-doc.with-alpha.lossless.webp` and `.png` | lossless with transparency | pixels and alpha equal the PNG |
| `gopher-doc.skip-hgroup.lossless.webp` | lossless, with Huffman groups that no tile uses | pixels equal `gopher-doc.8bpp.png` (as Go's own test does) |
| `blue-purple-pink.lossy.webp` | lossy (VP8) | close to `blue-purple-pink.png` (the PNG is the source it was compressed from) |
| `yellow_rose.lossy.webp` | lossy | close to `yellow_rose.png` |
| `yellow_rose.lossy-with-alpha.webp` | lossy with an alpha plane (VP8X and ALPH) | colour close to `yellow_rose.png`, alpha present |
| `video-001.lossy.webp` | lossy, a still from a video | decodes, with the size its header gives |

## Credits and licences of the pictures themselves

These pictures are test data only and are not in the package. The licence of the repository (BSD-3-Clause) covers the files as distributed there; where a picture has an author of its own, it is credited here (and in `NOTICE`):

- `gopher-doc.*`: the Go gopher was designed by Renée French, licensed under Creative Commons Attribution 3.0 (https://creativecommons.org/licenses/by/3.0/).
- `tux.*`: Tux, by Larry Ewing, drawn with The GIMP; the permission to use it is given provided that Larry Ewing and The GIMP are acknowledged.
- `yellow_rose.*` and `blue-purple-pink.*`: distributed in the golang.org/x/image testdata under that repository's BSD-3-Clause licence; the original source is not stated there.

## Where each file comes from

| File | Source URL | Version | Licence | SHA-256 |
|---|---|---|---|---|
| `blue-purple-pink.lossless.webp` | https://raw.githubusercontent.com/golang/image/v0.48.0/testdata/blue-purple-pink.lossless.webp | golang/image v0.48.0 | BSD-3-Clause | `6f318d786d3eef23427c65186a089959c36339cd3abb2cb4c839886aa1a8d677` |
| `blue-purple-pink.lossy.webp` | https://raw.githubusercontent.com/golang/image/v0.48.0/testdata/blue-purple-pink.lossy.webp | golang/image v0.48.0 | BSD-3-Clause | `af8e87f21fa9fcb8e74c12d31d61a56b1d8e06819038efd10119b0f0726bfab4` |
| `blue-purple-pink.png` | https://raw.githubusercontent.com/golang/image/v0.48.0/testdata/blue-purple-pink.png | golang/image v0.48.0 | BSD-3-Clause | `304fa9500e96c7dab6f86ee8ba2413effdf10f231c43e0af09c5eb025b363799` |
| `gopher-doc.1bpp.lossless.webp` | https://raw.githubusercontent.com/golang/image/v0.48.0/testdata/gopher-doc.1bpp.lossless.webp | golang/image v0.48.0 | BSD-3-Clause | `335348a182a3fc65e0a1a59a53555cdea0c2cd60373acf44b091a0304ff2504a` |
| `gopher-doc.1bpp.png` | https://raw.githubusercontent.com/golang/image/v0.48.0/testdata/gopher-doc.1bpp.png | golang/image v0.48.0 | BSD-3-Clause | `3611efecf009a8daa5b51cbb171aed83c448dd75b0716ac46293cf643ac29d7a` |
| `gopher-doc.2bpp.lossless.webp` | https://raw.githubusercontent.com/golang/image/v0.48.0/testdata/gopher-doc.2bpp.lossless.webp | golang/image v0.48.0 | BSD-3-Clause | `5884cb058d20c8099f9fc8e8208df21dd17151bb1257f6f742468e6bfd31ee8f` |
| `gopher-doc.2bpp.png` | https://raw.githubusercontent.com/golang/image/v0.48.0/testdata/gopher-doc.2bpp.png | golang/image v0.48.0 | BSD-3-Clause | `9cd0aaa77769311b6a0fa8bfbd3b6f3ff16d9cd37e107c95e522648e7c62b380` |
| `gopher-doc.4bpp.lossless.webp` | https://raw.githubusercontent.com/golang/image/v0.48.0/testdata/gopher-doc.4bpp.lossless.webp | golang/image v0.48.0 | BSD-3-Clause | `9f1fff55b9b2f960141ed09cddf38a602d233ba2323dfb868ee2adbcd8194a8e` |
| `gopher-doc.4bpp.png` | https://raw.githubusercontent.com/golang/image/v0.48.0/testdata/gopher-doc.4bpp.png | golang/image v0.48.0 | BSD-3-Clause | `c4074a044a2d1192eee32125631de5cda0ce276b5e5c5332dec0262675902d28` |
| `gopher-doc.8bpp.lossless.webp` | https://raw.githubusercontent.com/golang/image/v0.48.0/testdata/gopher-doc.8bpp.lossless.webp | golang/image v0.48.0 | BSD-3-Clause | `bed5f6444ddaeee85d498cc6540d48938a7866d771fe0e9470161f9fec7f9a64` |
| `gopher-doc.8bpp.png` | https://raw.githubusercontent.com/golang/image/v0.48.0/testdata/gopher-doc.8bpp.png | golang/image v0.48.0 | BSD-3-Clause | `7692a9366ca414ee1fc12beb0feea1b7f56015d853be7f404a0896b8b49a537c` |
| `gopher-doc.skip-hgroup.lossless.webp` | https://raw.githubusercontent.com/golang/image/v0.48.0/testdata/gopher-doc.skip-hgroup.lossless.webp | golang/image v0.48.0 | BSD-3-Clause | `576c62996ae5570f5d1a287fe50ae8525ffdd8f5075d37589f10c0acefd2a72c` |
| `gopher-doc.with-alpha.lossless.webp` | https://raw.githubusercontent.com/golang/image/v0.48.0/testdata/gopher-doc.with-alpha.lossless.webp | golang/image v0.48.0 | BSD-3-Clause | `cfa91d75af8ba0cce20bd3ce63227cc5a874e3c19a32eef911c04f05ac983b70` |
| `gopher-doc.with-alpha.png` | https://raw.githubusercontent.com/golang/image/v0.48.0/testdata/gopher-doc.with-alpha.png | golang/image v0.48.0 | BSD-3-Clause | `da1e0876c0fe4bd012f5656b6f0da0b16f592c85c407878c9e19d379010eac39` |
| `tux.lossless.webp` | https://raw.githubusercontent.com/golang/image/v0.48.0/testdata/tux.lossless.webp | golang/image v0.48.0 | BSD-3-Clause | `ac071c15ecb7ea9f59cbd4b5185721b4ccfc4b2ee9b6afc3d7b9b44d1f2cea26` |
| `tux.png` | https://raw.githubusercontent.com/golang/image/v0.48.0/testdata/tux.png | golang/image v0.48.0 | BSD-3-Clause | `4358b1e6137fd60a49ad90d108b73c0116738552d78cf4fceb56a89f044c342f` |
| `video-001.lossy.webp` | https://raw.githubusercontent.com/golang/image/v0.48.0/testdata/video-001.lossy.webp | golang/image v0.48.0 | BSD-3-Clause | `895022c2b310c764eab2553c6683da1e38b81e32ab7a3faa2b927369f032d5a2` |
| `yellow_rose.lossless.webp` | https://raw.githubusercontent.com/golang/image/v0.48.0/testdata/yellow_rose.lossless.webp | golang/image v0.48.0 | BSD-3-Clause | `d7e1a26bb37f648f0ec33a2aab948ab9857f92b9793b069122f7e3ce4c59ecb5` |
| `yellow_rose.lossy-with-alpha.webp` | https://raw.githubusercontent.com/golang/image/v0.48.0/testdata/yellow_rose.lossy-with-alpha.webp | golang/image v0.48.0 | BSD-3-Clause | `b6ddb7ed2f0397f132a975e9c2cdbc3e01abb39096fb1a584cf8c30a0d612378` |
| `yellow_rose.lossy.webp` | https://raw.githubusercontent.com/golang/image/v0.48.0/testdata/yellow_rose.lossy.webp | golang/image v0.48.0 | BSD-3-Clause | `e3da6435eb07c68d7532fded39a7a4442602fc96e9d9327cbf6aed084530c44d` |
| `yellow_rose.png` | https://raw.githubusercontent.com/golang/image/v0.48.0/testdata/yellow_rose.png | golang/image v0.48.0 | BSD-3-Clause | `c0a5788aea933d7bab215074684b899b4a20438205d1231d840e62f6e2aef600` |

## The sources that were ported

The Go and libwebp source files that the decoder was ported from are not in the repository; their addresses and hashes are in `docs/decisions/0001-webp-decoder.md`.
