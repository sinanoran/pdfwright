#pragma warning disable CA1416
using SkiaSharp;

namespace Pdfwright.Tests.Pdf;

/// <summary>
/// The comparator of <see cref="RenderGolden"/> on its own: it must pass identical pages and a one-pixel shift, and fail on everything that is a real change, down to one missing
/// mark, one moved mark, a light rule that is gone or a different colour. The pages are the stored PNGs (decoded, no renderer needed) and synthetic pages.
/// <c>PDFWRIGHT_SWEEP_ALL=1</c> runs the mark sweeps over every mark of every page instead of an evenly spaced sample (about a minute).
/// </summary>
public class RenderCompareTests
{
    private static string Renders => Path.Combine(Golden.Folder, "Renders");

    private static bool SweepAll => Environment.GetEnvironmentVariable("PDFWRIGHT_SWEEP_ALL") == "1";

    private static RenderGolden.Page Load(string file) => RenderGolden.Decode(File.ReadAllBytes(Path.Combine(Renders, file)));

    private static RenderGolden.Page With(RenderGolden.Page page, Action<byte[]> change)
    {
        byte[] copy = (byte[])page.Rgb.Clone();
        change(copy);
        return page with { Rgb = copy };
    }

    private static int Luma(RenderGolden.Page page, int p) => ((page.Rgb[p * 3] * 299) + (page.Rgb[(p * 3) + 1] * 587) + (page.Rgb[(p * 3) + 2] * 114) + 500) / 1000;

    private static void Set(byte[] rgb, int p, byte r, byte g, byte b)
    {
        rgb[p * 3] = r;
        rgb[(p * 3) + 1] = g;
        rgb[(p * 3) + 2] = b;
    }

    private static RenderGolden.Page Solid(int w, int h, byte r, byte g, byte b)
    {
        byte[] rgb = new byte[w * h * 3];
        for (int p = 0; p < w * h; p++)
        {
            Set(rgb, p, r, g, b);
        }

        return new RenderGolden.Page(w, h, rgb);
    }

    private static RenderGolden.Page Shifted(RenderGolden.Page page, int dx, int dy) => With(page, rgb =>
    {
        Array.Fill(rgb, (byte)255);
        for (int y = 0; y < page.Height; y++)
        {
            for (int x = 0; x < page.Width; x++)
            {
                int sx = x - dx, sy = y - dy;
                if (sx >= 0 && sx < page.Width && sy >= 0 && sy < page.Height)
                {
                    Array.Copy(page.Rgb, ((sy * page.Width) + sx) * 3, rgb, ((y * page.Width) + x) * 3, 3);
                }
            }
        }
    });

    /// <summary>The 8-connected components of the pixels darker than luminance 192: each dot, diacritic, glyph part or rule of a page.</summary>
    private static List<List<int>> Marks(RenderGolden.Page page)
    {
        int w = page.Width, h = page.Height;
        int[] label = new int[w * h];
        List<List<int>> marks = [];
        for (int start = 0; start < label.Length; start++)
        {
            if (Luma(page, start) >= 192 || label[start] != 0)
            {
                continue;
            }

            List<int> mark = [];
            Stack<int> stack = new([start]);
            label[start] = marks.Count + 1;
            while (stack.Count > 0)
            {
                int q = stack.Pop();
                mark.Add(q);
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = (q % w) + dx, ny = (q / w) + dy;
                        if (nx < 0 || ny < 0 || nx >= w || ny >= h)
                        {
                            continue;
                        }

                        int n = (ny * w) + nx;
                        if (Luma(page, n) < 192 && label[n] == 0)
                        {
                            label[n] = marks.Count + 1;
                            stack.Push(n);
                        }
                    }
                }
            }

