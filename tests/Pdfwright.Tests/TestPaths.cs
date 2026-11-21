namespace Pdfwright.Tests;

/// <summary>Where the fonts and the corpus are: beside the test assembly (the project copies them there).</summary>
internal static class TestPaths
{
    internal static string FontsDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "Fonts");

    internal static string CorpusDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "Corpus");

    internal static string Font(string fileName) => Path.Combine(FontsDirectory, fileName);
}
