using Pdfwright.Samples;
using Pdfwright.Tests;
using Pdfwright.Tests.Document;
using UglyToad.PdfPig.Content;

namespace Pdfwright.DocumentationTests;

/// <summary>Builds the report of <c>samples/Pdfwright.Samples.Report</c> (its source file is compiled into this project) and reads it back, so the sample is known to work on every target framework.</summary>
public class ReportSampleTests
{
    private static byte[] Logo() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Images", "Files", "gradient-24x16.jpg"));

    [Fact]
    public async Task The_sample_report_has_every_part_and_its_text_reads_back()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using FontCollection fonts = ReportSample.LoadFonts(TestPaths.FontsDirectory);
        PdfDocument document = ReportSample.Build(fonts, Logo());

        using MemoryStream stream = new();
        await document.SaveAsync(stream, cancellationToken);

        byte[] pdf = stream.ToArray();
        using UglyToad.PdfPig.PdfDocument read = UglyToad.PdfPig.PdfDocument.Open(pdf);
        string[] pages = [.. read.GetPages().Select((Page p) => p.Text.Replace("͏", string.Empty, StringComparison.Ordinal))];
        Assert.True(pages.Length >= 3, "The report runs over several pages.");
        Assert.Empty(document.Diagnostics.MissingGlyphs);
        Assert.Empty(document.Diagnostics.MissingFamilies);
        Assert.True(document.Diagnostics.IsPdfUA, string.Join("; ", document.Diagnostics.AccessibilityIssues));
        Assert.Empty(document.Diagnostics.AccessibilityIssues);
        Assert.Contains("Monthly report", pages[0]);
        Assert.Contains("Revenue", pages[0]);
        Assert.Contains("Oct", pages[0]);
        Assert.Contains($"Page 1 of {pages.Length}", pages[0]);
        Assert.Contains($"Page {pages.Length} of {pages.Length}", pages[^1]);
        Assert.Contains("Customer 90", string.Concat(pages));
        Assert.Contains("Customer", pages[1]); // the header of the table is repeated
        Assert.Contains("ملخص بالعربية", string.Concat(pages));
        Assert.Contains("INV-2026-0042", string.Concat(pages));
        Assert.Equal("Monthly report", read.Information.Title);

        // Last, so that a machine without qpdf skips the test after its assertions have run.
        DocumentTestKit.AssertValid(pdf, "report-sample");
    }

    [Fact]
    public async Task The_sample_report_is_the_same_bytes_every_time()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using FontCollection fonts = ReportSample.LoadFonts(TestPaths.FontsDirectory);
        PdfDocument document = ReportSample.Build(fonts, Logo());
        using MemoryStream first = new();
        using MemoryStream second = new();

        await document.SaveAsync(first, cancellationToken);
        await ReportSample.Build(fonts, Logo()).SaveAsync(second, cancellationToken);

        Assert.Equal(first.ToArray(), second.ToArray());
    }
}
