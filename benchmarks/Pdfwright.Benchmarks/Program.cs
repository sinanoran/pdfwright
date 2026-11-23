using System.Globalization;
using BenchmarkDotNet.Running;
using Pdfwright;
using Pdfwright.Benchmarks;
using Pdfwright.Benchmarks.Scenarios;

// pdfwright benchmarks:
//   cold --runs N -- <command...>        a fresh process N times: wall time and peak working set (median, p95)
//   once <table|report|report20|stress|table-arabic|table-devanagari|table10k> <output.pdf>     one fresh-process run of a scenario; prints one JSON line (use it under "cold")
//   warm <table|report|report20|stress|table-arabic|table-devanagari|table10k> [--runs N] [--warmup W]   the scenario N times in one process, after W unmeasured runs
//   start                                the runtime baseline: starts, prints its own time and peak working set, does nothing
//   bench [BenchmarkDotNet args]         warm time and allocations of the scenarios (BenchmarkDotNet)
string verb = args.Length > 0 ? args[0] : "help";
switch (verb)
{
    case "cold":
    {
        int runs = 10;
        int separator = Array.IndexOf(args, "--");
        // Only the arguments before the separator are ours; the command's own arguments may contain "--runs" too.
        int runsAt = separator < 0 ? -1 : Array.IndexOf(args, "--runs", 0, separator);
        if (runsAt >= 0 && (runsAt + 1 >= separator || !int.TryParse(args[runsAt + 1], NumberStyles.None, CultureInfo.InvariantCulture, out runs) || runs < 1))
        {
            Console.Error.WriteLine("--runs takes a whole number of at least 1");
            return 2;
        }

        if (separator < 0 || separator == args.Length - 1)
        {
            Console.Error.WriteLine("usage: cold --runs N -- <command> [arguments]");
            return 2;
        }

        return ColdRun.Run(runs, args[(separator + 1)..], Console.Out);
    }

    case "once":
    {
        Func<FontCollection, PdfDocument>? build = args.Length == 3 ? Warm.Find(args[1]) : null;
        if (build is null)
        {
            Console.Error.WriteLine("usage: once <table|report|report20|stress|table-arabic|table-devanagari|table10k> <output.pdf>");
            return 2;
        }

        return Warm.Once(build, args[2], Console.Out);
    }

    case "warm":
    {
        Func<FontCollection, PdfDocument>? build = args.Length >= 2 ? Warm.Find(args[1]) : null;
        if (build is null || !TryNumber(args, "--runs", 100, out int runs) || !TryNumber(args, "--warmup", 10, out int warmup))
        {
            Console.Error.WriteLine("usage: warm <table|report|report20|stress|table-arabic|table-devanagari|table10k> [--runs N] [--warmup W]");
            return 2;
        }

        return Warm.Loop(build, runs, warmup, Console.Out);
    }

    case "start":
    {
        // The runtime baseline: a process that starts, does nothing, and reports what "once" reports.
        using System.Diagnostics.Process self = System.Diagnostics.Process.GetCurrentProcess();
        double sinceStart = (DateTime.Now - self.StartTime).TotalMilliseconds;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{{\"sinceProcessStartMs\":{sinceStart:F1},\"peakWorkingSetMb\":{self.PeakWorkingSet64 / 1048576.0:F1}}}"));
        return 0;
    }

    case "bench":
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args[1..]);
        return 0;

    default:
        Console.WriteLine("verbs: cold --runs N -- <command...> | once <table|report|report20|stress|table-arabic|table-devanagari|table10k> <output.pdf> | warm <table|report|report20|stress|table-arabic|table-devanagari|table10k> [--runs N] [--warmup W] | start | bench [BenchmarkDotNet arguments]");
        return verb == "help" ? 0 : 2;
}

static bool TryNumber(string[] args, string name, int fallback, out int value)
{
    int at = Array.IndexOf(args, name);
    value = fallback;
    return at < 0 || (at + 1 < args.Length && int.TryParse(args[at + 1], NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= 1);
}
