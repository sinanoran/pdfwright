#pragma warning disable CA1416 // PDFtoImage is annotated for Windows, Linux, macOS, Android, iOS and the browser; the tests run on the first three, and Render() refuses the rest.
using System.Runtime.InteropServices;
using System.Text;
using PDFtoImage;
using SkiaSharp;

namespace Pdfwright.Tests.Pdf;

/// <summary>
/// The visual half of the golden files: every page of a golden document is drawn by PDFium (through PDFtoImage) at a fixed resolution and compared with the PNG
/// stored in <c>Golden/Renders</c> under a perceptual tolerance, so that a mark in the wrong place or a mirrored glyph is caught even where text extraction would
/// still read the right characters. The comparison is described at <see cref="Compare"/>. <c>PDFWRIGHT_UPDATE_GOLDENS=1</c> rewrites the PNGs (refused when <c>CI</c> is set);
/// a failure writes the page as drawn and a difference image to <c>PDFWRIGHT_ACTUAL_DIR</c> (the temporary folder when it is not set).
/// <c>PDFWRIGHT_REQUIRE_RENDER=1</c> turns a renderer that cannot load into a failure instead of a skip, as <c>PDFWRIGHT_REQUIRE_QPDF</c> does for qpdf.
/// </summary>
internal static class RenderGolden
{
    /// <summary>The resolution of every render. 96 dpi draws an A4 page as 794 x 1123 pixels: small PNGs, and a mark that is a third of an em off is several pixels.</summary>
    internal const int Dpi = 96;

    /// <summary>A pixel differs when no pixel of the other image within this many pixels has a luminance within <see cref="LuminanceThreshold"/> of it.</summary>
    internal const int Radius = 1;

    /// <summary>Luminance (0 to 255) beyond which two pixels are different: well above the grey of an anti-aliased edge, well below black against white.</summary>
    internal const int LuminanceThreshold = 96;

    /// <summary>The share of differing pixels, among the pixels that carry ink in either page, above which a page fails.</summary>
    internal const double MaxDifferingShare = 0.02;

    private static readonly object PdfiumLock = new();

    private static string? unavailable;
    private static bool probed;

    private static bool Updating =>
        Environment.GetEnvironmentVariable("PDFWRIGHT_UPDATE_GOLDENS") == "1" || Environment.GetEnvironmentVariable("PDFWRIGHT_UPDATE_GOLDEN") == "1";

    internal static bool Required => Environment.GetEnvironmentVariable("PDFWRIGHT_REQUIRE_RENDER") == "1";

    private static string RendersFolder => Path.Combine(Golden.Folder, "Renders");

    /// <summary>One page as luminance bytes (0 black, 255 white), row by row.</summary>
    internal sealed record Page(int Width, int Height, byte[] Luma);

    /// <summary>Draws every page of a PDF. Throws when the native renderer cannot be loaded (see <see cref="RendererUnavailable"/>).</summary>
    internal static List<Page> Render(byte[] pdf)
    {
        List<Page> pages = [];
        if (!(OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()))
        {
            throw new PlatformNotSupportedException("PDFium is loaded on Windows, Linux and macOS only");
        }

        lock (PdfiumLock)
        {
            // Anti-aliasing stays on (a page without it is a different picture on every platform's rasteriser, not a steadier one); the tolerance absorbs its shifts.
            RenderOptions options = new() { Dpi = Dpi, WithAnnotations = false, WithFormFill = false, BackgroundColor = SKColors.White, AntiAliasing = PdfAntiAliasing.All };
            int count = Conversion.GetPageCount(pdf);
            for (int i = 0; i < count; i++)
            {
                using SKBitmap bitmap = Conversion.ToImage(pdf, i, null, options);
                SKColor[] pixels = bitmap.Pixels;
                byte[] luma = new byte[pixels.Length];
                for (int p = 0; p < pixels.Length; p++)
                {
                    SKColor c = pixels[p];
                    luma[p] = (byte)(((c.Red * 299) + (c.Green * 587) + (c.Blue * 114) + 500) / 1000);
                }

                pages.Add(new Page(bitmap.Width, bitmap.Height, luma));
            }
        }

        return pages;
    }

