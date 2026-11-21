using Pdfwright.Tests.Document;
using System.Diagnostics;

namespace Pdfwright.Tests.Pdf;

/// <summary>
/// A smoke test of <c>tools/Pdfwright.GoldenDiff</c>, which a person runs when a golden test fails: the tool is built with the tests (a project reference without
/// the assembly) and run as a process, so that a break in it shows here and not on the day a golden file changes.
/// </summary>
public class GoldenDiffTests
{
    private static string ToolPath()
    {
        // .../tests/Pdfwright.Tests/Golden -> the repository root, and the tool's output folder of the configuration this test was built in.
        string root = Path.GetFullPath(Path.Combine(Golden.Folder, "..", "..", ".."));
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        string path = Path.Combine(root, "tools", "Pdfwright.GoldenDiff", "bin", configuration, "net10.0", "Pdfwright.GoldenDiff.dll");
        Assert.True(File.Exists(path), $"the diff tool was not built: {path}");
        return path;
    }

    private static (int ExitCode, string Output, string Error) Run(params string[] arguments)
    {
        ProcessStartInfo start = new("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(ToolPath());
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(60_000), "the diff tool did not finish in a minute");
        return (process.ExitCode, output.Result, error.Result);
    }

    private static byte[] Document(string text)
    {
        PdfDocument document = new(DocumentTestKit.Options(o => o.Compress = false));
        document.Add(new Paragraph(text));
        return DocumentTestKit.Save(document);
    }

    [Fact]
    public void The_diff_tool_exits_0_for_identical_files_1_naming_a_changed_object_and_2_naming_a_missing_file()
    {
        string folder = Path.Combine(Path.GetTempPath(), "pdfwright-golddiff-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(folder);
        try
        {
            string a = Path.Combine(folder, "a.pdf");
            string b = Path.Combine(folder, "b.pdf");
            string same = Path.Combine(folder, "same.pdf");
            File.WriteAllBytes(a, Document("A line of text."));
            File.WriteAllBytes(same, Document("A line of text."));
            File.WriteAllBytes(b, Document("A line of test."));

            (int identicalCode, string identical, _) = Run(a, same);
            (int changedCode, string changed, _) = Run(a, b);
            (int missingCode, _, string missing) = Run(a, Path.Combine(folder, "absent.pdf"));

            Assert.Equal(0, identicalCode);
            Assert.Contains("identical", identical, StringComparison.Ordinal);
            Assert.Equal(1, changedCode);
            Assert.Matches(@"object \d+: changed", changed);
            Assert.Equal(2, missingCode);
            Assert.Contains("absent.pdf", missing, StringComparison.Ordinal);
            Assert.Contains("actual", missing, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
