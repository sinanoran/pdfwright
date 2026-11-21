using HarfBuzzSharp;

namespace Pdfwright.Tests;

/// <summary>Where the fonts and the corpus are: beside the test assembly (the project copies them there).</summary>
internal static class TestPaths
{
    internal static string FontsDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "Fonts");

    internal static string CorpusDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "Corpus");

    /// <summary>The repository root: the nearest folder above the test assembly that holds Pdfwright.slnx (the Unicode data lives there, not beside the assembly).</summary>
    internal static string RepositoryRoot { get; } = FindRoot();

    private static string FindRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Pdfwright.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Pdfwright.slnx was not found above " + AppContext.BaseDirectory);
    }

    internal static string Font(string fileName) => Path.Combine(FontsDirectory, fileName);

    /// <summary>
    /// A HarfBuzz blob of bytes, loaded through a temporary file: <c>Blob.FromStream</c> over a <c>MemoryStream</c> crashed
    /// intermittently on .NET 9, and <c>Blob.FromFile</c> is the path that does not.
    /// </summary>
    internal static Blob BlobOf(byte[] bytes)
    {
        string path = Path.Combine(Path.GetTempPath(), "pdfwright-" + Guid.NewGuid().ToString("N") + ".ttf");
        File.WriteAllBytes(path, bytes);
        try
        {
            return Blob.FromFile(path)!;
        }
        finally
        {
            File.Delete(path);
        }
    }
}
