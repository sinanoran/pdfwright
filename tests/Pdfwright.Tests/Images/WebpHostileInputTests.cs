using System.Diagnostics;
using Pdfwright.Images;

namespace Pdfwright.Tests.Images;

/// <summary>
/// WebP data that is cut short, damaged, too large or crafted to make a decoder allocate or loop: a clear <see cref="ImageFormatException"/> (an <see cref="ArgumentException"/> through <see cref="PdfImage"/>) every time, with the
/// size checked before anything is allocated for it, and never an exception of the runtime. The three advisories of 2026 about the WebP decoder of golang.org/x/image, which this decoder is a port of, each have a test here.
/// </summary>
public class WebpHostileInputTests
{
    private static ImageFormatException Refused(byte[] file, long maxPixels = ImageSource.DefaultMaxPixels)
    {
        ImageFormatException error = Assert.Throws<ImageFormatException>(() => ImageSource.FromBytes(file, maxPixels));
        AssertRefusedByTheDecoder(error);
        return error;
    }

    /// <summary>
    /// The decoder turns an exception of the runtime into an <see cref="ImageFormatException"/> as a last line of defence for users, and keeps the original as the inner exception. A refusal that has one was not made by a check of the decoder:
    /// it is a bug that the net hid, and a test must see it.
    /// </summary>
    private static void AssertRefusedByTheDecoder(ImageFormatException error)
    {
        Assert.False(
            error.InnerException is IndexOutOfRangeException or ArgumentException or OverflowException or InvalidOperationException or NullReferenceException or DivideByZeroException,
            "The refusal was converted from an exception of the runtime: " + error.InnerException);
    }

    private static long Allocated(Action action)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        action();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    // ---- GO-2026-4961 (CVE-2026-33813, https://vuln.go.dev/ID/GO-2026-4961.json): a panic on a large WebP size on 32-bit platforms; the size of the canvas did not fit 32 bits

    [Theory]
    [InlineData(65536, 32768)] // 2^31 pixels, the first that a 32-bit signed size cannot hold.
    [InlineData(65535, 65535)] // Just under 2^32.
    [InlineData(1 << 24, 1 << 24)] // The largest the format can say: 2^48 pixels, which overflows a 32-bit product and a 32-bit size.
    [InlineData(60000, 60000)]
    [InlineData(10001, 10000)]
    public void A_canvas_over_the_limits_is_refused_before_anything_is_allocated(int width, int height)
    {
        byte[] file = WebpTestKit.Riff(("VP8X", WebpTestKit.Vp8x(0x10, width, height)));
        Stopwatch watch = Stopwatch.StartNew();
        long allocated = Allocated(() =>
        {
            ImageFormatException error = Refused(file);
            Assert.Contains("pixels", error.Message, StringComparison.Ordinal);
        });
        Assert.True(allocated < 1_000_000, $"{allocated} bytes were allocated for a header");
        Assert.True(watch.ElapsedMilliseconds < 2000);

        // Through the public API: an ArgumentException that names the size or the limit.
        ArgumentException exception = Assert.Throws<ArgumentException>(() => PdfImage.FromBytes(file));
        Assert.Equal("bytes", exception.ParamName);
    }

