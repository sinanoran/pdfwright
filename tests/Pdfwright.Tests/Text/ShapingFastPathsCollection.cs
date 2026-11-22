namespace Pdfwright.Tests.Text;

/// <summary>
/// The tests that flip <see cref="Pdfwright.Text.TextShaper.FastPaths"/>, a static switch every shaping call reads. They run one after the other and never beside the tests of another collection, which would otherwise shape
/// with the switch off for a moment and, in a test of the shaper's caches, change what is cached.
/// </summary>
[CollectionDefinition("ShapingFastPaths", DisableParallelization = true)]
public sealed class ShapingFastPathsCollection
{
}
