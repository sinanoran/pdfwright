using System.Buffers.Binary;
using System.Diagnostics;
using Pdfwright.Images;

namespace Pdfwright.Tests.Images;

/// <summary>Images that are cut short, damaged, too large or not images: a clear <see cref="ImageFormatException"/> every time, and never an exception of the runtime.</summary>
public class MalformedImageTests
{
    private static readonly string RealJpeg = Path.Combine(AppContext.BaseDirectory, "Images", "Files", "gradient-24x16.jpg");

    private static void AssertRejected(byte[] bytes, string? mentions = null)
    {
        Exception? thrown = Record.Exception(() => ImageSource.FromBytes(bytes));
        ImageFormatException exception = Assert.IsType<ImageFormatException>(thrown);
        if (mentions is not null)
        {
            Assert.Contains(mentions, exception.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static byte[] WithHeader(uint width, uint height, int depth = 8, int colorType = 2) =>
        ImageTestKit.Png(
            4,
            4,
            8,
            2,
            changeChunks: chunks =>
            {
                byte[] header = new byte[13];
                BinaryPrimitives.WriteUInt32BigEndian(header, width);
                BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), height);
                header[8] = (byte)depth;
                header[9] = (byte)colorType;
                chunks[0] = ("IHDR", header);
            });

    // ---- PNG

    [Fact]
    public void A_png_cut_at_any_length_is_rejected_with_a_typed_exception()
    {
        byte[][] pictures =
        [
            ImageTestKit.Png(7, 5, 8, 6),
            ImageTestKit.Png(7, 5, 4, 3, interlaced: true, palette: PngDecoderTests.PaletteFor(4)),
        ];
        foreach (byte[] png in pictures)
        {
            for (int length = 0; length < png.Length; length++)
            {
                AssertRejected(png[..length]);
            }
        }
    }

    [Fact]
    public void A_wrong_crc_is_rejected_in_every_chunk()
    {
        byte[] png = ImageTestKit.Png(7, 5, 8, 3, palette: PngDecoderTests.PaletteFor(8), transparency: [1, 2]);
        int position = 8;
        int chunks = 0;
        while (position < png.Length)
        {
            int length = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(position));
            byte[] damaged = (byte[])png.Clone();
            damaged[position + 8 + length] ^= 0x01;
            AssertRejected(damaged, "CRC");

            // A changed byte of the data is a wrong CRC as well.
            if (length > 0)
            {
                byte[] changed = (byte[])png.Clone();
                changed[position + 8] ^= 0x40;
                AssertRejected(changed, "CRC");
            }

            position += 12 + length;
            chunks++;
        }

        Assert.True(chunks >= 6);
    }

    [Fact]
    public void Damaged_compressed_data_behind_a_right_crc_is_rejected()
    {
        byte[] png = ImageTestKit.Png(20, 20, 8, 2, idatSize: 100000);
        int idat = IndexOf(png, "IDAT"u8) + 4;
        png[idat] = 0x00; // not a zlib header
        ImageTestKit.FixCrcs(png);
        AssertRejected(png, "damaged");
    }

    [Fact]
    public void Image_data_that_ends_before_the_last_row_is_rejected()
    {
        byte[] png = ImageTestKit.Png(30, 30, 8, 0, changeChunks: chunks =>
        {
            int first = chunks.FindIndex(c => c.Type == "IDAT");
            byte[] half = chunks[first].Data[..(chunks[first].Data.Length / 2)];
            chunks.RemoveAll(c => c.Type == "IDAT");
            chunks.Insert(first, ("IDAT", half));
        });
        AssertRejected(png);
    }

    [Theory]
    [InlineData(65536u, 1u)]
    [InlineData(1u, 65536u)]
    [InlineData(65535u, 65535u)]
    [InlineData(10001u, 10000u)]
    [InlineData(4294967295u, 4294967295u)]
    [InlineData(0u, 5u)]
    [InlineData(5u, 0u)]
    public void Dimensions_over_the_limits_or_of_nothing_are_rejected_before_anything_is_allocated(uint width, uint height)
    {
        Stopwatch watch = Stopwatch.StartNew();
        AssertRejected(WithHeader(width, height));
        Assert.True(watch.ElapsedMilliseconds < 2000);
    }

