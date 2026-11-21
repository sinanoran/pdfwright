using System.Diagnostics;
using Pdfwright.Pdf;

namespace Pdfwright.Tests.Pdf;

/// <summary>Documents the writer tests build, and the outside tools that look at them.</summary>
internal static class PdfTestKit
{
    internal static readonly DateTimeOffset Created = new(2026, 11, 21, 9, 30, 0, TimeSpan.Zero);

    /// <summary>
    /// Three pages of different sizes with text (a base-14 font, which needs no embedding), a URI link, a link to another
    /// page, a path, a clip and an opacity: every operator and structure the writer has.
    /// </summary>
    internal static byte[] ThreePages(PdfCompression compression, Stream? output = null)
    {
        MemoryStream memory = new();
        Stream target = output ?? memory;
        using (PdfDocumentWriter document = new(
            target,
            new PdfDocumentOptions
            {
                Compression = compression,
                Title = "Rapor İstanbul 2026",
                Subject = "Writer test",
                Author = "Pdfwright tests",
                Language = "tr",
                Created = Created,
            }))
        {
            PdfReference helvetica = document.AddObject(new PdfDictionary()
                .Set("Type", "Font")
                .Set("Subtype", "Type1")
                .Set("BaseFont", "Helvetica"));

            PdfPageWriter first = document.BeginPage(595, 842);
            string font = first.UseFont(helvetica);
            string half = first.UseOpacity(0.5);
            first.Content
                .SaveState()
                .FillRgb(0.9, 0.9, 0.2).Rectangle(40, 700.5, 200, 60).Fill()
                .RestoreState()
                .SaveState().ExtGState(half).StrokeRgb(0, 0, 1).LineWidth(2).Dash([3, 2], 0)
                .MoveTo(40, 680).LineTo(300, 680).CurveTo(320, 680, 330, 660, 300, 650).ClosePath().Stroke()
                .RestoreState()
                .SaveState().Rectangle(40, 500, 100, 100).Clip().FillGray(0.8).Rectangle(0, 0, 595, 842).Fill().RestoreState()
                .BeginText().Font(font, 14).TextMatrix(1, 0, 0, 1, 50, 720)
                .ShowText("Hello PDF, first page").EndText();
            first.AddUriLink(new PdfRectangle(50, 715, 200, 735), "https://example.com/report?a=1&b=2");
            first.AddPageLink(new PdfRectangle(50, 400, 200, 420), targetPage: 2, x: 0, y: 700);
            first.End();

            PdfPageWriter second = document.BeginPage(842, 595);
            string font2 = second.UseFont(helvetica);
            second.Content
                .BeginText().Font(font2, 12).TextMove(72, 500)
                .ShowText("Second page, landscape (with brackets and a backslash \\)").EndText();
            second.End();

            PdfPageWriter third = document.BeginPage(300, 300);
            string font3 = third.UseFont(helvetica);
            for (int i = 0; i < 40; i++)
            {
                third.Content.BeginText().Font(font3, 9).TextMove(10, 280 - (i * 6.5)).ShowText($"Line {i} of a repetitive page, repetitive, repetitive").EndText();
            }

            third.End();
            document.Complete();
        }

        return output is null ? memory.ToArray() : [];
    }

    /// <summary>A base-14 string for <c>Tj</c>, written as a literal: the tests use ASCII text only.</summary>
    internal static ContentStreamBuilder ShowText(this ContentStreamBuilder content, string text) =>
        content.ShowHex(System.Text.Encoding.ASCII.GetBytes(text));

    // ---- qpdf, the structure checker

    private static string? QpdfPath()
    {
        string? configured = Environment.GetEnvironmentVariable("PDFWRIGHT_QPDF");
        if (!string.IsNullOrEmpty(configured))
        {
            return configured;
        }

        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            foreach (string name in new[] { "qpdf", "qpdf.exe" })
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
    /// Runs <c>qpdf --check</c> on a file. A machine without qpdf skips the test, unless <c>PDFWRIGHT_REQUIRE_QPDF=1</c>
    /// (CI sets it), in which case a missing tool is a failure: a check that can be silently absent checks nothing.
    /// </summary>
    internal static void AssertQpdfAccepts(byte[] pdf, string name)
    {
        string? qpdf = QpdfPath();
        if (qpdf is null)
        {
            Assert.False(
                Environment.GetEnvironmentVariable("PDFWRIGHT_REQUIRE_QPDF") == "1",
                "qpdf is required here (PDFWRIGHT_REQUIRE_QPDF=1) and was not found on PATH or in PDFWRIGHT_QPDF");
            Assert.Skip("qpdf is not installed; set PDFWRIGHT_QPDF or put it on PATH");
            return;
        }

        string file = Path.Combine(Path.GetTempPath(), $"pdfwright-{Guid.NewGuid():N}-{name}.pdf");
        File.WriteAllBytes(file, pdf);
        try
        {
            using Process process = Process.Start(new ProcessStartInfo(qpdf, ["--check", file])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            }) ?? throw new InvalidOperationException("qpdf did not start");
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> errors = process.StandardError.ReadToEndAsync();
            process.WaitForExit();

            // Exit code 0 is a clean file; 3 is "warnings only", which for a file we wrote is also a defect.
            Assert.True(process.ExitCode == 0, $"qpdf --check exit code {process.ExitCode}:\n{output.Result}\n{errors.Result}");
        }
        finally
        {
            File.Delete(file);
        }
    }
}
