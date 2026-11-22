using System.Buffers.Binary;

namespace Pdfwright.Tests.Images;

/// <summary>The pixel limit of <see cref="PdfImage.FromBytes(ReadOnlySpan{byte})"/> and the option that changes it. The size is checked in the header, so a picture of any size is tried with a header and no pixels.</summary>
public class PixelLimitTests
{
    /// <summary>A PNG with the header of the given size and the data of a small picture: if the size is allowed, the reader goes on and finds the data too small, and that message is how a test knows it was let through.</summary>
    private static byte[] PngOfSize(uint width, uint height) =>
        ImageTestKit.Png(
            4,
            4,
            8,
            6,
            changeChunks: chunks =>
            {
                byte[] header = new byte[13];
                BinaryPrimitives.WriteUInt32BigEndian(header, width);
                BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), height);
                header[8] = 8;
                header[9] = 6;
                chunks[0] = ("IHDR", header);
            });

    private static byte[] JpegOfSize(int width, int height) => ImageTestKit.Jpeg(0xC0, width, height, 3);

    private static ArgumentException Refused(Func<PdfImage> read) => Assert.Throws<ArgumentException>(read);

    private static void AssertLimitMessage(ArgumentException exception, string format, string size, string limit)
    {
        Assert.Equal("bytes", exception.ParamName);
        Assert.Contains(format, exception.Message, StringComparison.Ordinal);
        Assert.Contains(size, exception.Message, StringComparison.Ordinal);
        Assert.Contains(limit, exception.Message, StringComparison.Ordinal);
        Assert.Contains("PdfImageOptions.MaxPixels", exception.Message, StringComparison.Ordinal);
    }

    private static long Allocated(Action action)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        action();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Fact]
    public void The_default_limit_is_40_million_pixels()
    {
        Assert.Equal(40_000_000, new PdfImageOptions().MaxPixels);
    }

    [Fact]
    public void A_png_of_40_million_pixels_is_let_through_and_one_of_more_is_refused_before_anything_is_allocated()
    {
        byte[] exact = PngOfSize(8000, 5000);
        byte[] over = PngOfSize(8000, 5001);

        // Let through: the reader goes on, and the small data cannot be 40 million pixels.
        ArgumentException cut = Refused(() => PdfImage.FromBytes(exact));
        Assert.Contains("too small", cut.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("PdfImageOptions", cut.Message, StringComparison.Ordinal);

        ArgumentException large = Refused(() => PdfImage.FromBytes(over));
        AssertLimitMessage(large, "PNG", "8,000 by 5,001 pixels (40,008,000 in all)", "40,000,000");
        Assert.DoesNotContain("not a usable", large.Message, StringComparison.Ordinal);

        Assert.True(Allocated(() => Refused(() => PdfImage.FromBytes(over))) < 5_000_000);
        Assert.True(Allocated(() => Refused(() => PdfImage.FromBytes(exact))) < 5_000_000);
    }

    [Fact]
    public void The_message_says_how_to_raise_the_limit_and_how_far()
    {
        ArgumentException large = Refused(() => PdfImage.FromBytes(PngOfSize(10000, 5000)));

        Assert.Contains("larger MaxPixels", large.Message, StringComparison.Ordinal);
        Assert.Contains("100,000,000", large.Message, StringComparison.Ordinal);
        Assert.Contains("every format", large.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_one_argument_overload_reads_like_the_default_options()
    {
        byte[] png = PngOfSize(8000, 5001);

        ArgumentException one = Refused(() => PdfImage.FromBytes(png));
        ArgumentException two = Refused(() => PdfImage.FromBytes(png, new PdfImageOptions()));
        Assert.Equal(one.Message, two.Message);
    }

    [Fact]
    public void The_option_raises_the_limit()
    {
        byte[] png = PngOfSize(8000, 5001);

        ArgumentException cut = Refused(() => PdfImage.FromBytes(png, new PdfImageOptions { MaxPixels = 100_000_000 }));
        Assert.Contains("too small", cut.Message, StringComparison.Ordinal);

        // At the largest limit the message cannot tell the caller to raise it further.
        ArgumentException largest = Refused(() => PdfImage.FromBytes(PngOfSize(10001, 10000), new PdfImageOptions { MaxPixels = 100_000_000 }));
        AssertLimitMessage(largest, "PNG", "10,001 by 10,000 pixels (100,010,000 in all)", "100,000,000");
        Assert.Contains("largest limit", largest.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("larger MaxPixels", largest.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_option_lowers_the_limit_and_the_limit_is_exact()
    {
        byte[] picture = ImageTestKit.Png(4, 3, 8, 6);

        Assert.Equal(4, PdfImage.FromBytes(picture, new PdfImageOptions { MaxPixels = 12 }).PixelWidth);
        ArgumentException refused = Refused(() => PdfImage.FromBytes(picture, new PdfImageOptions { MaxPixels = 11 }));
        AssertLimitMessage(refused, "PNG", "4 by 3 pixels (12 in all)", "11 pixels");

        Assert.Equal(1, PdfImage.FromBytes(ImageTestKit.Png(1, 1, 8, 6), new PdfImageOptions { MaxPixels = 1 }).PixelWidth);
    }

    [Fact]
    public void A_jpeg_has_the_same_limit_and_says_so()
    {
        // A JPEG is not decoded, so the whole file is read here, and the picture of exactly the limit is accepted.
        PdfImage exact = PdfImage.FromBytes(JpegOfSize(8000, 5000));
        Assert.Equal(8000, exact.PixelWidth);
        Assert.Equal(5000, exact.PixelHeight);

        ArgumentException large = Refused(() => PdfImage.FromBytes(JpegOfSize(8000, 5001)));
        AssertLimitMessage(large, "JPEG", "8,000 by 5,001 pixels (40,008,000 in all)", "40,000,000");

        Assert.Equal(8000, PdfImage.FromBytes(JpegOfSize(8000, 5001), new PdfImageOptions { MaxPixels = 41_000_000 }).PixelWidth);
        AssertLimitMessage(Refused(() => PdfImage.FromBytes(JpegOfSize(40, 30), new PdfImageOptions { MaxPixels = 1199 })), "JPEG", "40 by 30 pixels (1,200 in all)", "1,199");

        // A JPEG of the largest size a header can say is refused even at the largest limit.
        Refused(() => PdfImage.FromBytes(JpegOfSize(65535, 65535), new PdfImageOptions { MaxPixels = 100_000_000 }));
    }

    [Fact]
    public void The_side_limit_still_applies_whatever_the_pixel_limit_is()
    {
        ArgumentException wide = Refused(() => PdfImage.FromBytes(PngOfSize(65536, 1), new PdfImageOptions { MaxPixels = 100_000_000 }));
        Assert.Contains("65,535", wide.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(long.MinValue)]
    [InlineData(100_000_001L)]
    [InlineData(long.MaxValue)]
    public void A_limit_out_of_range_is_refused_where_it_is_set(long value)
    {
        ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(() => new PdfImageOptions { MaxPixels = value });

        Assert.Equal("MaxPixels", error.ParamName);
        Assert.Contains("100,000,000", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1L)]
    [InlineData(100_000_000L)]
    public void The_ends_of_the_range_are_allowed(long value)
    {
        Assert.Equal(value, new PdfImageOptions { MaxPixels = value }.MaxPixels);
    }

    [Fact]
    public void Options_that_are_null_are_refused()
    {
        ArgumentNullException error = Assert.Throws<ArgumentNullException>(() => PdfImage.FromBytes(ImageTestKit.Png(4, 3, 8, 6), null!));
        Assert.Equal("options", error.ParamName);
    }
}
