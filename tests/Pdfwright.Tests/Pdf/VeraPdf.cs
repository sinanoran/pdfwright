using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Pdfwright.Tests.Pdf;

/// <summary>
/// veraPDF, the PDF/UA-1 validator, run on a batch of files in one process (the Java machine starts once). It is found through
/// <c>PDFWRIGHT_VERAPDF</c> (a path; a <c>.cmd</c> or <c>.bat</c> is run through <c>cmd</c>) or as <c>verapdf</c> on the path. A machine without it
/// skips the test, unless <c>PDFWRIGHT_REQUIRE_VERAPDF=1</c> (the Linux leg of CI sets it), in which case a missing tool is a failure, as for qpdf.
/// </summary>
internal static class VeraPdf
{
    /// <summary>A rule of the profile a file failed: its clause of ISO 14289-1 and test number, what it asks, how many checks failed and the first message.</summary>
    internal sealed record Rule(string Clause, int Test, string Description, int Failed, string? Message)
    {
        /// <summary>The rule's name as veraPDF's profile has it, such as <c>7.21.8-1</c>.</summary>
        internal string Id => $"{Clause}-{Test}";

        public override string ToString() => $"{Id} ({Failed} failed): {Description}" + (Message is null ? string.Empty : $"\n      e.g. {Message}");
    }

    /// <summary>What veraPDF said of one file: whether it is compliant with PDF/UA-1, and the rules it failed.</summary>
    internal sealed record Result(string Name, bool Compliant, IReadOnlyList<Rule> Failures)
    {
        /// <summary>The ids of the failed rules, in order.</summary>
        internal IEnumerable<string> FailedIds => Failures.Select(f => f.Id);

        public override string ToString() =>
            Compliant ? $"{Name}: compliant" : $"{Name}: not compliant, {Failures.Count} rule(s) failed\n    " + string.Join("\n    ", Failures);
    }

    /// <summary>The rule a file without the PDF/UA identification in its metadata fails (ISO 14289-1, 5).</summary>
    internal const string ClaimRule = "5-1";

