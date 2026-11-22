using System.Text;
using Pdfwright.Graphics;
using Pdfwright.Images;
using Pdfwright.Pdf;
using Pdfwright.Tests.Pdf;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Tokens;

namespace Pdfwright.Tests.Images;

/// <summary>A WebP picture drawn into a document: it is an image XObject with a soft mask for its transparency, exactly as a PNG is, qpdf accepts the file, and PDFium draws it as it draws the PNG.</summary>
public class WebpDocumentTests
{
    private const double PageHeight = 300;

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

    private static byte[] Document(PdfCompression compression, Action<Canvas> draw)
    {
        using MemoryStream stream = new();
        using (PdfDocumentWriter document = new(stream, new PdfWriterOptions { Compression = compression, Created = PdfTestKit.Created }))
        {
            PdfPageWriter page = document.BeginPage(400, PageHeight);
            using (Canvas canvas = new(page, 0, PageHeight, 400, PageHeight))
            {
                draw(canvas);
            }

            page.End();
            document.Complete();
        }

        return stream.ToArray();
    }

    [Fact]
    public void A_lossless_webp_with_transparency_is_rgb_samples_with_a_soft_mask_and_the_pixels_come_back()
    {
        ImageSource image = ImageSource.FromBytes(WebpTestKit.Read("tux.lossless.webp"));
        byte[] pdf = Document(PdfCompression.Flate, c => c.DrawImage(image, 20, 30, 200, 150));

        using UglyToad.PdfPig.PdfDocument document = UglyToad.PdfPig.PdfDocument.Open(pdf);
        IPdfImage placed = Assert.Single(document.GetPage(1).GetImages());
        Assert.Equal(image.Width, placed.WidthInSamples);
        Assert.Equal(image.Height, placed.HeightInSamples);
        Assert.True(placed.ImageDictionary.ContainsKey(NameToken.Smask));
        Assert.True(placed.TryGetBytesAsMemory(out Memory<byte> pixels));
        Assert.Equal(image.Data.ToArray(), pixels.ToArray());

        // The mask is an image of its own in the file, and it is the alpha of the picture.
        Assert.Equal(2, Count(pdf, "/Subtype /Image"));
        Assert.Equal(1, Count(pdf, "/SMask"));
        Assert.Equal(1, Count(pdf, "/ColorSpace /DeviceGray"));
        PdfTestKit.AssertQpdfAccepts(pdf, "webp-lossless-alpha");
    }

    [Fact]
    public void A_lossy_webp_without_transparency_has_no_mask_and_one_with_has()
    {
        ImageSource opaque = ImageSource.FromBytes(WebpTestKit.Read("yellow_rose.lossy.webp"));
        ImageSource clear = ImageSource.FromBytes(WebpTestKit.Read("yellow_rose.lossy-with-alpha.webp"));
        byte[] first = Document(PdfCompression.None, c => c.DrawImage(opaque, 0, 0, 100, 80));
        byte[] second = Document(PdfCompression.None, c => c.DrawImage(clear, 0, 0, 100, 80));
        Assert.Equal(1, Count(first, "/Subtype /Image"));
        Assert.Equal(0, Count(first, "/SMask"));
        Assert.Equal(2, Count(second, "/Subtype /Image"));
        Assert.Equal(1, Count(second, "/SMask"));
        Assert.Contains("/ColorSpace /DeviceRGB", Encoding.Latin1.GetString(first), StringComparison.Ordinal);
        PdfTestKit.AssertQpdfAccepts(first, "webp-lossy");
        PdfTestKit.AssertQpdfAccepts(second, "webp-lossy-alpha");
    }