    [Fact]
    public void A_canvas_within_the_limit_is_let_through_the_header_and_one_over_the_option_is_not()
    {
        byte[] file = WebpTestKit.Riff(("VP8X", WebpTestKit.Vp8x(0, 10000, 10000)));
        Assert.Contains("no image data", Refused(file, 100_000_000).Message, StringComparison.Ordinal);
        ImageFormatException over = Refused(file, 99_999_999);
        Assert.True(over.LimitExceeded);
        Assert.Contains("PdfImageOptions.MaxPixels", over.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_size_in_the_header_of_a_picture_is_checked_before_anything_is_allocated_for_it()
    {
        // A lossy picture whose frame header says 16,383 by 16,383.
        byte[] lossy = WebpTestKit.Read("blue-purple-pink.lossy.webp");
        lossy[20 + 6] = 0xff;
        lossy[20 + 7] = 0x3f;
        lossy[20 + 8] = 0xff;
        lossy[20 + 9] = 0x3f;
        long allocated = Allocated(() => Assert.True(Refused(lossy).LimitExceeded));
        Assert.True(allocated < 1_000_000, $"{allocated} bytes were allocated for a lossy header");

        // A lossless one that says 16,384 by 16,384.
        byte[] lossless = WebpTestKit.Read("gopher-doc.8bpp.lossless.webp");
        lossless[20 + 1] = 0xff;
        lossless[20 + 2] = 0xff;
        lossless[20 + 3] = 0xff;
        lossless[20 + 4] = (byte)((lossless[20 + 4] & 0xf0) | 0x0f);
        allocated = Allocated(() => Assert.True(Refused(lossless).LimitExceeded));
        Assert.True(allocated < 1_000_000, $"{allocated} bytes were allocated for a lossless header");
    }

    // ---- GO-2026-5061 (CVE-2026-46601, https://vuln.go.dev/ID/GO-2026-5061.json): a panic when the VP8 alpha or the canvas size did not match; a canvas smaller than the picture

    [Fact]
    public void A_picture_that_is_not_the_size_of_its_canvas_is_refused_whether_it_is_lossy_or_lossless()
    {
        byte[] lossy = WebpTestKit.Chunk(WebpTestKit.Read("blue-purple-pink.lossy.webp"), "VP8 ");
        byte[] lossless = WebpTestKit.Chunk(WebpTestKit.Read("blue-purple-pink.lossless.webp"), "VP8L");
        foreach ((string id, byte[] picture) in new[] { ("VP8 ", lossy), ("VP8L", lossless) })
        {
            byte[] file = WebpTestKit.Riff(("VP8X", WebpTestKit.Vp8x(0, 1, 1)), (id, picture));
            ImageFormatException error = Refused(file);
            Assert.Contains("not the size", error.Message, StringComparison.Ordinal);
        }

        // With the right canvas the same two pictures are fine, and the canvas may carry the flags of metadata.
        (int width, int height) = WebpTestKit.Vp8Size(lossy);
        Assert.Equal(width, ImageSource.FromBytes(WebpTestKit.Riff(("VP8X", WebpTestKit.Vp8x(0x20, width, height)), ("VP8 ", lossy))).Width);

        // A canvas that is larger is refused as well (the picture does not fill it).
        Refused(WebpTestKit.Riff(("VP8X", WebpTestKit.Vp8x(0, width + 1, height)), ("VP8 ", lossy)));
        Refused(WebpTestKit.Riff(("VP8X", WebpTestKit.Vp8x(0, width, height + 1)), ("VP8 ", lossy)));
    }

    [Fact]
    public void An_alpha_plane_is_not_decoded_when_the_picture_is_not_the_size_of_the_canvas()
    {
        byte[] vp8 = WebpTestKit.Chunk(WebpTestKit.Read("blue-purple-pink.lossy.webp"), "VP8 ");
        byte[] file = WebpTestKit.Riff(("VP8X", WebpTestKit.Vp8x(0x10, 5000, 5000)), ("ALPH", [1, 0, 0, 0, 0]), ("VP8 ", vp8));
        long allocated = Allocated(() => Refused(file));
        Assert.True(allocated < 1_000_000, $"{allocated} bytes were allocated");
    }

    // ---- GO-2026-6222 (CVE-2026-46603, https://vuln.go.dev/ID/GO-2026-6222.json): excessive allocation for many unused Huffman groups; Huffman groups that no tile needs, and a size nothing allows

    [Fact]
    public void A_picture_that_names_group_65535_is_refused_without_the_memory_for_65535_groups()
    {
        byte[] file = WebpTestKit.Vp8lNamingGroup(4, 4, 255, 255);
        long allocated = Allocated(() => Assert.Contains("too many Huffman trees", Refused(file).Message, StringComparison.Ordinal));
        Assert.True(allocated < 5_000_000, $"{allocated} bytes were allocated");
    }

    [Fact]
    public void The_largest_group_number_that_is_let_through_is_2599_and_only_the_group_a_tile_uses_is_kept()
    {
        // Group 2600 is one too many.
        Assert.Contains("too many Huffman trees", Refused(WebpTestKit.Vp8lNamingGroup(4, 4, 10, 40)).Message, StringComparison.Ordinal);

        // Group 2599 is let through: 2,600 groups are in the data, 2,599 are read and dropped, and the picture is one colour.
        byte[] file = WebpTestKit.Vp8lNamingGroup(4, 4, 10, 39);
        long allocated = Allocated(() =>
        {
            ImageSource decoded = ImageSource.FromBytes(file);
            Assert.Equal(4, decoded.Width);
            Assert.Equal(4, decoded.Height);
            Assert.Null(decoded.Mask);
            byte[] expected = new byte[4 * 4 * 3];
            for (int i = 0; i < 16; i++)
            {
                expected[3 * i] = 20;
                expected[(3 * i) + 1] = 10;
                expected[(3 * i) + 2] = 30;
            }

            Assert.Equal(expected, decoded.Data.ToArray());
        });
        Assert.True(allocated < 5_000_000, $"{allocated} bytes were allocated for 2,600 groups of which one is kept");
    }

    // ---- The Huffman trees

    [Fact]
    public void A_huffman_code_that_is_incomplete_or_over_subscribed_is_refused()
    {
        Vp8lHuffmanTree tree = new();
        Assert.Throws<ImageFormatException>(() => tree.Build([2, 2]));
        Assert.Throws<ImageFormatException>(() => new Vp8lHuffmanTree().Build([1, 1, 1]));
        Assert.Throws<ImageFormatException>(() => new Vp8lHuffmanTree().Build([0, 0, 0]));
        Assert.Throws<ImageFormatException>(() => new Vp8lHuffmanTree().Build([16, 1]));
        Assert.Throws<ImageFormatException>(() => new Vp8lHuffmanTree().Build([3, 3, 3, 3, 3, 3, 3]));
    }

    [Fact]
    public void A_complete_code_reads_its_symbols_and_a_code_of_one_symbol_needs_no_bits()
    {
        // Lengths 1, 2, 3, 3: codes 0, 10, 110 and 111, read from the first bit; VP8L packs the bits starting from the least significant end.
        Vp8lHuffmanTree tree = new();
        tree.Build([1, 2, 3, 3]);
        WebpTestKit.BitWriter writer = new();
        foreach ((uint code, int length) in new (uint, int)[] { (0b0, 1), (0b10, 2), (0b110, 3), (0b111, 3), (0b0, 1) })
        {
            for (int i = length - 1; i >= 0; i--)
            {
                writer.Write((code >> i) & 1, 1);
            }
        }

        byte[] data = writer.ToArray();
        Vp8lBits bits = new(data, 0, data.Length);
        Assert.Equal(new uint[] { 0, 1, 2, 3, 0 }, new[] { tree.Next(bits), tree.Next(bits), tree.Next(bits), tree.Next(bits), tree.Next(bits) });

        Vp8lHuffmanTree single = new();
        single.Build([0, 0, 5]);
        Vp8lBits none = new([], 0, 0);
        Assert.Equal(2u, single.Next(none));
    }

    [Fact]
    public void A_code_of_15_bits_is_read_through_the_tree_beyond_the_look_up_table()
    {
        // 15 codes of lengths 1 to 14, then two of 15: a complete code.
        uint[] lengths = new uint[16];
        for (int i = 0; i < 14; i++)
        {
            lengths[i] = (uint)(i + 1);
        }

        lengths[14] = 15;
        lengths[15] = 15;
        Vp8lHuffmanTree tree = new();
        tree.Build(lengths);

        // The code of symbol 13 (length 14) is thirteen 1s and a 0.
        WebpTestKit.BitWriter writer = new();
        for (int i = 0; i < 13; i++)
        {
            writer.Write(1, 1);
        }

        writer.Write(0, 1);
        byte[] data = writer.ToArray();
        Assert.Equal(13u, tree.Next(new Vp8lBits(data, 0, data.Length)));
    }

    // ---- Damage

    [Fact]
    public void A_picture_cut_at_any_length_is_refused_with_a_typed_exception()
    {
        foreach (string name in new[] { "gopher-doc.1bpp.lossless.webp", "gopher-doc.2bpp.lossless.webp", "video-001.lossy.webp" })
        {
            byte[] file = WebpTestKit.Read(name);
            for (int length = 0; length < file.Length - 1; length += length < 120 ? 1 : 7)
            {
                Refused(file[..length]);
            }
        }
    }

    [Fact]
    public void Random_bytes_are_refused_whatever_they_start_with()
    {
        Random random = new(7);
        foreach (byte[] start in new byte[][] { [], "RIFF"u8.ToArray(), "RIFF\x10\0\0\0WEBPVP8 "u8.ToArray(), "RIFF\x10\0\0\0WEBPVP8L"u8.ToArray(), "RIFF\x10\0\0\0WEBPVP8X"u8.ToArray() })
        {
            for (int i = 0; i < 200; i++)
            {
                byte[] garbage = new byte[start.Length + random.Next(0, 300)];
                random.NextBytes(garbage);
                start.CopyTo(garbage, 0);
                try
                {
                    ImageSource.FromBytes(garbage);
                }
                catch (ImageFormatException error)
                {
                    AssertRefusedByTheDecoder(error);
                }
            }
        }
    }

    [Fact]
    public void A_lossy_frame_that_is_not_a_key_frame_or_has_no_start_code_is_refused()
    {
        byte[] file = WebpTestKit.Read("blue-purple-pink.lossy.webp");
        byte[] inter = (byte[])file.Clone();
        inter[20] |= 1; // Not a key frame.
        Assert.Contains("key frame", Refused(inter).Message, StringComparison.Ordinal);
        byte[] noCode = (byte[])file.Clone();
        noCode[20 + 3] ^= 0xff;
        Assert.Contains("start code", Refused(noCode).Message, StringComparison.Ordinal);
    }

    // ---- The mutation test: what a mutated picture gives is a picture or an ArgumentException from the public API, and nothing else

    [Fact]
    public void Mutating_truncating_or_extending_a_picture_never_gives_anything_but_a_picture_or_an_ArgumentException()
    {
        Random random = new(20261122);

        // A limit of 2 million pixels keeps a mutated size from costing a hundred megabytes in every round; the pictures are far smaller.
        PdfImageOptions options = new() { MaxPixels = 2_000_000 };
        int pictures = 0;
        int refused = 0;
        Stopwatch watch = Stopwatch.StartNew();
        foreach (string name in WebpTestKit.Everything)
        {
            byte[] original = WebpTestKit.Read(name);
            int mutations = original.Length <= 5000 ? 500 : 100;

            // Byte by byte: each of the first 48 bytes (the headers), then random places, changed to a random value, or to 0, or to 255, or by one bit.
            for (int i = 0; i < mutations; i++)
            {
                byte[] file = (byte[])original.Clone();
                int changes = 1 + random.Next(3);
                for (int k = 0; k < changes; k++)
                {
                    int at = i < 48 && k == 0 ? i : random.Next(file.Length);
                    file[at] = random.Next(4) switch
                    {
                        0 => (byte)random.Next(256),
                        1 => 0,
                        2 => 255,
                        _ => (byte)(file[at] ^ (1 << random.Next(8))),
                    };
                }

                Try(file);
            }

            // Truncated: every length of the first 40 bytes, and random ones after.
            for (int i = 0; i < 120; i++)
            {
                Try(original[..(i < 40 ? i : random.Next(original.Length))]);
            }

            // Extended: random bytes, zeros or a copy of the picture after the end, and the RIFF size made to match (so that the extra bytes are read as chunks).
            for (int i = 0; i < 30; i++)
            {
                byte[] extra = new byte[1 + random.Next(200)];
                if (i % 3 == 0)
                {
                    random.NextBytes(extra);
                }
                else if (i % 3 == 2)
                {
                    extra = original[..Math.Min(extra.Length, original.Length)];
                }

                byte[] file = [.. original, .. extra];
                if (i % 2 == 0)
                {
                    uint size = (uint)(file.Length - 8);
                    file[4] = (byte)size;
                    file[5] = (byte)(size >> 8);
                    file[6] = (byte)(size >> 16);
                    file[7] = (byte)(size >> 24);
                }

                Try(file);
            }
        }

        // Some mutations leave a picture (a change in the pixel data), and many are refused: the test would prove nothing if all went one way.
        Assert.True(pictures > 100, $"only {pictures} pictures came through");
        Assert.True(refused > 100, $"only {refused} were refused");
        Assert.True(watch.Elapsed < TimeSpan.FromMinutes(2), $"took {watch.Elapsed}");

        void Try(byte[] file)
        {
            // Through the internal reader, so that a refusal the safety net of the decoder converted from an exception of the runtime (which the public API shows the same way) is seen as the bug it is.
            try
            {
                ImageSource image = ImageSource.FromBytes(file, options.MaxPixels);
                Assert.True(image.Width >= 1 && image.Height >= 1);
                pictures++;
            }
            catch (ImageFormatException error)
            {
                AssertRefusedByTheDecoder(error);
                Assert.Throws<ArgumentException>(() => PdfImage.FromBytes(file, options));
                refused++;
            }
        }
    }

    // ---- The lossy decoder checks its headers and its data before it allocates and while it decodes

    [Fact]
    public void A_lossy_header_that_claims_a_huge_picture_is_refused_before_the_planes_are_allocated()
    {
        byte[] vp8 = WebpTestKit.Chunk(WebpTestKit.Read("blue-purple-pink.lossy.webp"), "VP8 ");
        byte[] header = HugeFrame(vp8)[..20];
        byte[] file = WebpTestKit.Riff(("VP8 ", header));
        Refused(file); // Warm up: the first call loads the code.

        long allocated = Allocated(() => Assert.Contains("cut short", Refused(file).Message, StringComparison.Ordinal));
        Assert.True(allocated < 2_000_000, $"{allocated} bytes were allocated for 20 bytes of data");
    }

    [Fact]
    public void A_lossy_picture_that_claims_more_rows_than_its_data_has_is_refused_at_the_first_row_that_lacks_its_data()
    {
        // The data of a small picture with the size of the frame patched to 16,383 by 2,441: it was decoded to the end from zero bits, about a second.
        byte[] vp8 = WebpTestKit.Chunk(WebpTestKit.Read("blue-purple-pink.lossy.webp"), "VP8 ");
        byte[] file = WebpTestKit.Riff(("VP8 ", HugeFrame(vp8)));
        Refused(file); // Warm up.

        Stopwatch guard = Stopwatch.StartNew();
        ImageFormatException error = Refused(file);

        // The signal is the row: the decoder checks the end of the data after every row, so the row it names is the first one that lacks data (a picture of 16,383 pixels has 1,024 macroblocks in a row, far more than this data holds). The time is only a guard against a hang.
        Assert.Contains("cut short", error.Message, StringComparison.Ordinal);
        Assert.Contains("in macroblock row 0.", error.Message, StringComparison.Ordinal);
        Assert.True(guard.ElapsedMilliseconds < 5000, $"took {guard.ElapsedMilliseconds} ms");
    }

    private static byte[] HugeFrame(byte[] vp8)
    {
        byte[] patched = (byte[])vp8.Clone();
        patched[6] = 0xff;
        patched[7] = 0x3f; // 16,383 wide.
        patched[8] = 0x89;
        patched[9] = 0x09; // 2,441 high.
        return patched;
    }

    // ---- The container

    [Fact]
    public void A_RIFF_file_that_is_not_a_WebP_says_what_it_is()
    {
        byte[] wave = [.. "RIFF"u8.ToArray(), 4, 0, 0, 0, .. "WAVE"u8.ToArray(), 0, 0, 0, 0];
        Assert.Contains("a RIFF file of type WAVE, not a WebP picture", Refused(wave).Message, StringComparison.Ordinal);

        byte[] odd = [.. "RIFF"u8.ToArray(), 4, 0, 0, 0, 0x01, (byte)'A', 0xff, (byte)'b'];
        Assert.Contains("a RIFF file of type ?A?b, not a WebP picture", Refused(odd).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_VP8X_chunk_that_is_not_the_first_chunk_is_refused()
    {
        byte[] vp8 = WebpTestKit.Chunk(WebpTestKit.Read("blue-purple-pink.lossy.webp"), "VP8 ");
        (int width, int height) = WebpTestKit.Vp8Size(vp8);
        byte[] vp8x = WebpTestKit.Vp8x(0, width, height);

        // In its place the picture is read, as it was before.
        Assert.Equal(width, ImageSource.FromBytes(WebpTestKit.Riff(("VP8X", vp8x), ("VP8 ", vp8))).Width);

        // After another chunk, or after an unknown one, it is not.
        Assert.Contains("not the first chunk", Refused(WebpTestKit.Riff(("ICCP", [1, 2]), ("VP8X", vp8x), ("VP8 ", vp8))).Message, StringComparison.Ordinal);
        Assert.Contains("not the first chunk", Refused(WebpTestKit.Riff(("JUNK", [1, 2, 3, 4]), ("VP8X", vp8x), ("VP8 ", vp8))).Message, StringComparison.Ordinal);
    }

    // ---- A tiny lossless picture can decode to the whole limit (documented in docs/known-limitations.md; there is no ratio check, as the file is valid)

    [Fact]
    public void A_valid_lossless_file_of_one_colour_decodes_to_four_bytes_a_pixel_of_what_it_says()
    {
        WebpTestKit.BitWriter bits = new();
        WebpTestKit.Vp8lHeader(bits, 4000, 1000);
        bits.Write(0, 1);
        bits.Write(0, 1);
        bits.Write(0, 1);
        WebpTestKit.SimpleGroup(bits, 10, 20, 30, 128);
        byte[] file = WebpTestKit.Riff(("VP8L", bits.ToArray()));
        Assert.True(file.Length < 60);
        ImageSource image = ImageSource.FromBytes(file);
        Assert.Equal(4000 * 1000 * 3, image.Data.Length);
        Assert.NotNull(image.Mask);
    }
}
