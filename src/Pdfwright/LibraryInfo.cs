namespace Pdfwright;

/// <summary>
/// What the package knows about itself. The public API started with slice S5a of <c>docs/plan-0.1.md</c>: lengths, page sizes, colours, the font
/// collection, text styles, the simple blocks (heading, paragraph, spacer, page break), <see cref="PdfDocument"/>, the text measurer and the
/// diagnostics. Everything under the folders <c>Pdf</c>, <c>Text</c>, <c>Fonts</c>, <c>Drawing</c>, <c>Images</c> and <c>Unicode</c> stays internal; table,
/// columns, callout, the drawing block, keep-together and header and footer are the next slices (S5b, S5c). The public surface is recorded in
/// <c>tests/Pdfwright.Tests/PublicApi/Pdfwright.PublicApi.txt</c>, and a test fails when it changes without that file changing too.
/// </summary>
internal static class LibraryInfo
{
    /// <summary>The name the package is published under.</summary>
    internal const string PackageName = "Pdfwright";
}