            marks.Add(mark);
        }

        return marks;
    }

    private static List<List<int>> Sample(List<List<int>> marks, int count) =>
        SweepAll || marks.Count <= count ? marks : Enumerable.Range(0, count).Select(i => marks[i * marks.Count / count]).ToList();

    /// <summary>The pixels of a mark and their neighbours: what a one-pixel tolerance would have to see empty for the mark to be gone.</summary>
    private static HashSet<int> Around(RenderGolden.Page page, List<int> mark)
    {
        HashSet<int> box = [];
        foreach (int q in mark)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = (q % page.Width) + dx, ny = (q / page.Width) + dy;
                    if (nx >= 0 && ny >= 0 && nx < page.Width && ny < page.Height)
                    {
                        _ = box.Add((ny * page.Width) + nx);
                    }
                }
            }
        }

        return box;
    }

    public static TheoryData<string> Files() => new(Directory.GetFiles(Renders, "*.png").Select(f => Path.GetFileName(f)!).Order());

    public static TheoryData<string> ScriptFiles() => new("arabic-short-p1.png", "hebrew-short-p1.png", "devanagari-short-p1.png", "bengali-short-p1.png", "tamil-short-p1.png", "thai-short-p1.png", "mixed-short-p1.png", "readme-report-p1.png");

    [Theory]
    [MemberData(nameof(Files))]
    public void An_identical_page_passes_and_a_one_pixel_shift_passes(string file)
    {
        RenderGolden.Page e = Load(file);
        Assert.False(RenderGolden.Compare(e, e).Fails);
        Assert.False(RenderGolden.Compare(Shifted(e, 1, 0), e).Fails);
        Assert.False(RenderGolden.Compare(Shifted(e, 0, 1), e).Fails);
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void A_blank_page_a_black_page_and_a_two_pixel_shift_fail(string file)
    {
        RenderGolden.Page e = Load(file);
        Assert.True(RenderGolden.Compare(Solid(e.Width, e.Height, 255, 255, 255), e).Fails);
        Assert.True(RenderGolden.Compare(Solid(e.Width, e.Height, 0, 0, 0), e).Fails);
        Assert.True(RenderGolden.Compare(Shifted(e, 2, 0), e).Fails);
        Assert.True(RenderGolden.Compare(Shifted(e, 0, 2), e).Fails);
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void The_comparison_is_the_same_with_the_pages_swapped(string file)
    {
        RenderGolden.Page e = Load(file);
        RenderGolden.Page s = Shifted(e, 2, 1);
        RenderGolden.Difference ab = RenderGolden.Compare(s, e);
        RenderGolden.Difference ba = RenderGolden.Compare(e, s);
        Assert.Equal(ab.Differing, ba.Differing);
        Assert.Equal(ab.LargestCluster, ba.LargestCluster);
        Assert.Equal(ab.Mask, ba.Mask);
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void Erasing_any_one_mark_fails(string file)
    {
        RenderGolden.Page e = Load(file);
        List<string> missed = [];
        int tested = 0;
        foreach (List<int> mark in Sample(Marks(e).Where(m => m.Count >= 2).ToList(), 30))
        {
            tested++;
            HashSet<int> box = Around(e, mark);
            RenderGolden.Page erased = With(e, rgb =>
            {
                foreach (int q in box)
                {
                    Set(rgb, q, 255, 255, 255);
                }
            });
            RenderGolden.Difference d = RenderGolden.Compare(erased, e);
            Assert.Equal(d.Differing, RenderGolden.Compare(e, erased).Differing);
            if (!d.Fails)
            {
                missed.Add($"{mark.Count}px mark at {mark[0] % e.Width},{mark[0] / e.Width}: {d.Differing} differing");
            }
        }

        Report(file, tested, missed.Count);
        Assert.True(missed.Count == 0, file + ": " + string.Join("; ", missed));
    }

    [Theory]
    [MemberData(nameof(ScriptFiles))]
    public void Moving_any_one_small_mark_by_five_pixels_fails(string file)
    {
        RenderGolden.Page e = Load(file);
        List<string> missed = [];
        int tested = 0;
        foreach (List<int> mark in Sample(Marks(e).Where(m => m.Count is >= 2 and <= 15).ToList(), 30))
        {
            tested++;
            HashSet<int> box = Around(e, mark);
            RenderGolden.Page moved = With(e, rgb =>
            {
                foreach (int q in box)
                {
                    Set(rgb, q, 255, 255, 255);
                }

                foreach (int q in mark)
                {
                    int nx = (q % e.Width) + 5, ny = q / e.Width;
                    if (nx < e.Width)
                    {
                        Array.Copy(e.Rgb, q * 3, rgb, ((ny * e.Width) + nx) * 3, 3);
                    }
                }
            });
            RenderGolden.Difference d = RenderGolden.Compare(moved, e);
            if (!d.Fails)
            {
                missed.Add($"{mark.Count}px mark at {mark[0] % e.Width},{mark[0] / e.Width}: {d.Differing} differing");
            }
        }

        Report(file, tested, missed.Count);
        Assert.True(missed.Count == 0, file + ": " + string.Join("; ", missed));
    }

    [Fact]
    public void A_light_rule_that_is_gone_fails()
    {
        // A table rule of luminance 179 (the colour of the README report's rules), 2 pixels thick, 300 long.
        RenderGolden.Page white = Solid(400, 100, 255, 255, 255);
        RenderGolden.Page ruled = With(white, rgb =>
        {
            for (int y = 50; y < 52; y++)
            {
                for (int x = 50; x < 350; x++)
                {
                    Set(rgb, (y * 400) + x, 179, 179, 179);
                }
            }
        });
        Assert.True(RenderGolden.Compare(white, ruled).Fails);
        Assert.True(RenderGolden.Compare(ruled, white).Fails);
        Assert.False(RenderGolden.Compare(ruled, ruled).Fails);
    }

    [Theory]
    [InlineData(255, 0, 0)]
    [InlineData(0, 0, 255)]
    [InlineData(0, 128, 0)]
    [InlineData(51, 102, 204)]
    [InlineData(153, 153, 153)]
    public void A_rectangle_of_another_colour_fails(byte r, byte g, byte b)
    {
        RenderGolden.Page black = With(Solid(200, 200, 255, 255, 255), rgb => FillRectangle(rgb, 200, 0, 0, 0));
        RenderGolden.Page other = With(Solid(200, 200, 255, 255, 255), rgb => FillRectangle(rgb, 200, r, g, b));
        Assert.True(RenderGolden.Compare(other, black).Fails);
        Assert.True(RenderGolden.Compare(black, other).Fails);
    }

    [Fact]
    public void The_stored_pages_are_colour_pngs_of_a_reasonable_size()
    {
        long total = Directory.GetFiles(Renders, "*.png").Sum(f => new FileInfo(f).Length);
        Assert.InRange(total, 1, 1_500_000);
        using SKBitmap bitmap = SKBitmap.Decode(File.ReadAllBytes(Directory.GetFiles(Renders, "*.png")[0]));
        Assert.Equal(793, bitmap.Width);
    }

    private static void Report(string file, int tested, int missed)
    {
        if (Environment.GetEnvironmentVariable("PDFWRIGHT_SWEEP_REPORT") is { Length: > 0 } path)
        {
            lock (Renders)
            {
                File.AppendAllText(path, $"{file} marks={tested} undetected={missed}" + Environment.NewLine);
            }
        }
    }

    private static void FillRectangle(byte[] rgb, int width, byte r, byte g, byte b)
    {
        for (int y = 40; y < 140; y++)
        {
            for (int x = 40; x < 140; x++)
            {
                Set(rgb, (y * width) + x, r, g, b);
            }
        }
    }
}
