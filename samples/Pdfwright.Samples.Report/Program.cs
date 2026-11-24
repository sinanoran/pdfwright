using Pdfwright;
using Pdfwright.Samples;

// Writes the sample report to a file: Pdfwright.Samples.Report [output.pdf]
string output = args.Length > 0 ? args[0] : "report.pdf";
string directory = AppContext.BaseDirectory;

using FontCollection fonts = ReportSample.LoadFonts(Path.Combine(directory, "fonts"));
PdfDocument document = ReportSample.Build(fonts, await File.ReadAllBytesAsync(Path.Combine(directory, "logo.jpg")));

await using (FileStream file = File.Create(output))
{
    await document.SaveAsync(file);
}

DocumentDiagnostics report = document.Diagnostics;
await Console.Out.WriteLineAsync($"{output}: {report.PageCount} pages, {report.ByteCount} bytes, {(report.IsPdfUA ? "tagged, PDF/UA-1" : "not PDF/UA-1")}.");
foreach (MissingGlyph glyph in report.MissingGlyphs)
{
    await Console.Error.WriteLineAsync($"No font has {glyph.Character} (U+{glyph.CodePoint:X4}), page {glyph.Page}.");
}

foreach (AccessibilityIssue issue in report.AccessibilityIssues)
{
    await Console.Error.WriteLineAsync($"Accessibility: {issue}");
}

return report.MissingGlyphs.Count == 0 && report.IsPdfUA ? 0 : 1;
