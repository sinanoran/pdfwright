# 0001. Reading WebP: a managed decoder ported from Go's `golang.org/x/image`

Status: accepted, 22 November 2026. Applies to: `src/Pdfwright/Images/Webp*.cs`, `Vp8*.cs`, `Vp8l*.cs`.

## Context

`PdfImage.FromBytes` read JPEG and PNG only. WebP is the format a large part of the web serves pictures in, so a report writer that is given a logo or a photograph from a web source meets it. A decoder had to meet these conditions:

- **MIT-compatible and no native code of its own.** Pdfwright ships managed code and one native library (HarfBuzz) that it already carries for Linux glibc and musl and every architecture. A second native dependency for pictures was to be avoided.
- **No new NuGet dependency**, and one that does not narrow where Pdfwright runs (net8.0 to net11.0, Windows, Linux glibc and musl, macOS).
- **Hostile input is the normal case.** The bytes of a picture come from outside. The WebP decoders of the wild have a history (CVE-2023-4863, a heap overflow in libwebp's Huffman table building that was exploited), so the decoder has to be small enough to read in full, and every size has to be checked before it is used.
- **The colours must be libwebp's**, within one level, so that a logo looks as it does in a browser.

## Decision

Port the WebP decoder of Go's `golang.org/x/image`, tag **v0.48.0**, to C#, and add the pieces that make the colours libwebp's from libwebp **v1.6.0**:

- from Go: the RIFF container with the `VP8 `, `VP8L`, `VP8X` and `ALPH` chunks (`webp/decode.go`), the lossy VP8 decoder (`vp8/*.go`: arithmetic decoder, headers, quantisation, tokens, predictors, inverse DCT and WHT, the simple and normal loop filters) and the lossless VP8L decoder (`vp8l/*.go`: Huffman groups, colour cache, LZ77, the predictor, cross-colour, subtract-green and colour-indexing transforms). About 3,500 lines of Go, no SIMD, no unsafe code, no threads;
- from libwebp: the scalar YUV to RGB formulas (`src/dsp/yuv.h`) and the "fancy" bilinear upsampling of the chroma planes (`src/dsp/upsampling.c`), which libwebp (`dwebp`, browsers) applies by default. Go's decoder returns YCbCr planes and leaves both to the caller;
- added here: the pixel limit (`PdfImageOptions.MaxPixels`, 40 million by default, and 65,535 pixels on a side) checked after every header and before any allocation; a cap on the number of Huffman groups (2,600, as libwebp's encoder) and on the memory of all the Huffman tables of an image (64 MB); the refusal of an incomplete Huffman code, as libwebp has it; libwebp's leniency about `ALPH` chunks and about the predictor modes 14 and 15; an `ImageFormatException` for every failure, which `PdfImage.FromBytes` surfaces as an `ArgumentException`.

The output has the form `PngDecoder` produces: 8-bit RGB samples and, when there is transparency, a separate alpha plane, written as an image XObject with a soft mask. An animated WebP (the animation flag of `VP8X`, or an `ANIM` or `ANMF` chunk) is **refused** with a message that says so. The first frame of an animation is not shown; supporting it would be about a day of work (the `ANMF` chunk, the frame offsets and the compositing of the first frame) and has not been asked for. The `ICCP`, `EXIF` and `XMP ` chunks are skipped, so the colour profile is ignored, as it is for PNG and JPEG.

Go v0.48.0 contains the fixes of three advisories of 2026 that matter for a decoder of untrusted input, and each has a regression test here with a crafted input:

| Advisory | What it was | Test |
|---|---|---|
| GO-2026-4961 | The product of the canvas width and height of `VP8X` did not fit a 32-bit size. | A canvas of 65,536 by 32,768 and one of 16,777,216 by 16,777,216 are refused by the limits before anything is allocated. |
| GO-2026-5061 | A `VP8X` canvas smaller than the `VP8` or `VP8L` picture that follows it made the decoder allocate for the picture and write it to a smaller canvas. | A `VP8X` canvas of 1 by 1 in front of a real lossy and a real lossless picture is refused. |
| GO-2026-6222 | One image could name up to 65,535 Huffman groups and make the decoder allocate more than 3 GB for them. | A `VP8L` stream that names group 65,535 is refused without the memory for it; group 2,600 is refused and group 2,599 is read, keeping only the one group a tile uses. |

## Rejected

| Option | Why not |
|---|---|
| **SixLabors.ImageSharp** | The Six Labors Split License is not MIT: it is free only under conditions (open source use, small companies), and would put those conditions on everyone who uses Pdfwright. |
| **Magick.NET** | A native library of 97 MB for the sake of one format. |
| **SkiaSharp** | About 60 MB of natives for Linux, and musl support unconfirmed; Pdfwright deliberately does not use Skia (see `NOTICE`). |
| **NetVips** | LGPL, and a native libvips. |
| **Imazen.WebP** | No musl build, and it is built on `System.Drawing`. |
| **WebP_Net, Webp-Wrapper** | Windows only. |
| **Darnelix.Webp** | Proprietary. |
| **Sdcb.SimdCodecs.Webp** | net10 only, new, SIMD-heavy and not fuzzed; kept in mind as a second oracle for the colours, not as a dependency. |
| **ImgSmush, PictureSharp** | Immature. |
| **A direct port of libwebp** | 8,000 to 10,000 lines of C with pointer arithmetic, 15 to 25 days of work, and the CVE-2023-4863 history in exactly the code (the Huffman tables) that would have been ported. |
| **Rust `image-webp`** | Larger and harder to port than the Go code; kept as the reference for the animation chunks if they are ever wanted. |

The effort estimate for the chosen way was 8 to 11 days: the container 1, VP8L 2, VP8 3 to 4, the colour conversion half a day, the limits and the fuzzing 1.5, tests and documentation 1 to 2.

## Licences and notices

- **golang.org/x/image** is BSD-3-Clause, with an additional patent grant from Google (the `PATENTS` file). **libwebp** is BSD-3-Clause with its own patent grant (`PATENTS`). Both licences require the copyright notice and the list of conditions to be kept with the source and with binaries.
- Each ported C# file carries the notice of its source in its header comment, and the licence and patent texts of both projects are in `NOTICE`, which ships in the package.
- The name of Google is not used to endorse Pdfwright, and nothing here says that Google has approved it.
- The Go and libwebp files that were read to be ported are **not** in the repository. They were downloaded once, read, and not run; their addresses and hashes are below, so that anyone can fetch the same bytes and compare.
- Some test pictures (below) are committed as test data; they come from the `testdata` folder of golang.org/x/image v0.48.0 and have the licence of that repository.

## Files downloaded

The maintainer approved exactly these files. All are data to read and port; none was run.

| File | Source URL | Version | Licence | SHA-256 |
|---|---|---|---|---|
| `LICENSE` | https://raw.githubusercontent.com/golang/image/v0.48.0/LICENSE | golang/image v0.48.0 | BSD-3-Clause | `911f8f5782931320f5b8d1160a76365b83aea6447ee6c04fa6d5591467db9dad` |
| `PATENTS` | https://raw.githubusercontent.com/golang/image/v0.48.0/PATENTS | golang/image v0.48.0 | Google patent grant | `96f408bfae65bf137fc2525d3ecb030271c50c1e90799f87abf8846d8dd505cc` |
| `webp/decode.go` | https://raw.githubusercontent.com/golang/image/v0.48.0/webp/decode.go | golang/image v0.48.0 | BSD-3-Clause | `cd82c67e40ec49165ba5f7dec5b2672d68816a9473711098305b59008df9d433` |
| `webp/doc.go` | https://raw.githubusercontent.com/golang/image/v0.48.0/webp/doc.go | golang/image v0.48.0 | BSD-3-Clause | `d28a6c1eea36b0806ddfdc0dfa98ac0a32afd13951dfdedf65a2eb0c93bcc2a1` |
| `webp/decode_test.go` | https://raw.githubusercontent.com/golang/image/v0.48.0/webp/decode_test.go | golang/image v0.48.0 | BSD-3-Clause | `a842cf15f60e2fb71be1706f46a5da7dcf70c9ce65da7b1b5b81ed5baef5c15b` |
| `vp8/decode.go` | https://raw.githubusercontent.com/golang/image/v0.48.0/vp8/decode.go | golang/image v0.48.0 | BSD-3-Clause | `9d738122d496eda99e39e358e4c41d13fcbe7d17a5f0b68b563f2dd1d544629d` |
| `vp8/filter.go` | https://raw.githubusercontent.com/golang/image/v0.48.0/vp8/filter.go | golang/image v0.48.0 | BSD-3-Clause | `7b0196140613dd37ab2eb3aece987b3839289731741ac9fca52f8000c818e653` |
| `vp8/idct.go` | https://raw.githubusercontent.com/golang/image/v0.48.0/vp8/idct.go | golang/image v0.48.0 | BSD-3-Clause | `b6b27996e730f34d7cc614392cd511eef98b02c44f42659d04294c7b1d9b0594` |
| `vp8/partition.go` | https://raw.githubusercontent.com/golang/image/v0.48.0/vp8/partition.go | golang/image v0.48.0 | BSD-3-Clause | `6d9d2cfd7ea2207297ff76509733c1135dba3c1dfdc2eed2afcbcdc977a632d2` |
| `vp8/pred.go` | https://raw.githubusercontent.com/golang/image/v0.48.0/vp8/pred.go | golang/image v0.48.0 | BSD-3-Clause | `791ceebb5f99b762441d56ab00c3e6fa3475416b93be2c48890b740830631dcf` |
| `vp8/predfunc.go` | https://raw.githubusercontent.com/golang/image/v0.48.0/vp8/predfunc.go | golang/image v0.48.0 | BSD-3-Clause | `0df4ca0eaa5cdbe562e4f11572279e5717d0ce18aa2f3dfb14adac329062cb67` |
| `vp8/quant.go` | https://raw.githubusercontent.com/golang/image/v0.48.0/vp8/quant.go | golang/image v0.48.0 | BSD-3-Clause | `b806d2f3a849c648fe394d2812d2386e30148c8545e939dda2f2c47c3628fb14` |
| `vp8/reconstruct.go` | https://raw.githubusercontent.com/golang/image/v0.48.0/vp8/reconstruct.go | golang/image v0.48.0 | BSD-3-Clause | `72a34c52982880ce3103101e66871acc542ccbfe3fcf5c45e56bf887f8621950` |
| `vp8/token.go` | https://raw.githubusercontent.com/golang/image/v0.48.0/vp8/token.go | golang/image v0.48.0 | BSD-3-Clause | `644a520b7b3102757947ef346cd95da5d6fbed1993513b15eb27ad16008a987c` |
| `vp8l/decode.go` | https://raw.githubusercontent.com/golang/image/v0.48.0/vp8l/decode.go | golang/image v0.48.0 | BSD-3-Clause | `0667281c231f8c439ea39c8f850e6cb58e39e67a4a33e0ecd60200fc863f44d2` |
| `vp8l/huffman.go` | https://raw.githubusercontent.com/golang/image/v0.48.0/vp8l/huffman.go | golang/image v0.48.0 | BSD-3-Clause | `2cbfd295ca97102bb2f6efea9eb125ca36ad428d299160ea08438a02855dcd8a` |
| `vp8l/transform.go` | https://raw.githubusercontent.com/golang/image/v0.48.0/vp8l/transform.go | golang/image v0.48.0 | BSD-3-Clause | `ffb990b337e40a07f921bfd213b55b205b3973410fdbd2a38eb2581527dafea1` |
| `COPYING` | https://raw.githubusercontent.com/webmproject/libwebp/v1.6.0/COPYING | libwebp v1.6.0 | BSD-3-Clause | `5aec868f669e384a22372a4e8a1a6cd7d44c64cd451f960ca69cc170d1e13acf` |
| `PATENTS` | https://raw.githubusercontent.com/webmproject/libwebp/v1.6.0/PATENTS | libwebp v1.6.0 | Google patent grant | `cc3273e0694ea5896145e0677699b53471b03ea43021ddc50e7923fbb9f5023c` |
| `src/dsp/yuv.h` | https://raw.githubusercontent.com/webmproject/libwebp/v1.6.0/src/dsp/yuv.h | libwebp v1.6.0 | BSD-3-Clause | `5805c01f84a677e50bf0d16cd903b78d25c2a0c46fe17c67ce0e48f11f3d512b` |
| `src/dsp/upsampling.c` | https://raw.githubusercontent.com/webmproject/libwebp/v1.6.0/src/dsp/upsampling.c | libwebp v1.6.0 | BSD-3-Clause | `21b05126aff3fbe902655a7081e3a538f8d1ca6cc9e0907c607e1c4c50ba0a9f` |

The test pictures that are committed, with the same source, version, licence and hash, are listed in `tests/Pdfwright.Tests/Images/Files/webp/README.md`.

## Consequences

- WebP (lossy, lossless, with alpha) is read; animated WebP is refused. See `docs/known-limitations.md`.
- Lossy colours are libwebp's formulas and upsampling, ported from the source and not compared with a running libwebp (no libwebp was run); the claim "within one level of libwebp" rests on that port, and a reference run of `dwebp` can confirm it.
- Go's decoder reads the whole chunk into memory and decodes whole planes, so a lossless picture of the limit (40 million pixels) needs about 320 MB while it is decoded (the 4-byte pixels and the 3 + 1 byte samples that replace them), a lossy one about 220 MB.
- Security fixes of golang.org/x/image after v0.48.0 are not picked up by themselves. Anyone who changes this code should compare it with the current tag of `vp8`, `vp8l` and `webp` first.