    /// <summary>The reason the native renderer is missing, or <c>null</c> when it loads and draws.</summary>
    internal static string? RendererUnavailable()
    {
        lock (PdfiumLock)
        {
            if (!probed)
            {
                probed = true;
                try
                {
                    _ = Render(Probe);
                }
                catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or TypeInitializationException or EntryPointNotFoundException or PlatformNotSupportedException)
                {
                    unavailable = $"PDFium/Skia could not be loaded on this platform ({ex.GetType().Name}: {ex.Message})";
                }
            }

            return unavailable;
        }
    }

    /// <summary>
    /// Compares a page with its stored page. A pixel counts as different when, within <see cref="Radius"/> pixels of it in the other image, there is no pixel whose
    /// luminance is within <see cref="LuminanceThreshold"/> of it (so an edge that moved by a pixel, or a different anti-aliasing, is not a difference, and a glyph that moved
    /// by more, a mirrored glyph or a missing mark is). The result is the count of such pixels and their share of the pixels that carry ink (luminance under 192) in either page.
    /// </summary>
    internal static (int Differing, double Share, byte[] Mask) Compare(Page actual, Page expected)
    {
        int w = actual.Width, h = actual.Height;
        byte[] mask = new byte[w * h];
        int differing = 0, ink = 0;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int p = (y * w) + x;
                byte a = actual.Luma[p], e = expected.Luma[p];
                if (a < 192 || e < 192)
                {
                    ink++;
                }

                if (Math.Abs(a - e) <= LuminanceThreshold)
                {
                    continue;
                }

                if (!Near(expected.Luma, a, x, y, w, h) || !Near(actual.Luma, e, x, y, w, h))
                {
                    mask[p] = 1;
                    differing++;
                }
            }
        }

        return (differing, differing / (double)Math.Max(1, ink), mask);
    }

    private static bool Near(byte[] other, byte value, int x, int y, int w, int h)
    {
        for (int dy = -Radius; dy <= Radius; dy++)
        {
            int yy = y + dy;
            if (yy < 0 || yy >= h)
            {
                continue;
            }

            for (int dx = -Radius; dx <= Radius; dx++)
            {
                int xx = x + dx;
                if (xx >= 0 && xx < w && Math.Abs(other[(yy * w) + xx] - value) <= LuminanceThreshold)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Renders <paramref name="pdf"/> and compares every page with <c>Renders/&lt;name&gt;-p&lt;n&gt;.png</c>.</summary>
    internal static void Check(string name, byte[] pdf)
    {
        if (RendererUnavailable() is { } reason)
        {
            if (Required)
            {
                Assert.Fail($"PDFWRIGHT_REQUIRE_RENDER=1 and {reason}");
            }

            Assert.Skip(reason + "; set PDFWRIGHT_REQUIRE_RENDER=1 to make this a failure.");
            return;
        }

        List<Page> pages = Render(pdf);
        if (Updating)
        {
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI")))
            {
                Assert.Fail("PDFWRIGHT_UPDATE_GOLDENS is set while the CI environment variable is set: renders are regenerated by hand, never in a CI run.");
            }

            _ = System.IO.Directory.CreateDirectory(RendersFolder);
            foreach (string old in System.IO.Directory.GetFiles(RendersFolder, $"{Path.GetFileNameWithoutExtension(name)}-p*.png"))
            {
                File.Delete(old);
            }

            for (int i = 0; i < pages.Count; i++)
            {
                File.WriteAllBytes(PathOf(name, i), Encode(pages[i]));
            }

            return;
        }

        List<string> failures = [];
        for (int i = 0; i < pages.Count; i++)
        {
            string stored = PathOf(name, i);
            if (!File.Exists(stored))
            {
                failures.Add($"page {i + 1}: {Path.GetFileName(stored)} is not in Golden/Renders; create it with PDFWRIGHT_UPDATE_GOLDENS=1");
                continue;
            }

            Page expected = Decode(File.ReadAllBytes(stored));
            if (expected.Width != pages[i].Width || expected.Height != pages[i].Height)
            {
                failures.Add($"page {i + 1}: drawn as {pages[i].Width} x {pages[i].Height} pixels, stored as {expected.Width} x {expected.Height}");
                WriteActual(name, i, pages[i], null);
                continue;
            }

            (int differing, double share, byte[] mask) = Compare(pages[i], expected);
            Report(name, i, differing, share);
            if (share > MaxDifferingShare)
            {
                failures.Add($"page {i + 1}: {differing} pixels differ ({share:P2} of the inked pixels, limit {MaxDifferingShare:P2})");
                WriteActual(name, i, pages[i], (expected, mask));
            }
        }

        if (File.Exists(PathOf(name, pages.Count)))
        {
            failures.Add($"the document has {pages.Count} pages and Golden/Renders has a page {pages.Count + 1}");
        }

        if (failures.Count > 0)
        {
            Assert.Fail(
                $"{name} does not look like its stored renders at {Dpi} dpi:\n  " + string.Join("\n  ", failures)
                + $"\nThe page as drawn (pdfwright-actual-*.png) and the differences in red (pdfwright-diff-*.png) are in {ActualFolder()}."
                + "\nIf the change is intended, run the tests with PDFWRIGHT_UPDATE_GOLDENS=1 and commit the regenerated PNGs by themselves (tests/Pdfwright.Tests/Golden/README.md).");
        }
    }

    /// <summary>PDFWRIGHT_RENDER_REPORT=&lt;file&gt; appends one line for each page compared, to measure the difference between two platforms.</summary>
    private static void Report(string name, int pageIndex, int differing, double share)
    {
        if (Environment.GetEnvironmentVariable("PDFWRIGHT_RENDER_REPORT") is { Length: > 0 } file)
        {
            lock (PdfiumLock)
            {
                _ = System.IO.Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
                File.AppendAllText(file, $"{name} p{pageIndex + 1} differing={differing} share={share:0.00000}\n");
            }
        }
    }

    private static string PathOf(string name, int pageIndex) => Path.Combine(RendersFolder, $"{Path.GetFileNameWithoutExtension(name)}-p{pageIndex + 1}.png");

    private static string ActualFolder()
    {
        string folder = Environment.GetEnvironmentVariable("PDFWRIGHT_ACTUAL_DIR") is { Length: > 0 } custom ? custom : Path.GetTempPath();
        _ = System.IO.Directory.CreateDirectory(folder);
        return folder;
    }

    private static void WriteActual(string name, int pageIndex, Page actual, (Page Expected, byte[] Mask)? diff)
    {
        string stem = $"{Path.GetFileNameWithoutExtension(name)}-p{pageIndex + 1}.png";
        File.WriteAllBytes(Path.Combine(ActualFolder(), "pdfwright-actual-" + stem), Encode(actual));
        if (diff is not { } d)
        {
            return;
        }

        // The stored page, faded, with the pixels that differ in red.
        using SKBitmap bitmap = new(actual.Width, actual.Height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        SKColor[] pixels = new SKColor[actual.Luma.Length];
        for (int p = 0; p < pixels.Length; p++)
        {
            byte faded = (byte)(255 - ((255 - d.Expected.Luma[p]) / 3));
            pixels[p] = d.Mask[p] == 1 ? new SKColor(220, 0, 0) : new SKColor(faded, faded, faded);
        }

        bitmap.Pixels = pixels;
        using SKData data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(Path.Combine(ActualFolder(), "pdfwright-diff-" + stem), data.ToArray());
    }

    private static byte[] Encode(Page page)
    {
        using SKBitmap bitmap = new(page.Width, page.Height, SKColorType.Gray8, SKAlphaType.Opaque);
        Marshal.Copy(page.Luma, 0, bitmap.GetPixels(), page.Luma.Length);
        using SKData data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static Page Decode(byte[] png)
    {
        using SKBitmap bitmap = SKBitmap.Decode(png);
        SKColor[] pixels = bitmap.Pixels;
        byte[] luma = new byte[pixels.Length];
        for (int p = 0; p < pixels.Length; p++)
        {
            luma[p] = pixels[p].Red;
        }

        return new Page(bitmap.Width, bitmap.Height, luma);
    }

    /// <summary>The smallest PDF that has a page, to find out whether the renderer loads.</summary>
    private static readonly byte[] Probe = Encoding.ASCII.GetBytes(
        "%PDF-1.4\n1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj\n2 0 obj<</Type/Pages/Kids[3 0 R]/Count 1>>endobj\n3 0 obj<</Type/Page/Parent 2 0 R/MediaBox[0 0 20 20]>>endobj\ntrailer<</Root 1 0 R>>\n");
}
