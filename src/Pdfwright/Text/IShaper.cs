namespace Pdfwright.Text;

/// <summary>
/// The shaping seam: what the text layer needs from a shaping engine, in Pdfwright's own types. HarfBuzz (<see cref="HarfBuzzShaper"/>) is the only
/// implementation; nothing above it, and no type of this file, names a HarfBuzz type. Choosing a font for a character is not the shaper's job
/// (the font model answers glyph-for-code-point), and neither are bidi, scripts and line breaking.
/// </summary>
internal interface IShaper
{
    /// <summary>Makes the shaper's handle for a font: the bytes of an sfnt font and its design units per em (positions are returned in these units). The handle owns native resources and is disposed by its owner.</summary>
    ShaperFont CreateFont(byte[] fontBytes, int unitsPerEm);

    /// <summary>
    /// Shapes <c>text[start .. start + length]</c> as one run, with the text before and after it as context (for joining letters and the like). The glyphs come in drawing order
    /// (left to right), so for a right-to-left run the reverse of the logical order; each glyph's cluster is the UTF-16 index in <paramref name="text"/> of the characters it stands for.
    /// A right-to-left run gives every mark a cluster of its own where the engine can. No language is set when <paramref name="language"/> is <see langword="null"/>. <paramref name="font"/> is a handle made by this shaper's <see cref="CreateFont"/>; <paramref name="scriptTag"/> is the four-letter ISO 15924 code of the run's script (<c>Latn</c>, <c>Arab</c>).
    /// </summary>
    ShapedGlyph[] Shape(ShaperFont font, string text, int start, int length, bool rightToLeft, string scriptTag, string? language);
}

/// <summary>A font as a shaper holds it: opaque to everything outside the shaper that made it.</summary>
internal abstract class ShaperFont : IDisposable
{
    public abstract void Dispose();
}
