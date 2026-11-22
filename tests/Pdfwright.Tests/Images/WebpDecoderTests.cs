using Pdfwright.Images;

namespace Pdfwright.Tests.Images;

/// <summary>
/// The WebP decoder against its test pictures (<c>Images/Files/webp</c>). A lossless picture must equal the PNG it was made from, pixel for pixel and alpha for alpha. A lossy picture cannot equal
/// the PNG it was compressed from (the compression loses information), so it must be close to it: see <see cref="LossyTolerance"/>.
/// </summary>
public class WebpDecoderTests
{
    /// <summary>
    /// How close a lossy picture must be to the lossless PNG it was made from, over the opaque pixels of the PNG: a mean absolute error of at most <see cref="MaxMeanError"/> levels (out of 255) over all colour channels, and a peak signal to noise ratio of at least
    /// <see cref="MinPsnr"/> decibels. What the decoder measured: blue-purple-pink 3.94 levels and 32.7 dB, yellow_rose 2.52 and 36.7 dB, yellow_rose with alpha 3.57 and 33.9 dB; the limits keep a margin over that. A wrong colour conversion, a wrong
    /// upsampling or a loop filter that is wrong or missing costs several decibels. (This is a tolerance against the original, not against another decoder: the compression itself loses that much.)
    /// </summary>
    private const double MaxMeanError = 5.0;

    private const double MinPsnr = 30.0;

    public static TheoryData<string, string> LosslessPictures()
    {
        TheoryData<string, string> data = [];
        foreach ((string webp, string png) in WebpTestKit.Lossless)
        {
            data.Add(webp, png);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(LosslessPictures))]
    public void A_lossless_picture_equals_its_png_pixel_for_pixel_and_alpha_for_alpha(string webp, string png)
    {
        ImageSource decoded = ImageSource.FromBytes(WebpTestKit.Read(webp));
        ImageSource expected = ImageSource.FromBytes(WebpTestKit.Read(png));

        Assert.Equal(expected.Width, decoded.Width);
        Assert.Equal(expected.Height, decoded.Height);
        Assert.Equal(ImageColorSpace.Rgb, decoded.ColorSpace);
        (byte[] rgb, byte[] alpha) = WebpTestKit.Rgba(decoded);
        (byte[] expectedRgb, byte[] expectedAlpha) = WebpTestKit.Rgba(expected);
        Assert.Equal(expectedAlpha, alpha);
        Assert.Equal(expectedRgb, rgb);
    }

    [Fact]
    public void A_lossless_picture_with_transparency_has_a_mask_and_one_without_has_none()
    {
        Assert.NotNull(ImageSource.FromBytes(WebpTestKit.Read("tux.lossless.webp")).Mask);
        Assert.NotNull(ImageSource.FromBytes(WebpTestKit.Read("gopher-doc.with-alpha.lossless.webp")).Mask);
        Assert.NotNull(ImageSource.FromBytes(WebpTestKit.Read("yellow_rose.lossless.webp")).Mask);
        Assert.Null(ImageSource.FromBytes(WebpTestKit.Read("blue-purple-pink.lossless.webp")).Mask);
    }

    [Theory]
    [InlineData("blue-purple-pink.lossy.webp", "blue-purple-pink.png")]
    [InlineData("yellow_rose.lossy.webp", "yellow_rose.png")]
    [InlineData("yellow_rose.lossy-with-alpha.webp", "yellow_rose.png")]
    public void A_lossy_picture_is_close_to_the_png_it_was_compressed_from(string webp, string png)
    {
        ImageSource decoded = ImageSource.FromBytes(WebpTestKit.Read(webp));
        ImageSource expected = ImageSource.FromBytes(WebpTestKit.Read(png));
        Assert.Equal(expected.Width, decoded.Width);
        Assert.Equal(expected.Height, decoded.Height);

        (double mean, double psnr) = LossyTolerance(decoded, expected);
        Assert.True(mean <= MaxMeanError, $"mean absolute error {mean:F3} is over {MaxMeanError}");
        Assert.True(psnr >= MinPsnr, $"PSNR {psnr:F2} dB is under {MinPsnr}");
    }

