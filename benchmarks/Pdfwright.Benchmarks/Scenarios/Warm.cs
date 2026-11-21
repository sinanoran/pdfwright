using System.Diagnostics;
using System.Globalization;

namespace Pdfwright.Benchmarks.Scenarios;

/// <summary>The scenarios as verbs of the program: one run of a fresh process, and a warm in-process loop.</summary>
internal static class Warm
{
    internal static Func<FontCollection, PdfDocument>? Find(string name) => name switch
    {
        "table" => fonts => ScenarioDocuments.Table(fonts),
        "report" => fonts => ScenarioDocuments.Report(fonts),
        _ => null,
    };

    /// <summary>One run of a fresh process: load the fonts, build, save to a file, and print what the process itself saw as one JSON line.</summary>
    internal static int Once(Func<FontCollection, PdfDocument> build, string path, TextWriter output)
    {
        using Process self = Process.GetCurrentProcess();
        using FontCollection fonts = ScenarioDocuments.LoadFonts();
        PdfDocument document = build(fonts);
        using (FileStream file = File.Create(path))
        {
            document.Save(file);
        }

        double sinceStart = (DateTime.Now - self.StartTime).TotalMilliseconds;
        self.Refresh();
        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{{\"sinceProcessStartMs\":{sinceStart:F1},\"peakWorkingSetMb\":{self.PeakWorkingSet64 / 1048576.0:F1},\"bytes\":{new FileInfo(path).Length},\"pages\":{document.Diagnostics.PageCount}}}"));
        return 0;
    }

    /// <summary>Warm: the document is built and saved to memory <paramref name="runs"/> times in one process after <paramref name="warmup"/> unmeasured ones; the fonts are loaded once.</summary>
    internal static int Loop(Func<FontCollection, PdfDocument> build, int runs, int warmup, TextWriter output)
    {
        using FontCollection fonts = ScenarioDocuments.LoadFonts();
        using MemoryStream stream = new();
        PdfDocument last = null!;
        List<double> times = [];
        List<long> allocated = [];
        for (int i = 0; i < warmup + runs; i++)
        {
            stream.SetLength(0);
            long before = GC.GetAllocatedBytesForCurrentThread();
            long start = Stopwatch.GetTimestamp();
            last = build(fonts);
            last.Save(stream);
            double ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            if (i >= warmup)
            {
                times.Add(ms);
                allocated.Add(bytes);
            }
        }

        double[] sorted = times.Order().ToArray();
        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"warm, {runs} runs after {warmup} warm-up: p50 {Percentile(sorted, 0.5):F1} ms, p95 {Percentile(sorted, 0.95):F1} ms, min {sorted[0]:F1}, max {sorted[^1]:F1}; allocated on this thread {allocated.Order().ElementAt(allocated.Count / 2) / 1048576.0:F1} MB per run (median); {stream.Length} bytes, {last.Diagnostics.PageCount} pages"));
        return 0;
    }

    internal static double Percentile(double[] sorted, double p)
    {
        // Nearest rank: the p95 of 30 runs is the 29th value, and no value is made up.
        int rank = (int)Math.Ceiling(p * sorted.Length);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Length - 1)];
    }
}