    private static string? ToolPath()
    {
        string? configured = Environment.GetEnvironmentVariable("PDFWRIGHT_VERAPDF");
        if (!string.IsNullOrEmpty(configured))
        {
            return configured;
        }

        string[] names = OperatingSystem.IsWindows() ? ["verapdf.bat", "verapdf.cmd", "verapdf.exe"] : ["verapdf"];
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            foreach (string name in names)
            {
                string candidate = Path.Combine(directory, name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The tool, or a skip (a failure with <c>PDFWRIGHT_REQUIRE_VERAPDF=1</c>) when there is none. Call it before building the files, so a skipped test costs nothing.
    /// With <c>PDFWRIGHT_VERAPDF_RUNTIME</c> set to a major version of .NET (CI sets 10), the validation runs on that runtime only and the other target
    /// frameworks skip it: the files it checks are the same on every runtime but for compression, and each run starts Java. Without it, every runtime validates.
    /// </summary>
    internal static string Require()
    {
        if (Environment.GetEnvironmentVariable("PDFWRIGHT_VERAPDF_RUNTIME") is { Length: > 0 } runtime && runtime != Environment.Version.Major.ToString(System.Globalization.CultureInfo.InvariantCulture))
        {
            Assert.Skip($"veraPDF validates on .NET {runtime} only (PDFWRIGHT_VERAPDF_RUNTIME); this is .NET {Environment.Version.Major}");
        }

        string? tool = ToolPath();
        if (tool is null)
        {
            Assert.False(
                Environment.GetEnvironmentVariable("PDFWRIGHT_REQUIRE_VERAPDF") == "1",
                "veraPDF is required here (PDFWRIGHT_REQUIRE_VERAPDF=1) and was not found on PATH or in PDFWRIGHT_VERAPDF");
            Assert.Skip("veraPDF is not installed; set PDFWRIGHT_VERAPDF or put verapdf on PATH");
        }

        return tool!;
    }

    /// <summary>
    /// Validates the files against PDF/UA-1 (<c>--flavour ua1</c>) in one veraPDF process and returns each file's result by name. The names are file
    /// names, so they are plain ASCII without a path; the files are written to a directory of their own, which is deleted afterwards.
    /// </summary>
    internal static IReadOnlyDictionary<string, Result> Validate(IEnumerable<(string Name, byte[] Pdf)> files)
    {
        string tool = Require();
        string directory = Path.Combine(Path.GetTempPath(), $"pdfwright-verapdf-{Guid.NewGuid():N}");
        _ = Directory.CreateDirectory(directory);
        try
        {
            List<string> names = [];
            foreach ((string name, byte[] pdf) in files)
            {
                if (name.Any(c => c is not ((>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-' or '_' or '.')))
                {
                    throw new ArgumentException($"'{name}' is not a plain file name.", nameof(files));
                }

                File.WriteAllBytes(Path.Combine(directory, name + ".pdf"), pdf);
                names.Add(name);
            }

            Dictionary<string, Result> results = Parse(Run(tool, directory));
            List<string> missing = [.. names.Where(n => !results.ContainsKey(n))];
            Assert.True(missing.Count == 0, $"veraPDF reported nothing for {string.Join(", ", missing)}");
            return results;
        }
        finally
        {
            // A directory that cannot be deleted (a file still held open after a failure) is left in the temporary folder rather than hiding what failed.
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// Keeps a file that failed where it can be looked at: <c>PDFWRIGHT_ACTUAL_DIR</c> (CI keeps its <c>pdfwright-actual-*.pdf</c> as an artifact) or the
    /// temporary folder. Returns the path.
    /// </summary>
    internal static string Keep(string name, byte[] pdf)
    {
        string folder = Environment.GetEnvironmentVariable("PDFWRIGHT_ACTUAL_DIR") is { Length: > 0 } custom ? custom : Path.GetTempPath();
        _ = Directory.CreateDirectory(folder);
        string written = Path.Combine(folder, $"pdfwright-actual-verapdf-{name}.pdf");
        File.WriteAllBytes(written, pdf);
        return written;
    }

    /// <summary>How long one veraPDF run may take: the largest batch takes about a minute; a run that hangs fails the test instead of the whole job.</summary>
    private static readonly TimeSpan RunLimit = TimeSpan.FromMinutes(5);

    private static string Run(string tool, string directory)
    {
        // The profile's own limit of 100 failed checks a rule is kept; one message is enough for each rule here.
        string arguments = $"--flavour ua1 --format json --maxfailuresdisplayed 1 \"{directory}\"";
        bool script = OperatingSystem.IsWindows() && (tool.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || tool.EndsWith(".bat", StringComparison.OrdinalIgnoreCase));

        // cmd takes the line after /c whole with /s: the outer quotes go and the inner ones (a path with spaces) stay.
        ProcessStartInfo start = script
            ? new ProcessStartInfo("cmd.exe", $"/d /s /c \"\"{tool}\" {arguments}\"")
            : new ProcessStartInfo(tool, arguments);
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.UseShellExecute = false;
        start.StandardOutputEncoding = Encoding.UTF8;
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("veraPDF did not start");
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> errors = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(RunLimit))
        {
            // The Java machine is a child of cmd on Windows: the whole tree goes.
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            Assert.Fail($"veraPDF did not finish in {RunLimit.TotalMinutes} minutes and was stopped:\n{errors.Result}");
        }

        process.WaitForExit(); // the redirected streams are read to their end

        // The exit code is 1 when a file is not compliant, so it says nothing here: the report does, and a run that wrote none failed. A launcher may
        // print a line before the report (a JVM notice), so the report starts at the first line that opens an object.
        string text = output.Result;
        int report = text.StartsWith('{') ? 0 : text.IndexOf("\n{", StringComparison.Ordinal) + 1;
        Assert.True(report >= 0 && report < text.Length && text[report] == '{', $"veraPDF wrote no report (exit code {process.ExitCode}):\n{text}\n{errors.Result}");
        return text[report..];
    }

    /// <summary>Reads veraPDF's JSON report: <c>report.jobs[]</c>, each with the file's name and a validation result of rule summaries.</summary>
    internal static Dictionary<string, Result> Parse(string json)
    {
        using JsonDocument report = JsonDocument.Parse(json);
        Dictionary<string, Result> results = [];
        foreach (JsonElement job in report.RootElement.GetProperty("report").GetProperty("jobs").EnumerateArray())
        {
            string path = job.GetProperty("itemDetails").GetProperty("name").GetString()!;
            string name = Path.GetFileNameWithoutExtension(path.Replace('\\', '/').Split('/')[^1]);
            if (!job.TryGetProperty("validationResult", out JsonElement validations) || validations.ValueKind != JsonValueKind.Array || validations.GetArrayLength() == 0)
            {
                // A file veraPDF could not parse or validate has a task exception instead of a result.
                results[name] = new Result(name, false, [new Rule("job", 0, "veraPDF could not validate the file: " + job.GetRawText(), 1, null)]);
                continue;
            }

            JsonElement validation = validations[0];
            List<Rule> failures = [];
            if (validation.TryGetProperty("details", out JsonElement details) && details.TryGetProperty("ruleSummaries", out JsonElement rules))
            {
                foreach (JsonElement rule in rules.EnumerateArray())
                {
                    string? message = rule.TryGetProperty("checks", out JsonElement checks) && checks.GetArrayLength() > 0 && checks[0].TryGetProperty("errorMessage", out JsonElement m)
                        ? m.GetString()
                        : null;
                    failures.Add(new Rule(
                        rule.GetProperty("clause").GetString()!,
                        rule.GetProperty("testNumber").GetInt32(),
                        rule.GetProperty("description").GetString()!,
                        rule.GetProperty("failedChecks").GetInt32(),
                        message));
                }
            }

            results[name] = new Result(name, validation.GetProperty("compliant").GetBoolean(), failures);
        }

        return results;
    }
}