    [Fact]
    public void A_webp_in_a_document_through_the_public_api_is_written_once_however_often_it_is_drawn()
    {
        PdfImage logo = PdfImage.FromBytes(WebpTestKit.Read("gopher-doc.with-alpha.lossless.webp"));
        ImageSource source = logo.Source;
        byte[] pdf = Document(PdfCompression.None, c =>
        {
            c.DrawImage(source, 0, 0, 40, 40);
            c.DrawImage(source, 60, 0, 40, 40);
            c.DrawImage(source, 120, 0, 40, 40, opacity: 0.5);
        });
        Assert.Equal(2, Count(pdf, "/Subtype /Image"));
        PdfTestKit.AssertQpdfAccepts(pdf, "webp-three-times");
    }

    [Theory]
    [InlineData("yellow_rose.lossless.webp", "yellow_rose.png")]
    [InlineData("tux.lossless.webp", "tux.png")]
    [InlineData("gopher-doc.with-alpha.lossless.webp", "gopher-doc.with-alpha.png")]
    [InlineData("gopher-doc.8bpp.lossless.webp", "gopher-doc.8bpp.png")]
    public void A_lossless_webp_is_drawn_pixel_for_pixel_as_the_png_it_equals(string webp, string png)
    {
        string? unavailable = RenderGolden.RendererUnavailable();
        if (unavailable is not null)
        {
            Assert.False(RenderGolden.Required, $"PDFWRIGHT_REQUIRE_RENDER=1 and {unavailable}");
            Assert.Skip(unavailable + "; set PDFWRIGHT_REQUIRE_RENDER=1 to make this a failure.");
            return;
        }

        ImageSource fromWebp = ImageSource.FromBytes(WebpTestKit.Read(webp));
        ImageSource fromPng = ImageSource.FromBytes(WebpTestKit.Read(png));
        byte[] pdfWebp = Document(PdfCompression.Flate, c => c.DrawImage(fromWebp, 20, 20, 300, 240));
        byte[] pdfPng = Document(PdfCompression.Flate, c => c.DrawImage(fromPng, 20, 20, 300, 240));

        RenderGolden.Page drawnWebp = Assert.Single(RenderGolden.Render(pdfWebp));
        RenderGolden.Page drawnPng = Assert.Single(RenderGolden.Render(pdfPng));
        Assert.Equal(drawnPng.Width, drawnWebp.Width);
        Assert.Equal(drawnPng.Height, drawnWebp.Height);

        // Not a blank page, and the same pixels.
        Assert.Contains(drawnWebp.Rgb, b => b != 255);
        Assert.True(drawnPng.Rgb.AsSpan().SequenceEqual(drawnWebp.Rgb), "the WebP is not drawn exactly as the PNG");
    }

    [Fact]
    public void A_lossy_webp_is_drawn_close_to_the_png_it_was_compressed_from()
    {
        string? unavailable = RenderGolden.RendererUnavailable();
        if (unavailable is not null)
        {
            Assert.False(RenderGolden.Required, $"PDFWRIGHT_REQUIRE_RENDER=1 and {unavailable}");
            Assert.Skip(unavailable + "; set PDFWRIGHT_REQUIRE_RENDER=1 to make this a failure.");
            return;
        }

        ImageSource lossy = ImageSource.FromBytes(WebpTestKit.Read("yellow_rose.lossy.webp"));
        ImageSource original = ImageSource.FromBytes(WebpTestKit.Read("yellow_rose.png"));
        RenderGolden.Page drawnLossy = Assert.Single(RenderGolden.Render(Document(PdfCompression.Flate, c => c.DrawImage(lossy, 20, 20, 300, 240))));
        RenderGolden.Page drawnOriginal = Assert.Single(RenderGolden.Render(Document(PdfCompression.Flate, c => c.DrawImage(original, 20, 20, 300, 240))));

        long sum = 0;
        for (int i = 0; i < drawnLossy.Rgb.Length; i++)
        {
            sum += Math.Abs(drawnLossy.Rgb[i] - drawnOriginal.Rgb[i]);
        }

        double mean = (double)sum / drawnLossy.Rgb.Length;
        Assert.True(mean < 4.0, $"the mean difference of the two pages is {mean:F2} levels");
    }
}
