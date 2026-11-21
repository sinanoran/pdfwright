#pragma warning disable CA1416 // PDFtoImage is annotated for Windows, Linux, macOS, Android, iOS and the browser; the tests run on the first three, and Render() refuses the rest.
using System.Text;
using PDFtoImage;
using SkiaSharp;

namespace Pdfwright.Tests.Pdf;

/// <summary>
/// The visual half of the golden files: every page of a golden document is drawn by PDFium (through PDFtoImage) at a fixed resolution and compared with the PNG
/// stored in <c>Golden/Renders</c> under a tight tolerance, so that a mark in the wrong place, a missing dot, a lost rule, a changed colour or a mirrored glyph is caught even where
/// text extraction would still read the right characters. The comparison is described at <see cref="Compare"/>. <c>PDFWRIGHT_UPDATE_GOLDENS=1</c> rewrites the PNGs (refused when <c>CI</c> is set);
/// a failure writes the page as drawn and a difference image to <c>PDFWRIGHT_ACTUAL_DIR</c> (the temporary folder when it is not set).
/// <c>PDFWRIGHT_REQUIRE_RENDER=1</c> turns a renderer that cannot load into a failure instead of a skip, as <c>PDFWRIGHT_REQUIRE_QPDF</c> does for qpdf.
/// </summary>
internal static class RenderGolden
{
    /// <summary>The resolution of every render. 96 dpi draws an A4 page as 793 x 1122 pixels: small PNGs, and a mark that is a third of an em off is several pixels.</summary>
    internal const int Dpi = 96;

    /// <summary>A pixel differs when no pixel of the other image within this many pixels is within <see cref="ChannelThreshold"/> of it on every colour channel.</summary>
    internal const int Radius = 1;

    /// <summary>
    /// How far (0 to 255) a colour channel may be from the other image's before two pixels are different. Low enough that a light table rule (luminance 179, 76 from white) counts as ink and
    /// that black against red, green or blue is a difference; above the shift of an anti-aliased edge, which the neighbourhood of <see cref="Radius"/> absorbs.
    /// </summary>
    internal const int ChannelThreshold = 40;

    /// <summary>A page fails when more than this many pixels differ in total: one lone pixel is tolerated, any two are not (so a missing dot, a moved mark or a lost rule fails, and so does one moved single-pixel speck). Three operating systems measured 0 differing pixels on every golden page, so the limit is a margin for a future PDFium, not for noise.</summary>
    internal const int MaxDifferingPixels = 1;


    private static readonly object PdfiumLock = new();

    private static string? unavailable;
    private static bool probed;

    private static bool Updating =>
        Environment.GetEnvironmentVariable("PDFWRIGHT_UPDATE_GOLDENS") == "1" || Environment.GetEnvironmentVariable("PDFWRIGHT_UPDATE_GOLDEN") == "1";

    internal static bool Required => Environment.GetEnvironmentVariable("PDFWRIGHT_REQUIRE_RENDER") == "1";

    private static string RendersFolder => Path.Combine(Golden.Folder, "Renders");

    /// <summary>One page as red, green and blue bytes (three per pixel, 255 white), row by row.</summary>
    internal sealed record Page(int Width, int Height, byte[] Rgb);

    /// <summary>The outcome of <see cref="Compare"/>: the pixels that differ, the largest 8-connected group of them, and a mask (1 where a pixel differs).</summary>
    internal sealed record Difference(int Differing, int LargestCluster, byte[] Mask)
    {
        internal bool Fails => Differing > MaxDifferingPixels;
    }

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
            // Anti-aliasing stays on (a page without it is a different picture on every platform's rasteriser, not a steadier one); the neighbourhood of the comparison absorbs its shifts.
            RenderOptions options = new() { Dpi = Dpi, WithAnnotations = false, WithFormFill = false, BackgroundColor = SKColors.White, AntiAliasing = PdfAntiAliasing.All };
            int count = Conversion.GetPageCount(pdf);
            for (int i = 0; i < count; i++)
            {
                using SKBitmap bitmap = Conversion.ToImage(pdf, i, null, options);
                pages.Add(new Page(bitmap.Width, bitmap.Height, ToRgb(bitmap)));
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
    /// Compares a page with its stored page. A pixel counts as different when, within <see cref="Radius"/> pixels of it in the other image, there is no pixel within
    /// <see cref="ChannelThreshold"/> of it on every colour channel (so an edge that moved by a pixel, or a different anti-aliasing, is not a difference, and a glyph that moved by
    /// more, a mirrored glyph, a missing mark, a lost light rule or a changed colour is). The test is made both ways, so the result is the same with the two pages swapped.
    /// The page fails (<see cref="Difference.Fails"/>) on more than <see cref="MaxDifferingPixels"/> differing pixels.
    /// </summary>
    internal static Difference Compare(Page actual, Page expected)
    {
        int w = actual.Width, h = actual.Height;
        byte[] a = actual.Rgb, e = expected.Rgb;
        byte[] mask = new byte[w * h];
        int differing = 0;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int p = (y * w) + x;
                if (Close(a, p, e, p))
                {
                    continue;
                }

                if (!Near(e, a, p, x, y, w, h) || !Near(a, e, p, x, y, w, h))
                {
                    mask[p] = 1;
                    differing++;
                }
            }
        }

