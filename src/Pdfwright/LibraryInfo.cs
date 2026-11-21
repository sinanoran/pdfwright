namespace Pdfwright;

/// <summary>
/// What the repository's skeleton needs from the library before the writer exists (slice S0 of
/// <c>docs/plan-0.1.md</c>). There is deliberately no public type yet: the public API is the part
/// that is hard to change after release, and it is designed in section 5 of the plan, not by
/// accident in the first commit.
/// </summary>
internal static class LibraryInfo
{
    /// <summary>The name the package is published under.</summary>
    internal const string PackageName = "Pdfwright";
}