    [Fact]
    public void A_header_that_promises_more_than_the_data_can_hold_is_rejected_without_the_memory()
    {
        // 6,000 by 6,000 is inside the limit, and 80 bytes of compressed data cannot be that many rows.
        long before = GC.GetTotalAllocatedBytes(precise: false);
        AssertRejected(WithHeader(6000, 6000), "too small");
        Assert.True(GC.GetTotalAllocatedBytes(precise: false) - before < 50_000_000);
    }

    [Theory]
    [InlineData(8, 1)]
    [InlineData(4, 2)]
    [InlineData(16, 3)]
    [InlineData(8, 5)]
    [InlineData(3, 0)]
    public void A_colour_type_and_bit_depth_the_format_does_not_allow_are_rejected(int depth, int colorType)
    {
        AssertRejected(WithHeader(4, 4, depth, colorType));
    }

    [Fact]
    public void A_row_with_an_unknown_filter_is_rejected()
    {
        AssertRejected(ImageTestKit.Png(8, 8, 8, 2, filter: 9), "filter");
    }

    [Fact]
    public void An_indexed_image_needs_a_palette_and_every_index_must_be_in_it()
    {
        AssertRejected(ImageTestKit.Png(8, 8, 8, 3), "palette");
        byte[] small = [0, 0, 0, 255, 255, 255]; // two entries, and the picture uses four indexes
        AssertRejected(ImageTestKit.Png(8, 8, 2, 3, palette: small), "palette");
    }

    [Fact]
    public void A_transparency_chunk_of_the_wrong_size_is_rejected()
    {
        AssertRejected(ImageTestKit.Png(4, 4, 8, 0, transparency: [1, 2, 3]));
        AssertRejected(ImageTestKit.Png(4, 4, 8, 2, transparency: [1, 2]));
        AssertRejected(ImageTestKit.Png(4, 4, 2, 3, palette: PngDecoderTests.PaletteFor(2), transparency: [1, 2, 3, 4, 5, 6]));
    }

    [Fact]
    public void A_png_with_a_missing_end_chunk_or_a_first_chunk_that_is_not_the_header_is_rejected()
    {
        AssertRejected(ImageTestKit.Png(4, 4, 8, 0, changeChunks: chunks => chunks.RemoveAt(chunks.Count - 1)));
        AssertRejected(ImageTestKit.Png(4, 4, 8, 0, changeChunks: chunks => chunks.Reverse(0, 2)));
        AssertRejected(ImageTestKit.Png(4, 4, 8, 0, changeChunks: chunks => chunks.RemoveAll(c => c.Type == "IDAT")), "no image data");
        AssertRejected(ImageTestKit.Png(4, 4, 8, 0, changeChunks: chunks => chunks.Insert(1, chunks[0])), "two IHDR");
    }

    [Fact]
    public void Mutating_a_png_never_gives_anything_but_a_picture_or_a_typed_exception()
    {
        Random random = new(20261121);
        byte[][] pictures =
        [
            ImageTestKit.Png(11, 7, 8, 6, interlaced: true),
            ImageTestKit.Png(11, 7, 4, 3, palette: PngDecoderTests.PaletteFor(4), transparency: [0, 9]),
            ImageTestKit.Png(11, 7, 16, 2),
            ImageTestKit.Png(11, 7, 2, 0, transparency: [0, 2]),
        ];
        int accepted = 0;
        for (int i = 0; i < 4000; i++)
        {
            byte[] png = (byte[])pictures[i % pictures.Length].Clone();
            for (int k = random.Next(1, 4); k > 0; k--)
            {
                png[random.Next(png.Length)] = (byte)random.Next(256);
            }

            if (i % 2 == 0)
            {
                ImageTestKit.FixCrcs(png);
            }

            try
            {
                ImageSource.FromBytes(png);
                accepted++;
            }
            catch (ImageFormatException)
            {
            }
        }

        Assert.True(accepted < 4000);
    }

    // ---- JPEG

    [Fact]
    public void A_real_jpeg_cut_at_any_length_is_rejected()
    {
        byte[] jpeg = File.ReadAllBytes(RealJpeg);
        for (int length = 0; length < jpeg.Length; length++)
        {
            AssertRejected(jpeg[..length]);
        }
    }