        return new Difference(differing, LargestCluster(mask, w, h), mask);
    }

    private static bool Close(byte[] a, int pa, byte[] b, int pb)
    {
        pa *= 3;
        pb *= 3;
        return Math.Abs(a[pa] - b[pb]) <= ChannelThreshold && Math.Abs(a[pa + 1] - b[pb + 1]) <= ChannelThreshold && Math.Abs(a[pa + 2] - b[pb + 2]) <= ChannelThreshold;
    }

    /// <summary>Whether a pixel of <paramref name="other"/> within the radius of (x, y) is close to pixel <paramref name="p"/> of <paramref name="own"/>.</summary>
    private static bool Near(byte[] other, byte[] own, int p, int x, int y, int w, int h)
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
                if (xx >= 0 && xx < w && Close(own, p, other, (yy * w) + xx))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static int LargestCluster(byte[] mask, int w, int h)
    {
        int largest = 0;
        byte[] seen = new byte[mask.Length];
        Stack<int> stack = new();
        for (int start = 0; start < mask.Length; start++)
        {
            if (mask[start] == 0 || seen[start] != 0)
            {
                continue;
            }

            int size = 0;
            seen[start] = 1;
            stack.Push(start);
            while (stack.Count > 0)
            {
                int q = stack.Pop();
                size++;
                int qx = q % w, qy = q / w;
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = qx + dx, ny = qy + dy;
                        if (nx < 0 || ny < 0 || nx >= w || ny >= h)
                        {
                            continue;
                        }

                        int n = (ny * w) + nx;
                        if (mask[n] != 0 && seen[n] == 0)
                        {
                            seen[n] = 1;
                            stack.Push(n);
                        }
                    }
                }
            }

            largest = Math.Max(largest, size);
        }

        return largest;
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

            Difference difference = Compare(pages[i], expected);
            Report(name, i, difference);
            if (difference.Fails)
            {
                failures.Add($"page {i + 1}: {difference.Differing} pixels differ, {difference.LargestCluster} of them together (limit {MaxDifferingPixels})");
                WriteActual(name, i, pages[i], (expected, difference.Mask));
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
    private static void Report(string name, int pageIndex, Difference difference)
    {
        if (Environment.GetEnvironmentVariable("PDFWRIGHT_RENDER_REPORT") is { Length: > 0 } file)
        {
            lock (PdfiumLock)
            {
                _ = System.IO.Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
                File.AppendAllText(file, $"{name} p{pageIndex + 1} differing={difference.Differing} cluster={difference.LargestCluster}\n");
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
        SKColor[] pixels = new SKColor[d.Mask.Length];
        for (int p = 0; p < pixels.Length; p++)
        {
            byte Fade(int channel) => (byte)(255 - ((255 - d.Expected.Rgb[(p * 3) + channel]) / 3));
            pixels[p] = d.Mask[p] == 1 ? new SKColor(220, 0, 0) : new SKColor(Fade(0), Fade(1), Fade(2));
        }

        bitmap.Pixels = pixels;
        using SKData data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(Path.Combine(ActualFolder(), "pdfwright-diff-" + stem), data.ToArray());
    }

    private static byte[] ToRgb(SKBitmap bitmap)
    {
        SKColor[] pixels = bitmap.Pixels;
        byte[] rgb = new byte[pixels.Length * 3];
        for (int p = 0; p < pixels.Length; p++)
        {
            rgb[p * 3] = pixels[p].Red;
            rgb[(p * 3) + 1] = pixels[p].Green;
            rgb[(p * 3) + 2] = pixels[p].Blue;
        }

        return rgb;
    }

    internal static byte[] Encode(Page page)
    {
        using SKBitmap bitmap = new(page.Width, page.Height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        SKColor[] pixels = new SKColor[page.Width * page.Height];
        for (int p = 0; p < pixels.Length; p++)
        {
            pixels[p] = new SKColor(page.Rgb[p * 3], page.Rgb[(p * 3) + 1], page.Rgb[(p * 3) + 2]);
        }

        bitmap.Pixels = pixels;
        using SKData data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    internal static Page Decode(byte[] png)
    {
        using SKBitmap bitmap = SKBitmap.Decode(png);
        return new Page(bitmap.Width, bitmap.Height, ToRgb(bitmap));
    }

    /// <summary>The smallest PDF that has a page, to find out whether the renderer loads.</summary>
    private static readonly byte[] Probe = Encoding.ASCII.GetBytes(
        "%PDF-1.4\n1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj\n2 0 obj<</Type/Pages/Kids[3 0 R]/Count 1>>endobj\n3 0 obj<</Type/Page/Parent 2 0 R/MediaBox[0 0 20 20]>>endobj\ntrailer<</Root 1 0 R>>\n");
}
