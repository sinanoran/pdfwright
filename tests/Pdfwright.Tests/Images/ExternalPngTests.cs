using Pdfwright.Images;

namespace Pdfwright.Tests.Images;

/// <summary>
/// PNG files written by an encoder that is not the test kit's own (WPF's <c>PngBitmapEncoder</c>, made with a throwaway script and kept in
/// <c>Images/Files</c>), so that the decoder is not only tested against the author's reading of the format. The pixels are known from the
/// formulas the files were made with.
/// </summary>
public class ExternalPngTests
{
    private static ImageSource Read(string name) => ImageSource.FromBytes(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Images", "Files", name)));

    [Fact]
    public void A_4_bit_palette_picture_keeps_its_indices_and_its_palette()
    {
        // 5 x 3: the index of a pixel is (x + 5 y) % 16, and the colour of index i is (16 i, 255 - 16 i, 37 i % 256).
        ImageSource image = Read("wpf-palette4-5x3.png");
        Assert.Equal((5, 3), (image.Width, image.Height));
        Assert.Equal(ImageColorSpace.Indexed, image.ColorSpace);
        Assert.Null(image.Mask);

        byte[] data = image.Data.ToArray();
        Assert.Equal(15, data.Length);
        for (int y = 0; y < 3; y++)
        {
            for (int x = 0; x < 5; x++)
            {
                Assert.Equal((x + (5 * y)) % 16, data[(y * 5) + x]);
            }
        }

        byte[] palette = image.Palette!;
        for (int i = 0; i < 16; i++)
        {
            Assert.Equal([(byte)(16 * i), (byte)(255 - (16 * i)), (byte)((37 * i) % 256)], palette[(i * 3)..((i * 3) + 3)]);
        }
    }

    [Fact]
    public void A_16_bit_gray_picture_is_rounded_to_8_bits()
    {
        // 4 x 3: the sample of a pixel is (x + 4 y) * 5000 + 123 of 65535.
        ImageSource image = Read("wpf-gray16-4x3.png");
        Assert.Equal((4, 3), (image.Width, image.Height));
        Assert.Equal(ImageColorSpace.Gray, image.ColorSpace);
        Assert.Null(image.Mask);

        byte[] data = image.Data.ToArray();
        Assert.Equal(12, data.Length);
        for (int i = 0; i < 12; i++)
        {
            Assert.Equal((byte)((((i * 5000) + 123) * 255L + 32767) / 65535), data[i]);
        }
    }

    [Fact]
    public void An_RGBA_picture_gives_its_colours_and_its_alpha_as_a_mask()
    {
        // 4 x 3: R = 60 x, G = 100 y, B = 200 - 10 x - 5 y, A = 255 in the first row and 80 x + 15 below it.
        ImageSource image = Read("wpf-rgba-4x3.png");
        Assert.Equal((4, 3), (image.Width, image.Height));
        Assert.Equal(ImageColorSpace.Rgb, image.ColorSpace);

        byte[] data = image.Data.ToArray();
        byte[] mask = image.Mask!.Value.ToArray();
        for (int y = 0; y < 3; y++)
        {
            for (int x = 0; x < 4; x++)
            {
                int at = ((y * 4) + x) * 3;
                Assert.Equal([(byte)(60 * x), (byte)(100 * y), (byte)(200 - (10 * x) - (5 * y))], data[at..(at + 3)]);
                Assert.Equal(y == 0 ? 255 : (80 * x) + 15, mask[(y * 4) + x]);
            }
        }
    }

    [Fact]
    public void An_interlaced_picture_is_put_back_in_order()
    {
        // 9 x 9, Adam7: R = 28 x, G = 28 y, B = 14 (x + y), as bytes.
        ImageSource image = Read("wpf-rgb-interlaced-9x9.png");
        Assert.Equal((9, 9), (image.Width, image.Height));
        Assert.Equal(ImageColorSpace.Rgb, image.ColorSpace);
        Assert.Null(image.Mask);

        byte[] data = image.Data.ToArray();
        for (int y = 0; y < 9; y++)
        {
            for (int x = 0; x < 9; x++)
            {
                int at = ((y * 9) + x) * 3;
                Assert.Equal([(byte)(28 * x), (byte)(28 * y), (byte)(14 * (x + y))], data[at..(at + 3)]);
            }
        }
    }
}