    /// <summary>The mean absolute error and the PSNR of the colour of a lossy picture against the lossless one, over the pixels that are fully opaque in the lossless one (the colour of a transparent pixel is not meant to be kept).</summary>
    private static (double Mean, double Psnr) LossyTolerance(ImageSource decoded, ImageSource expected)
    {
        (byte[] rgb, _) = WebpTestKit.Rgba(decoded);
        (byte[] expectedRgb, byte[] expectedAlpha) = WebpTestKit.Rgba(expected);
        double sum = 0;
        double squares = 0;
        long count = 0;
        for (int i = 0; i < expectedAlpha.Length; i++)
        {
            if (expectedAlpha[i] != 255)
            {
                continue;
            }

            for (int c = 0; c < 3; c++)
            {
                int d = rgb[(3 * i) + c] - expectedRgb[(3 * i) + c];
                sum += Math.Abs(d);
                squares += d * d;
                count++;
            }
        }

        double mse = squares / count;
        return (sum / count, mse == 0 ? double.PositiveInfinity : 10 * Math.Log10(255.0 * 255.0 / mse));
    }

    [Fact]
    public void A_lossy_picture_with_an_alpha_plane_has_a_mask_that_varies()
    {
        ImageSource decoded = ImageSource.FromBytes(WebpTestKit.Read("yellow_rose.lossy-with-alpha.webp"));
        ReadOnlyMemory<byte> mask = Assert.NotNull(decoded.Mask);
        Assert.Equal(decoded.Width * decoded.Height, mask.Length);
        Assert.Contains((byte)0, mask.ToArray());
        Assert.Contains((byte)255, mask.ToArray());
    }

    [Fact]
    public void A_lossy_picture_without_an_alpha_plane_has_no_mask_and_the_size_its_header_gives()
    {
        foreach (string name in new[] { "blue-purple-pink.lossy.webp", "yellow_rose.lossy.webp", "video-001.lossy.webp" })
        {
            byte[] file = WebpTestKit.Read(name);
            (int width, int height) = WebpTestKit.Vp8Size(WebpTestKit.Chunk(file, "VP8 "));
            ImageSource decoded = ImageSource.FromBytes(file);
            Assert.Equal(width, decoded.Width);
            Assert.Equal(height, decoded.Height);
            Assert.Equal(3 * width * height, decoded.Data.Length);
            Assert.Null(decoded.Mask);
        }
    }

    [Fact]
    public void A_picture_is_found_by_its_riff_header_and_the_public_api_reads_it()
    {
        byte[] file = WebpTestKit.Read("tux.lossless.webp");
        Assert.True(WebpDecoder.HasSignature(file));
        Assert.False(WebpDecoder.HasSignature(WebpTestKit.Read("tux.png")));
        Assert.False(WebpDecoder.HasSignature("RIFF\0\0\0\0WAVE"u8));

        PdfImage image = PdfImage.FromBytes(file);
        ImageSource source = ImageSource.FromPng(WebpTestKit.Read("tux.png"));
        Assert.Equal(source.Width, image.PixelWidth);
        Assert.Equal(source.Height, image.PixelHeight);
        Assert.True(image.HasTransparency);
        Assert.False(PdfImage.FromBytes(WebpTestKit.Read("blue-purple-pink.lossless.webp")).HasTransparency);
    }

    [Fact]
    public void Chunks_that_are_metadata_or_unknown_are_skipped()
    {
        byte[] lossless = WebpTestKit.Chunk(WebpTestKit.Read("tux.lossless.webp"), "VP8L");
        (int width, int height) = ImageHeader(lossless);
        byte[] file = WebpTestKit.Riff(
            ("VP8X", WebpTestKit.Vp8x(0x10 | 0x20 | 0x08 | 0x04, width, height)),
            ("ICCP", [1, 2, 3, 4, 5]),
            ("VP8L", lossless),
            ("EXIF", [9, 9, 9]),
            ("XMP ", [8]),
            ("ZZZZ", []));
        ImageSource decoded = ImageSource.FromBytes(file);
        ImageSource plain = ImageSource.FromBytes(WebpTestKit.Read("tux.lossless.webp"));
        Assert.Equal(plain.Data.ToArray(), decoded.Data.ToArray());
        Assert.Equal(plain.Mask!.Value.ToArray(), decoded.Mask!.Value.ToArray());
    }

    private static (int Width, int Height) ImageHeader(byte[] vp8l) =>
        (1 + (vp8l[1] | ((vp8l[2] & 0x3f) << 8)), 1 + ((vp8l[2] >> 6) | (vp8l[3] << 2) | ((vp8l[4] & 0x0f) << 10)));

