namespace Pdfwright;

/// <summary>
/// What the package knows about itself. The public API is lengths, page sizes, colours, the font collection, text styles, the blocks (heading, paragraph, spacer, page break, table,
/// columns, callout, drawing, image), the document and its options and limits, the drawing canvas, the text measurer and the diagnostics. Everything under the folders <c>Pdf</c>,
/// <c>Text</c>, <c>Fonts</c>, <c>Drawing</c>, <c>Images</c> and <c>Unicode</c> stays internal. The public surface is recorded in
/// <c>tests/Pdfwright.Tests/PublicApi/Pdfwright.PublicApi.txt</c>, and a test fails when it changes without that file changing too.
/// </summary>
internal static class LibraryInfo
{
    /// <summary>The name the package is published under.</summary>
    internal const string PackageName = "Pdfwright";
}
