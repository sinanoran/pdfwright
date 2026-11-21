using System.Text;
using Pdfwright.Drawing;
using Pdfwright.Images;
using Pdfwright.Pdf;
using Pdfwright.Tests.Drawing;
using Pdfwright.Tests.Pdf;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Tokens;

namespace Pdfwright.Tests.Images;

/// <summary>Images drawn on a canvas and written into a file, read back with PdfPig.</summary>
public class ImageDocumentTests
{
    private const double PageHeight = 300;

    private static readonly string RealJpeg = System.IO.Path.Combine(AppContext.BaseDirectory, "Images", "Files", "gradient-24x16.jpg");

    private static int Count(byte[] pdf, string text)
    {
        string all = Encoding.Latin1.GetString(pdf);
        int count = 0;
        for (int at = all.IndexOf(text, StringComparison.Ordinal); at >= 0; at = all.IndexOf(text, at + text.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static byte[] Document(PdfCompression compression, Action<Canvas> draw, int pages = 1)
    {
        using MemoryStream stream = new();
        using (PdfDocumentWriter document = new(stream, new PdfWriterOptions { Compression = compression, Created = PdfTestKit.Created }))
        {
            for (int i = 0; i < pages; i++)
            {
                PdfPageWriter page = document.BeginPage(400, PageHeight);
                using (Canvas canvas = new(page, 0, PageHeight, 400, PageHeight))
                {
                    draw(canvas);
                }

                page.End();
            }

            document.Complete();
        }

        return stream.ToArray();
    }

    [Fact]
    public void A_jpeg_is_passed_through_as_dctdecode_with_its_size_and_the_bytes_it_came_in()
    {
        byte[] file = File.ReadAllBytes(RealJpeg);
        ImageSource image = ImageSource.FromBytes(file);
        byte[] pdf = Document(PdfCompression.Flate, c => c.DrawImage(image, 20, 30, 120, 80));

        using UglyToad.PdfPig.PdfDocument document = UglyToad.PdfPig.PdfDocument.Open(pdf);
        IPdfImage placed = Assert.Single(document.GetPage(1).GetImages());
        Assert.Equal(24, placed.WidthInSamples);
        Assert.Equal(16, placed.HeightInSamples);
        Assert.Equal("DCTDecode", Filter(placed));
        Assert.True(placed.RawMemory.Span.SequenceEqual(file), "the JPEG bytes are in the file as they came");

        Assert.Equal(20, placed.BoundingBox.Left, 0.01);
        Assert.Equal(PageHeight - 30 - 80, placed.BoundingBox.Bottom, 0.01);
        Assert.Equal(120, placed.BoundingBox.Width, 0.01);
        Assert.Equal(80, placed.BoundingBox.Height, 0.01);
        PdfTestKit.AssertQpdfAccepts(pdf, "jpeg-image");
    }

    [Fact]
    public void The_matrix_of_an_image_puts_its_first_row_at_the_top_of_the_rectangle()
    {
        ImageSource image = ImageSource.FromBytes(File.ReadAllBytes(RealJpeg));
        Drawn drawn = CanvasTestKit.Run(c => c.DrawImage(image, 20, 30, 120, 80), left: 0, top: 300);

        double[] flip = CanvasTestKit.Numbers(drawn.Lines[1]);
        double[] cm = CanvasTestKit.Numbers(Assert.Single(drawn.Lines, l => l.EndsWith(" cm", StringComparison.Ordinal) && l != drawn.Lines[1]));
        Matrix page = new Matrix(cm[0], cm[1], cm[2], cm[3], cm[4], cm[5]).Then(new Matrix(flip[0], flip[1], flip[2], flip[3], flip[4], flip[5]));

        // The image's unit square, (0,0) bottom left to (1,1) top: a positive scale in y means it is upright, and it fills the rectangle.
        Point bottomLeft = page.Apply(new Point(0, 0));
        Point topRight = page.Apply(new Point(1, 1));
        Assert.Equal(20, bottomLeft.X, 9);
        Assert.Equal(300 - 30 - 80, bottomLeft.Y, 9);
        Assert.Equal(140, topRight.X, 9);
        Assert.Equal(300 - 30, topRight.Y, 9);
        Assert.Contains("/X1 Do", drawn.Lines);
    }

    [Fact]
    public void An_rgba_png_is_flate_samples_with_a_soft_mask_and_the_pixels_come_back()
    {
        byte[] png = ImageTestKit.Png(13, 11, 8, 6, interlaced: true);
        ImageSource image = ImageSource.FromBytes(png);
        (_, byte[] samples, byte[]? alpha, _) = PngDecoderTests.Expected(13, 11, 8, 6, null, null);
        byte[] pdf = Document(PdfCompression.Flate, c => c.DrawImage(image, 10, 10, 130, 110));

        using UglyToad.PdfPig.PdfDocument document = UglyToad.PdfPig.PdfDocument.Open(pdf);
        IPdfImage placed = Assert.Single(document.GetPage(1).GetImages());
        Assert.Equal(13, placed.WidthInSamples);
        Assert.Equal(11, placed.HeightInSamples);
        Assert.Equal("FlateDecode", Filter(placed));
        Assert.True(placed.ImageDictionary.ContainsKey(NameToken.Smask));
        Assert.True(placed.TryGetBytesAsMemory(out Memory<byte> pixels));
        Assert.Equal(samples, pixels.ToArray());

        // The mask is an image of its own, in the file, with the alpha as it was in the PNG.
        Assert.Equal(2, Count(pdf, "/Subtype /Image"));
        Assert.NotNull(alpha);
        PdfTestKit.AssertQpdfAccepts(pdf, "png-rgba");
    }

    [Theory]
    [InlineData(0, 8)]
    [InlineData(0, 2)]
    [InlineData(2, 16)]
    [InlineData(3, 4)]
    [InlineData(4, 16)]
    public void The_pixels_of_a_png_come_back_from_the_file_as_the_reader_made_them(int type, int depth)
    {
        byte[]? palette = type == 3 ? PngDecoderTests.PaletteFor(depth) : null;
        byte[] png = ImageTestKit.Png(21, 13, depth, type, palette: palette);
        ImageSource image = ImageSource.FromBytes(png);
        (_, byte[] samples, _, _) = PngDecoderTests.Expected(21, 13, depth, type, palette, null);
        byte[] pdf = Document(PdfCompression.Flate, c => c.DrawImage(image, 0, 0, 21, 13));

        using UglyToad.PdfPig.PdfDocument document = UglyToad.PdfPig.PdfDocument.Open(pdf);
        IPdfImage placed = Assert.Single(document.GetPage(1).GetImages());
        Assert.Equal(21, placed.WidthInSamples);
        Assert.Equal("FlateDecode", Filter(placed));
        Assert.True(placed.TryGetBytesAsMemory(out Memory<byte> pixels));
        Assert.Equal(samples, pixels.ToArray());
    }

    [Fact]
    public void A_palette_image_is_indexed_and_a_palette_with_transparency_is_rgb_with_a_mask()
    {
        byte[] palette = PngDecoderTests.PaletteFor(4);
        ImageSource indexed = ImageSource.FromBytes(ImageTestKit.Png(21, 13, 4, 3, palette: palette));
        ImageSource clear = ImageSource.FromBytes(ImageTestKit.Png(21, 13, 4, 3, palette: palette, transparency: [0, 100]));
        byte[] pdf = Document(PdfCompression.None, c =>
        {
            c.DrawImage(indexed, 0, 0, 21, 13);
            c.DrawImage(clear, 30, 0, 21, 13);
        });

        string text = Encoding.Latin1.GetString(pdf);
        Assert.Contains("/ColorSpace [/Indexed /DeviceRGB 15 <", text, StringComparison.Ordinal);
        Assert.Equal(1, Count(pdf, "/Indexed"));
        Assert.Equal(1, Count(pdf, "/SMask"));
        PdfTestKit.AssertQpdfAccepts(pdf, "png-palette");
    }

    [Fact]
    public void An_adobe_cmyk_jpeg_writes_its_decode_array()
    {
        ImageSource image = ImageSource.FromBytes(ImageTestKit.Jpeg(0xC0, 10, 10, 4, adobe: true));
        byte[] pdf = Document(PdfCompression.None, c => c.DrawImage(image, 0, 0, 10, 10));
        string text = Encoding.Latin1.GetString(pdf);
        Assert.Contains("/ColorSpace /DeviceCMYK", text, StringComparison.Ordinal);
        Assert.Contains("/Decode [1 0 1 0 1 0 1 0]", text, StringComparison.Ordinal);
        Assert.Contains("/Filter /DCTDecode", text, StringComparison.Ordinal);
    }

    [Fact]
    public void An_image_drawn_many_times_and_on_many_pages_is_written_once_to_a_document_and_again_to_the_next()
    {
        ImageSource image = ImageSource.FromBytes(File.ReadAllBytes(RealJpeg));
        byte[] first = Document(PdfCompression.None, c =>
        {
            c.DrawImage(image, 0, 0, 10, 10);
            c.DrawImage(image, 20, 0, 10, 10);
            c.DrawImage(image, 40, 0, 10, 10, opacity: 0.5);
        }, pages: 3);
        byte[] second = Document(PdfCompression.None, c => c.DrawImage(image, 0, 0, 10, 10));

        Assert.Equal(1, Count(first, "/Subtype /Image"));
        Assert.Equal(1, Count(second, "/Subtype /Image"));
        using UglyToad.PdfPig.PdfDocument document = UglyToad.PdfPig.PdfDocument.Open(first);
        Assert.Equal(3, document.NumberOfPages);
        Assert.Equal(3, document.GetPage(2).GetImages().Count());
    }

    [Fact]
    public void An_image_with_an_opacity_selects_the_fill_opacity_state()
    {
        ImageSource image = ImageSource.FromBytes(File.ReadAllBytes(RealJpeg));
        Drawn drawn = CanvasTestKit.Run(c => c.DrawImage(image, 0, 0, 10, 10, opacity: 0.4));
        Assert.Contains("/G1 gs", drawn.Lines);
        Assert.Equal([("G1", 0.4, 1.0)], drawn.States.ToArray());
    }

    [Fact]
    public void A_rectangle_that_is_not_finite_or_has_no_area_is_refused()
    {
        ImageSource image = ImageSource.FromBytes(File.ReadAllBytes(RealJpeg));
        Drawn ignored = CanvasTestKit.Run(c =>
        {
            Assert.Throws<ArgumentException>(() => c.DrawImage(image, double.NaN, 0, 10, 10));
            Assert.Throws<ArgumentException>(() => c.DrawImage(image, 0, 0, 0, 10));
            Assert.Throws<ArgumentException>(() => c.DrawImage(image, 0, 0, 10, -1));
            Assert.Throws<ArgumentException>(() => c.DrawImage(image, 0, 0, 10, 10, opacity: 2));
        });
        Assert.Equal(CanvasTestKit.Wrap(), ignored.Lines);
    }

    [Fact]
    public void A_document_with_images_is_the_same_bytes_every_time()
    {
        ImageSource jpeg = ImageSource.FromBytes(File.ReadAllBytes(RealJpeg));
        ImageSource png = ImageSource.FromBytes(ImageTestKit.Png(13, 11, 8, 6));
        byte[] Make() => Document(PdfCompression.Flate, c =>
        {
            c.DrawImage(jpeg, 0, 0, 24, 16);
            c.DrawImage(png, 30, 0, 26, 22, opacity: 0.5);
        });
        Assert.Equal(Make(), Make());
    }

    private static string? Filter(IPdfImage image) =>
        image.ImageDictionary.TryGet(NameToken.Filter, out IToken? token) && token is NameToken name ? name.Data : null;
}