    [Fact]
    public void Trailing_bytes_after_the_riff_size_are_ignored()
    {
        byte[] file = WebpTestKit.Read("gopher-doc.8bpp.lossless.webp");
        ImageSource plain = ImageSource.FromBytes(file);
        ImageSource trailing = ImageSource.FromBytes([.. file, 1, 2, 3, 4, 5]);
        Assert.Equal(plain.Data.ToArray(), trailing.Data.ToArray());
    }

    // ---- The alpha plane of a lossy picture

    /// <summary>The plane <c>(x * 7 + y * 13 + (x * y) % 5) % 256</c>, which no one filter predicts exactly.</summary>
    private static byte[] Plane(int width, int height)
    {
        byte[] plane = new byte[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                plane[(y * width) + x] = (byte)(((x * 7) + (y * 13) + ((x * y) % 5)) % 256);
            }
        }

        return plane;
    }

    /// <summary>The residuals of a plane for one of the filters of the format (the inverse of what the decoder does).</summary>
    private static byte[] Filtered(byte[] plane, int width, int filter)
    {
        byte[] result = new byte[plane.Length];
        for (int i = 0; i < plane.Length; i++)
        {
            int x = i % width;
            int y = i / width;
            int left = x > 0 ? plane[i - 1] : 0;
            int above = y > 0 ? plane[i - width] : 0;
            int corner = x > 0 && y > 0 ? plane[i - width - 1] : 0;
            int prediction;
            if (i == 0)
            {
                prediction = 0;
            }
            else if (filter == 1)
            {
                prediction = x == 0 ? above : left;
            }
            else if (filter == 2)
            {
                prediction = y == 0 ? left : above;
            }
            else
            {
                prediction = y == 0 ? left : x == 0 ? above : Math.Clamp(left + above - corner, 0, 255);
            }

            result[i] = (byte)(plane[i] - prediction);
        }

        return result;
    }

    private static ImageSource LossyWithAlpha(byte[] alphaChunk)
    {
        byte[] vp8 = WebpTestKit.Chunk(WebpTestKit.Read("blue-purple-pink.lossy.webp"), "VP8 ");
        (int width, int height) = WebpTestKit.Vp8Size(vp8);
        return ImageSource.FromBytes(WebpTestKit.Riff(("VP8X", WebpTestKit.Vp8x(0x10, width, height)), ("ALPH", alphaChunk), ("VP8 ", vp8)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void An_uncompressed_alpha_plane_is_read_with_each_of_the_four_filters(int filter)
    {
        (int width, int height) = WebpTestKit.Vp8Size(WebpTestKit.Chunk(WebpTestKit.Read("blue-purple-pink.lossy.webp"), "VP8 "));
        byte[] plane = Plane(width, height);
        byte[] data = filter == 0 ? plane : Filtered(plane, width, filter);
        ImageSource decoded = LossyWithAlpha([.. new byte[] { (byte)(filter << 2) }, .. data]);
        Assert.Equal(plane, Assert.NotNull(decoded.Mask).ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void A_lossless_coded_alpha_plane_is_read(int filter)
    {
        (int width, int height) = WebpTestKit.Vp8Size(WebpTestKit.Chunk(WebpTestKit.Read("blue-purple-pink.lossy.webp"), "VP8 "));
        ImageSource decoded = LossyWithAlpha([.. new byte[] { (byte)((filter << 2) | 1) }, .. WebpTestKit.ConstantAlphaStream(200)]);

        // A constant plane after the filter's residuals are added: 200 at the start, and for the gradient filter the prediction of the rest is 200 + 200 - 200 clamped, so the plane is the sum.
        byte[] mask = Assert.NotNull(decoded.Mask).ToArray();
        Assert.Equal(width * height, mask.Length);
        if (filter == 0)
        {
            Assert.All(mask, a => Assert.Equal(200, a));
        }
        else
        {
            Assert.Equal(200, mask[0]);
        }
    }

    [Fact]
    public void An_alpha_plane_that_is_cut_short_or_uses_an_unknown_method_is_refused()
    {
        (int width, int height) = WebpTestKit.Vp8Size(WebpTestKit.Chunk(WebpTestKit.Read("blue-purple-pink.lossy.webp"), "VP8 "));
        byte[] vp8 = WebpTestKit.Chunk(WebpTestKit.Read("blue-purple-pink.lossy.webp"), "VP8 ");
        byte[] Make(byte[] alpha) => WebpTestKit.Riff(("VP8X", WebpTestKit.Vp8x(0x10, width, height)), ("ALPH", alpha), ("VP8 ", vp8));
        Assert.Throws<ImageFormatException>(() => ImageSource.FromBytes(Make([0, 1, 2, 3])));
        Assert.Throws<ImageFormatException>(() => ImageSource.FromBytes(Make([2, 1, 2, 3])));
        Assert.Throws<ImageFormatException>(() => ImageSource.FromBytes(Make([])));
    }

    [Fact]
    public void A_vp8x_picture_with_the_alpha_flag_but_no_alpha_chunk_is_opaque()
    {
        byte[] vp8 = WebpTestKit.Chunk(WebpTestKit.Read("blue-purple-pink.lossy.webp"), "VP8 ");
        (int width, int height) = WebpTestKit.Vp8Size(vp8);
        ImageSource decoded = ImageSource.FromBytes(WebpTestKit.Riff(("VP8X", WebpTestKit.Vp8x(0x10, width, height)), ("VP8 ", vp8)));
        Assert.Null(decoded.Mask);
    }

    // ---- Animation

    [Fact]
    public void An_animated_picture_is_refused_with_a_message_that_says_so()
    {
        // The animation flag of VP8X.
        byte[] flagged = WebpTestKit.Riff(("VP8X", WebpTestKit.Vp8x(0x02, 10, 10)), ("ANIM", [0, 0, 0, 0, 0, 0]));
        ImageFormatException flaggedError = Assert.Throws<ImageFormatException>(() => ImageSource.FromBytes(flagged));
        Assert.Contains("animation", flaggedError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("still pictures only", flaggedError.Message, StringComparison.Ordinal);

        // The chunks of an animation, without the flag.
        byte[] frame = WebpTestKit.Chunk(WebpTestKit.Read("tux.lossless.webp"), "VP8L");
        foreach (string id in new[] { "ANIM", "ANMF" })
        {
            byte[] unflagged = WebpTestKit.Riff(("VP8X", WebpTestKit.Vp8x(0, 10, 10)), (id, frame));
            Assert.Contains("animation", Assert.Throws<ImageFormatException>(() => ImageSource.FromBytes(unflagged)).Message, StringComparison.OrdinalIgnoreCase);
        }

        // Through the public API it is an ArgumentException that names the reason.
        ArgumentException error = Assert.Throws<ArgumentException>(() => PdfImage.FromBytes(flagged));
        Assert.Contains("animation", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---- The container

    [Fact]
    public void A_file_with_no_picture_or_with_two_headers_or_a_bad_header_is_refused()
    {
        Assert.Throws<ImageFormatException>(() => ImageSource.FromBytes(WebpTestKit.Riff()));
        Assert.Throws<ImageFormatException>(() => ImageSource.FromBytes(WebpTestKit.Riff(("EXIF", [1, 2]))));
        Assert.Throws<ImageFormatException>(() => ImageSource.FromBytes(WebpTestKit.Riff(("VP8X", WebpTestKit.Vp8x(0, 4, 4)), ("VP8X", WebpTestKit.Vp8x(0, 4, 4)))));
        Assert.Throws<ImageFormatException>(() => ImageSource.FromBytes(WebpTestKit.Riff(("VP8X", [0, 0, 0, 0, 0, 0, 0, 0, 0])))); // 9 bytes, not 10.
        Assert.Throws<ImageFormatException>(() => ImageSource.FromBytes(WebpTestKit.Riff(("ALPH", [0, 1, 2]), ("VP8 ", [1, 2, 3]))));

        // The RIFF size says more than the data holds, or less than a chunk.
        byte[] file = WebpTestKit.Read("gopher-doc.8bpp.lossless.webp");
        Assert.Throws<ImageFormatException>(() => ImageSource.FromBytes(file[..^10]));
        byte[] tooShort = (byte[])file.Clone();
        tooShort[4] = 4;
        tooShort[5] = tooShort[6] = tooShort[7] = 0;
        Assert.Throws<ImageFormatException>(() => ImageSource.FromBytes(tooShort));
    }
}
