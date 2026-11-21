using System.Diagnostics;
using System.Globalization;

namespace Pdfwright.Benchmarks;

/// <summary>
/// The fresh-process harness: starts a command <c>runs</c> times, one after another, and reports the median
/// wall time and the median peak working set of the process it started. A fresh process is what shows the cost
/// a report pays the first time (JIT, native library load), which a warm BenchmarkDotNet run hides.
/// </summary>
/// <remarks>
/// The peak is that of the started process only. A command that starts children (a browser) is undercounted:
/// <c>Baselines/sample.ps1</c> sums a whole process tree on Windows.
/// </remarks>
internal static class ColdRun
{
    internal static int Run(int runs, string[] command, TextWriter output)
    {
        List<double> wall = [];
        List<double> peakMegabytes = [];
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
            _ = stdout.Result;
            string errors = stderr.Result;
            if (process.ExitCode != 0)
            {
                output.WriteLine($"run {i + 1}: exit code {process.ExitCode}{(errors.Length > 0 ? Environment.NewLine + errors.TrimEnd() : string.Empty)}");
                return process.ExitCode;
            }

            wall.Add(clock.Elapsed.TotalMilliseconds);
            peakMegabytes.Add(peak / 1048576.0);
        }

        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{runs} fresh-process runs of '{string.Join(' ', command)}': median {Median(wall):F0} ms, median peak working set {Median(peakMegabytes):F0} MB (min {wall.Min():F0} ms, max {wall.Max():F0} ms)"));
        return 0;
    }

    private static double Median(List<double> values)
    {
        double[] sorted = values.Order().ToArray();
        int middle = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }
}
