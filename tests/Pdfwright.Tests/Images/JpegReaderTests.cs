using Pdfwright.Images;

namespace Pdfwright.Tests.Images;

public class JpegReaderTests
{
    private static readonly string RealJpeg = Path.Combine(AppContext.BaseDirectory, "Images", "Files", "gradient-24x16.jpg");

    [Fact]
    public void A_real_jpeg_is_read_for_its_size_and_kept_byte_for_byte()
    {
        byte[] file = File.ReadAllBytes(RealJpeg);
        ImageSource image = ImageSource.FromBytes(file);
        Assert.Equal(24, image.Width);
        Assert.Equal(16, image.Height);
        Assert.Equal(ImageColorSpace.Rgb, image.ColorSpace);
        Assert.True(image.IsJpeg);
        Assert.Null(image.Decode);
        Assert.Null(image.Mask);
        Assert.Equal(file, image.Data.ToArray());
    }

    [Theory]
    [InlineData(0xC0, 1, 0)]
    [InlineData(0xC1, 3, 1)]
    [InlineData(0xC2, 3, 1)]
    [InlineData(0xC0, 4, 2)]
    public void Baseline_extended_and_progressive_frames_are_read_by_their_components(int marker, int components, int spaceValue)
    {
        ImageSource image = ImageSource.FromJpeg(ImageTestKit.Jpeg(marker, 300, 200, components));
        Assert.Equal(300, image.Width);
        Assert.Equal(200, image.Height);
        Assert.Equal((ImageColorSpace)spaceValue, image.ColorSpace);
        Assert.Null(image.Decode);
    }

    [Fact]
    public void An_adobe_cmyk_jpeg_gets_the_inverting_decode_array()
    {
        ImageSource image = ImageSource.FromBytes(ImageTestKit.Jpeg(0xC0, 10, 10, 4, adobe: true));
        Assert.Equal(ImageColorSpace.Cmyk, image.ColorSpace);
        Assert.Equal(new double[] { 1, 0, 1, 0, 1, 0, 1, 0 }, image.Decode);

        // An Adobe marker on an RGB file changes nothing about the decode.
        Assert.Null(ImageSource.FromBytes(ImageTestKit.Jpeg(0xC0, 10, 10, 3, adobe: true)).Decode);
    }

    [Fact]
    public void The_stuffed_bytes_and_restart_markers_of_the_scan_are_data_and_the_file_ends_at_the_end_of_image()
    {
        byte[] file = ImageTestKit.Jpeg(0xC0, 8, 8, 3);
        byte[] withTrailer = [.. file, 0xDE, 0xAD, 0xBE, 0xEF];
        ImageSource image = ImageSource.FromBytes(withTrailer);
        Assert.Equal(file, image.Data.ToArray());
    }

    [Fact]
    public void A_progressive_file_with_several_scans_is_read_to_its_end()
    {
        byte[] first = ImageTestKit.Jpeg(0xC2, 20, 20, 1, endOfImage: false);
        byte[] second = ImageTestKit.Segment(0xDA, 1, 1, 0, 1, 63, 0);
        byte[] file = [.. first, .. second, 0x11, 0x22, 0xFF, 0xD9];
        Assert.Equal(file, ImageSource.FromBytes(file).Data.ToArray());
    }
}