    [Fact]
    public void A_jpeg_skeleton_cut_at_any_length_is_rejected()
    {
        byte[] jpeg = ImageTestKit.Jpeg(0xC2, 40, 30, 3, adobe: true);
        for (int length = 0; length < jpeg.Length; length++)
        {
            AssertRejected(jpeg[..length]);
        }
    }

    [Fact]
    public void Arithmetic_coding_twelve_bit_samples_and_lossless_are_rejected_with_their_reason()
    {
        AssertRejected(ImageTestKit.Jpeg(0xC9, 8, 8, 3), "arithmetic");
        AssertRejected(ImageTestKit.Jpeg(0xCA, 8, 8, 3), "arithmetic");
        AssertRejected(ImageTestKit.Jpeg(0xC0, 8, 8, 3, precision: 12), "12");
        AssertRejected(ImageTestKit.Jpeg(0xC1, 8, 8, 1, precision: 16), "16");
        AssertRejected(ImageTestKit.Jpeg(0xC3, 8, 8, 1), "lossless");
    }

    [Fact]
    public void A_jpeg_with_an_impossible_frame_is_rejected()
    {
        AssertRejected(ImageTestKit.Jpeg(0xC0, 0, 8, 3), "width");
        AssertRejected(ImageTestKit.Jpeg(0xC0, 8, 0, 3), "height");
        AssertRejected(ImageTestKit.Jpeg(0xC0, 8, 8, 2), "components");
        AssertRejected(ImageTestKit.Jpeg(0xC0, 8, 8, 5), "components");
        AssertRejected(ImageTestKit.Jpeg(0xC0, 65535, 65535, 3), "pixels");
    }

    [Fact]
    public void A_jpeg_with_no_end_of_image_marker_or_no_scan_is_rejected()
    {
        AssertRejected(ImageTestKit.Jpeg(0xC0, 8, 8, 3, endOfImage: false), "cut short");
        AssertRejected([0xFF, 0xD8, .. ImageTestKit.Frame(0xC0, 8, 8, 3), 0xFF, 0xD9], "no image data");
        AssertRejected([0xFF, 0xD8, 0xFF, 0xD9, 0x00, 0x00]);
    }

    [Fact]
    public void A_segment_that_runs_past_the_end_or_has_an_impossible_length_is_rejected()
    {
        AssertRejected([0xFF, 0xD8, 0xFF, 0xE0, 0xFF, 0xFF, 1, 2, 3]);
        AssertRejected([0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x01, 1, 2, 3, 4]);
        AssertRejected([0xFF, 0xD8, 0xFF, 0xE0, 0x00]);
        AssertRejected([0xFF, 0xD8, 0xFF]);
        AssertRejected([0xFF, 0xD8, 0x12, 0x34, 0x56, 0x78]);
    }

    [Fact]
    public void Random_bytes_are_rejected_whatever_they_start_with()
    {
        Random random = new(7);
        foreach (byte[] start in new byte[][] { [], [0xFF, 0xD8], [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], [1, 2, 3] })
        {
            for (int i = 0; i < 300; i++)
            {
                byte[] garbage = new byte[start.Length + random.Next(0, 200)];
                random.NextBytes(garbage);
                start.CopyTo(garbage, 0);
                AssertRejected(garbage);
            }
        }
    }

    [Fact]
    public void Mutating_a_jpeg_never_gives_anything_but_a_picture_or_a_typed_exception()
    {
        Random random = new(11);
        byte[][] pictures = [File.ReadAllBytes(RealJpeg), ImageTestKit.Jpeg(0xC2, 40, 30, 4, adobe: true)];
        for (int i = 0; i < 4000; i++)
        {
            byte[] jpeg = (byte[])pictures[i % pictures.Length].Clone();
            for (int k = random.Next(1, 4); k > 0; k--)
            {
                jpeg[random.Next(jpeg.Length)] = (byte)random.Next(256);
            }

            try
            {
                ImageSource.FromBytes(jpeg);
            }
            catch (ImageFormatException)
            {
            }
        }
    }

    [Fact]
    public void Something_that_is_neither_is_rejected()
    {
        AssertRejected("GIF89a......"u8.ToArray(), "neither");
        AssertRejected([], "neither");
    }

    private static int IndexOf(byte[] data, ReadOnlySpan<byte> pattern)
    {
        int found = data.AsSpan().IndexOf(pattern);
        Assert.True(found >= 0);
        return found;
    }
}
