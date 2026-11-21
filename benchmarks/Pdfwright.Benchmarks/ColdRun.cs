using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Pdfwright.Benchmarks;

/// <summary>
/// The fresh-process harness: starts a command <c>runs</c> times, one after another, and reports the median and
/// the 95th percentile of the wall time (from starting the process to its exit) and the peak working set of the
/// process it started. A fresh process is what shows the cost a report pays the first time (JIT, native library
/// load), which a warm BenchmarkDotNet run hides.
/// </summary>
/// <remarks>
/// The peak is that of the started process only, sampled by this parent every 5 ms, so a command that starts
/// children (a browser) is undercounted: <c>Baselines/sample.ps1</c> sums a whole process tree on Windows.
/// When the command prints a JSON line with <c>peakWorkingSetMb</c> (the <c>once</c> verb does), that figure, which
/// the process read from its own <c>PeakWorkingSet64</c> after the file was written, is used instead, together with the
/// <c>bytes</c> and <c>pages</c> it reports.
/// </remarks>
internal static class ColdRun
{
    internal static int Run(int runs, string[] command, TextWriter output)
    {
        List<double> wall = [];
        List<double> peakMegabytes = [];
        string? shape = null;
        bool ownPeak = false;
        for (int i = 0; i < runs; i++)
        {
            ProcessStartInfo start = new(command[0], command.Skip(1).ToArray())
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            Stopwatch clock = Stopwatch.StartNew();
            using Process process = Process.Start(start) ?? throw new InvalidOperationException($"could not start {command[0]}");
            long peak = 0;
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            while (!process.HasExited)
            {
                try
                {
                    process.Refresh();
                    peak = Math.Max(peak, process.PeakWorkingSet64);
                }
                catch (InvalidOperationException)
                {
                    // The process ended between HasExited and Refresh; its last reading stands.
                }

                Thread.Sleep(5);
            }

            process.WaitForExit();
            clock.Stop();
            string printed = stdout.Result;
            string errors = stderr.Result;
            if (process.ExitCode != 0)
            {
                output.WriteLine($"run {i + 1}: exit code {process.ExitCode}{(errors.Length > 0 ? Environment.NewLine + errors.TrimEnd() : string.Empty)}");
                return process.ExitCode;
            }

            wall.Add(clock.Elapsed.TotalMilliseconds);
            double peakMb = peak / 1048576.0;
            if (TryReadReport(printed, out double ownPeakMb, out string reported))
            {
                peakMb = ownPeakMb;
                ownPeak = true;
                shape = reported;
            }

            peakMegabytes.Add(peakMb);
        }

        double[] times = wall.Order().ToArray();
        double[] peaks = peakMegabytes.Order().ToArray();
        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{runs} fresh-process runs of '{string.Join(' ', command)}': wall p50 {Percentile(times, 0.5):F0} ms, p95 {Percentile(times, 0.95):F0} ms (min {times[0]:F0}, max {times[^1]:F0}); peak working set p50 {Percentile(peaks, 0.5):F0} MB, max {peaks[^1]:F0} MB ({(ownPeak ? "read by the process itself at the end" : "sampled by the parent")}){(shape is null ? string.Empty : "; " + shape)}"));
        return 0;
    }

    private static bool TryReadReport(string printed, out double peakMegabytes, out string shape)
    {
        peakMegabytes = 0;
        shape = string.Empty;
        string? line = printed.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault(l => l.StartsWith('{'));
        if (line is null)
        {
            return false;
        }

        try
        {
            using JsonDocument json = JsonDocument.Parse(line);
            if (!json.RootElement.TryGetProperty("peakWorkingSetMb", out JsonElement peak))
            {
                return false;
            }

            peakMegabytes = peak.GetDouble();
            shape = $"{json.RootElement.GetProperty("bytes").GetInt64()} bytes, {json.RootElement.GetProperty("pages").GetInt32()} pages";
            return true;
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return false;
        }
    }

    /// <summary>The nearest-rank percentile of an ordered list: with 30 runs the p95 is the 29th value, and no value is made up.</summary>
    private static double Percentile(double[] sorted, double p) =>
        sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1)];
}
