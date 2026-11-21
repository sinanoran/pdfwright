using BenchmarkDotNet.Running;
using Pdfwright.Benchmarks;

// pdfwright benchmarks:
//   cold --runs N -- <command...>   a fresh process N times: median wall time and peak working set
//   bench [BenchmarkDotNet args]    warm time and allocations of the scenarios (none yet: S1 adds the first)
string verb = args.Length > 0 ? args[0] : "help";
switch (verb)
{
    case "cold":
    {
        int runs = 10;
        int separator = Array.IndexOf(args, "--");
        // Only the arguments before the separator are ours; the command's own arguments may contain "--runs" too.
        int runsAt = separator < 0 ? -1 : Array.IndexOf(args, "--runs", 0, separator);
        if (runsAt >= 0 && (runsAt + 1 >= separator || !int.TryParse(args[runsAt + 1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out runs) || runs < 1))
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

    case "bench":
        if (!typeof(Program).Assembly.GetTypes().Any(t => t.GetMethods().Any(m => m.GetCustomAttributes(typeof(BenchmarkDotNet.Attributes.BenchmarkAttribute), false).Length > 0)))
        {
            Console.Error.WriteLine("no benchmark scenarios yet: the first is added with the writer (slice S1)");
            return 3;
        }

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args[1..]);
        return 0;

    default:
        Console.WriteLine("verbs: cold --runs N -- <command...> | bench [BenchmarkDotNet arguments]");
        return verb == "help" ? 0 : 2;
}
