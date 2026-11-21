using System.Diagnostics;
using System.Text;
using Pdfwright.Text;

namespace Pdfwright.Tests.Text;

/// <summary>
/// A regression floor, not an acceptance test: xpdf's <c>pdftotext</c> in its default (position-ordered) mode reads some of the corpus lines
/// wrongly for reasons of its own (see <c>docs/known-limitations.md</c>), and a change to how glyphs are written must not make it worse.
/// It runs where <c>pdftotext</c> is on the path (or named by <c>PDFWRIGHT_PDFTOTEXT</c>) and is skipped elsewhere, so CI, which does not install it, skips it.
/// </summary>
public class XpdfRegressionFloorTests
{
    /// <summary>The number of corpus lines xpdf reads exactly (after the normalisations below) when this floor was set.</summary>
    private const int Floor = 45;

    private static string? FindPdftotext()
    {
        string? named = Environment.GetEnvironmentVariable("PDFWRIGHT_PDFTOTEXT");
        if (named is { Length: > 0 } && File.Exists(named))
        {
            return named;
        }

        string executable = OperatingSystem.IsWindows() ? "pdftotext.exe" : "pdftotext";
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(directory, executable);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>What is compared: the embedding controls xpdf adds to right-to-left text are removed, no-break spaces and runs of white space become one space.</summary>
    private static string Normalise(string text)
    {
        StringBuilder kept = new();
        foreach (char c in text.Normalize(NormalizationForm.FormC))
        {
            if (c is >= '‪' and <= '‮' or '‎' or '‏' or '͏')
            {
                continue;
            }

            kept.Append(c is ' ' or ' ' or ' ' ? ' ' : c);
        }

        return string.Join(' ', kept.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public void Xpdf_reads_at_least_the_floor_of_the_corpus_lines_exactly()
    {
        string? pdftotext = FindPdftotext();
        if (pdftotext is null)
        {
            Assert.Skip("pdftotext (xpdf) is not installed; set PDFWRIGHT_PDFTOTEXT or put it on PATH");
            return;
        }

        string directory = Path.Combine(Path.GetTempPath(), "pdfwright-xpdf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            int exact = 0;
            List<CorpusTests.Case> cases = CorpusTests.Load();
            foreach (CorpusTests.Case c in cases)
            {
                string path = Path.Combine(directory, c.Id + ".pdf");
                File.WriteAllBytes(path, MixedDirectionExtractionTests.Draw(c.Text, c.Fonts, out _, out _));
                ProcessStartInfo start = new(pdftotext) { RedirectStandardOutput = true, UseShellExecute = false, StandardOutputEncoding = Encoding.UTF8 };
                foreach (string argument in new[] { "-enc", "UTF-8", "-nopgbrk", path, "-" })
                {
                    start.ArgumentList.Add(argument);
                }

                using Process process = Process.Start(start)!;
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();
                if (Normalise(output) == Normalise(c.Text))
                {
                    exact++;
                }
            }

            Assert.True(exact >= Floor, $"xpdf reads {exact} of {cases.Count} lines exactly; the floor is {Floor}");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
