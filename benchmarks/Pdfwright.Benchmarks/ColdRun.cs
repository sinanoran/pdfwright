using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Pdfwright.Benchmarks;

/// <summary>
/// The fresh-process harness: starts a command <c>runs</c> times, one after another, and reports the median and
/// the 95th percentile (nearest rank: with 30 runs the 29th value, nearly the maximum) of the wall time (from starting
/// the process to its exit) and the peak working set of the process it started. A fresh process is what shows the cost
/// a report pays the first time (JIT, native library load), which a warm BenchmarkDotNet run hides.
/// </summary>
/// <remarks>
/// The wall time is taken by <c>WaitForExit</c>; the peak is that of the started process only, sampled by this parent on
/// another thread every 5 ms (so the sleep granularity of Windows is not added to the time), so a command that starts
/// children (a browser) is undercounted: <c>Baselines/sample.ps1</c> sums a whole process tree on Windows.
/// When the command prints a JSON line with <c>peakWorkingSetMb</c> (the <c>once</c> and <c>start</c> verbs do), that
/// figure, which the process read from its own <c>PeakWorkingSet64</c> after the file was written, is used instead, and
/// its own <c>sinceProcessStartMs</c> is reported next to the wall time, together with the <c>bytes</c> and <c>pages</c>.
/// </remarks>
internal static class ColdRun
{
    internal static int Run(int runs, string[] command, TextWriter output)
    {
        List<double> wall = [];
        List<double> peakMegabytes = [];
        List<double> own = [];
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
            using CancellationTokenSource stop = new();
            Thread sampler = new(() =>
            {
                while (!stop.IsCancellationRequested)
                {
                    try
                    {
                        process.Refresh();
                        Volatile.Write(ref peak, Math.Max(Volatile.Read(ref peak), process.PeakWorkingSet64));
                    }
                    catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
                    {
                        return; // The process ended; its last reading stands.
                    }

                    stop.Token.WaitHandle.WaitOne(5);
                }
            })
            { IsBackground = true };
            sampler.Start();
            process.WaitForExit();
            clock.Stop();
            stop.Cancel();
            sampler.Join();
            string printed = stdout.Result;
            string errors = stderr.Result;
            if (process.ExitCode != 0)
            {
                output.WriteLine($"run {i + 1}: exit code {process.ExitCode}{(errors.Length > 0 ? Environment.NewLine + errors.TrimEnd() : string.Empty)}");
                return process.ExitCode;
            }

            wall.Add(clock.Elapsed.TotalMilliseconds);
            double peakMb = Volatile.Read(ref peak) / 1048576.0;
            if (TryReadReport(printed, out double ownPeakMb, out double sinceStart, out string reported))
            {
                peakMb = ownPeakMb;
                ownPeak = true;
                own.Add(sinceStart);
                shape = reported;
            }

            peakMegabytes.Add(peakMb);
        }

        double[] times = wall.Order().ToArray();
        double[] peaks = peakMegabytes.Order().ToArray();
        double[] inside = own.Order().ToArray();
        string insideText = inside.Length == 0
            ? string.Empty
            : string.Create(CultureInfo.InvariantCulture, $"; the process's own time since its start p50 {Percentile(inside, 0.5):F0} ms, p95 {Percentile(inside, 0.95):F0} ms");
        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{runs} fresh-process runs of '{string.Join(' ', command)}': wall p50 (median) {Percentile(times, 0.5):F0} ms, p95 {Percentile(times, 0.95):F0} ms (min {times[0]:F0}, max {times[^1]:F0}){insideText}; peak working set p50 {Percentile(peaks, 0.5):F0} MB, max {peaks[^1]:F0} MB ({(ownPeak ? "read by the process itself at the end" : "sampled by the parent")}){(string.IsNullOrEmpty(shape) ? string.Empty : "; " + shape)}"));
        return 0;
    }

    private static bool TryReadReport(string printed, out double peakMegabytes, out double sinceStartMs, out string shape)
    {
        peakMegabytes = 0;
        sinceStartMs = 0;
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
            if (json.RootElement.TryGetProperty("sinceProcessStartMs", out JsonElement since))
            {
                sinceStartMs = since.GetDouble();
            }

            if (json.RootElement.TryGetProperty("bytes", out JsonElement bytes) && json.RootElement.TryGetProperty("pages", out JsonElement pages))
            {
                shape = $"{bytes.GetInt64()} bytes, {pages.GetInt32()} pages";
            }

            return true;
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// The percentile of an ordered list. p = 0.5 is the true median (the mean of the two middle values when the count is even);
    /// any other p is the nearest rank, so with 30 runs the p95 is the 29th value and no value is made up.
    /// </summary>
    internal static double Percentile(double[] sorted, double p)
    {
        if (p == 0.5)
        {
            int middle = sorted.Length / 2;
            return sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
        }

        return sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1)];
    }
}
