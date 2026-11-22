using System.Text;
using Pdfwright.Text;

namespace Pdfwright.Tests.Text;

/// <summary>Further checks of the shaper's faster paths and of its per-thread buffer.</summary>
[Collection("ShapingFastPaths")]
public class ShapingBufferTests
{
    private static string D(ShapedGlyph[] glyphs) => string.Join(";", glyphs.Select(g => $"{g.Glyph}/{g.Cluster}/{g.XAdvance}/{g.XOffset}/{g.YOffset}"));

    private static string D(ShapedParagraph p)
    {
        StringBuilder sb = new();
        sb.Append(p.ParagraphLevel).Append('|').Append(string.Join(',', p.Missing)).Append('|').Append(string.Join(',', p.Bidi.Levels)).Append('|');
        foreach (ShapedRun r in p.Runs)
        {
            sb.Append($"[{r.Utf16Start},{r.Utf16Length},{r.Level},{r.Script},{r.FontIndex},{r.Style}:{D(r.Glyphs)}]");
        }

        return sb.ToString();
    }

    private static string WithoutFastPaths(Func<string> f)
    {
        TextShaper.FastPaths = false;
        try
        {
            return f();
        }
        finally
        {
            TextShaper.FastPaths = true;
        }
    }

    private static T OnFreshThread<T>(Func<T> f)
    {
        T result = default!;
        Thread thread = new(() => result = f());
        thread.Start();
        thread.Join();
        return result;
    }

    [Fact]
    public void Paths_agree_with_styles_and_languages()
    {
        ShapingFont[] fonts = LayoutTestKit.Fonts(LayoutTestKit.Arabic, LayoutTestKit.Latin);
        int[] order = [0, 1];
        foreach (string text in new[] { "fi ij IJ i I", "abc def ghi", "مرحبا abc", "abc مرحبا بالعالم", "x" })
        {
            foreach (string? language in new string?[] { null, "tr", "ar" })
            {
                foreach (int level in new[] { -1, 0, 1 })
                {
                    int cut = Math.Min(3, text.Length);
                    ShapeStyling styling = new()
                    {
                        Spans = [new StyledSpan(0, cut, 0), new StyledSpan(cut, text.Length - cut, 1)],
                        FontOrders = [order, order],
                        ShapeKeys = [0, 1],
                        Languages = [language, language == "tr" ? "ar" : "tr"],
                    };
                    if (text.Length == cut)
                    {
                        styling = new ShapeStyling { Spans = [new StyledSpan(0, cut, 0)], FontOrders = [order], ShapeKeys = [0], Languages = [language] };
                    }

                    string expected = WithoutFastPaths(() => D(TextShaper.Shape(text, fonts, level, language, null, styling)));
                    Assert.Equal(expected, D(TextShaper.Shape(text, fonts, level, language, null, styling)));
                    Assert.Equal(expected, D(TextShaper.Shape(text, fonts, level, language, null, styling)));
                    string plain = WithoutFastPaths(() => D(TextShaper.Shape(text, fonts, level, language)));
                    Assert.Equal(plain, D(TextShaper.Shape(text, fonts, level, language)));
                    Assert.Equal(plain, D(TextShaper.Shape(text, fonts, level, language)));
                }
            }
        }
    }

    [Fact]
    public void A_reused_buffer_gives_what_a_fresh_one_gives()
    {
        ShapingFont arabic = LayoutTestKit.Font(LayoutTestKit.Arabic);
        ShapingFont latin = LayoutTestKit.Font(LayoutTestKit.Latin);
        ShapingFont devanagari = LayoutTestKit.Font("NotoSansDevanagari-Regular.ttf");
        IShaper shaper = arabic.Shaper;
        string ar = "مرحبا بالعالم";
        string lat = "office fi Ta";
        string dv = "नमस्ते क्षत्रिय";

        string Arabic() => D(shaper.Shape(arabic.Handle, "xx" + ar + "yy", 2, ar.Length, true, "Arab", "ar"));
        string Latin() => D(shaper.Shape(latin.Handle, lat, 0, lat.Length, false, "Latn", null));
        string Devanagari() => D(shaper.Shape(devanagari.Handle, dv, 0, dv.Length, false, "Deva", null));
        string SubRun() => D(shaper.Shape(arabic.Handle, ar, 3, 4, true, "Arab", null));

        string[] expected = [OnFreshThread(Arabic), OnFreshThread(Latin), OnFreshThread(Devanagari), OnFreshThread(SubRun)];
        string[] reused = OnFreshThread(() => new[] { Arabic(), Latin(), Devanagari(), SubRun(), Latin(), Arabic() });
        Assert.Equal(expected[0], reused[0]);
        Assert.Equal(expected[1], reused[1]);
        Assert.Equal(expected[2], reused[2]);
        Assert.Equal(expected[3], reused[3]);
        Assert.Equal(expected[1], reused[4]);
        Assert.Equal(expected[0], reused[5]);
    }

    [Fact]
    public void A_level_that_is_not_0_1_or_automatic_is_refused_on_every_path()
    {
        ShapingFont[] latin = LayoutTestKit.Fonts(LayoutTestKit.Latin);
        Assert.Throws<ArgumentOutOfRangeException>(() => TextShaper.Shape("abc", latin, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => TextShaper.Shape("abc", latin, 7));
        Assert.Equal(0, TextShaper.Shape("abc", latin, 0).ParagraphLevel);
        Assert.Equal(0, TextShaper.Shape("abc", latin).ParagraphLevel);
        Assert.Equal(1, TextShaper.Shape("abc", latin, 1).ParagraphLevel);
        TextShaper.FastPaths = false;
        try
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => TextShaper.Shape("abc", latin, 2));
        }
        finally
        {
            TextShaper.FastPaths = true;
        }
    }

    [Fact]
    public void A_long_run_does_not_leave_a_large_buffer_on_the_thread()
    {
        ShapingFont latin = LayoutTestKit.Font(LayoutTestKit.Latin);
        string big = new('a', 2_000_000);
        int afterSmall = 0, afterBig = 0, afterMore = 0;
        long glyphs = 0;
        Thread thread = new(() =>
        {
            latin.Shaper.Shape(latin.Handle, "small", 0, 5, false, "Latn", null);
            afterSmall = HarfBuzzShaper.ThreadBufferLength;
            glyphs = latin.Shaper.Shape(latin.Handle, big, 0, big.Length, false, "Latn", null).Length;
            afterBig = HarfBuzzShaper.ThreadBufferLength;
            for (int i = 0; i < 10; i++)
            {
                latin.Shaper.Shape(latin.Handle, "small " + i, 0, 7, false, "Latn", null);
            }

            afterMore = HarfBuzzShaper.ThreadBufferLength;
        });
        thread.Start();
        thread.Join();
        Assert.Equal(big.Length, glyphs);
        Assert.InRange(afterSmall, 0, HarfBuzzShaper.ReusedBufferLimit);
        Assert.InRange(afterBig, 0, HarfBuzzShaper.ReusedBufferLimit);
        Assert.InRange(afterMore, 0, HarfBuzzShaper.ReusedBufferLimit);
    }
}