using System.Runtime.InteropServices;
using HarfBuzzSharp;

namespace Pdfwright.Text;

/// <summary>The one <see cref="IShaper"/>: HarfBuzz through HarfBuzzSharp. Only the files of the HarfBuzz folder name HarfBuzz types.</summary>
internal sealed class HarfBuzzShaper : IShaper
{
    internal static HarfBuzzShaper Instance { get; } = new();

    public ShaperFont CreateFont(byte[] fontBytes, int unitsPerEm)
    {
        ArgumentNullException.ThrowIfNull(fontBytes);
        return new HarfBuzzFont(fontBytes, unitsPerEm);
    }

    public ShapedGlyph[] Shape(ShaperFont font, string text, int start, int length, bool rightToLeft, string scriptTag, string? language)
    {
        if (font is not HarfBuzzFont hb)
        {
            throw new ArgumentException("The font was not made by the HarfBuzz shaper.", nameof(font));
        }

        using HarfBuzzSharp.Buffer buffer = new();

        // The item offset and length make HarfBuzz see the text before and after the run as context (a joining letter, a tatweel).
        buffer.AddUtf16(text, start, length);
        buffer.Direction = rightToLeft ? Direction.RightToLeft : Direction.LeftToRight;
        buffer.Script = HarfBuzzSharp.Script.Parse(scriptTag);
        if (rightToLeft)
        {
            // Cluster level 1 gives every mark of a right-to-left run a cluster, and so a text, of its own, which the writer needs to keep marks apart for pdf.js.
            // Left-to-right runs stay at the default level: level 1 there made xpdf misread five Indic lines.
            buffer.ClusterLevel = ClusterLevel.MonotoneCharacters;
        }

        if (language is not null)
        {
            buffer.Language = new HarfBuzzSharp.Language(language);
        }

        hb.Font.Shape(buffer);
        GlyphInfo[] infos = buffer.GlyphInfos;
        GlyphPosition[] positions = buffer.GlyphPositions;
        ShapedGlyph[] glyphs = new ShapedGlyph[infos.Length];
        for (int i = 0; i < infos.Length; i++)
        {
            // A .notdef glyph stays on the baseline: when a mark has no glyph (a Hebrew point after a Latin digit, the font of the digit does not have it), HarfBuzz's
            // fallback positioning of marks puts the .notdef box below the base by its own height, which on the last line of a page is outside it.
            int yOffset = infos[i].Codepoint == 0 ? 0 : positions[i].YOffset;
            glyphs[i] = new ShapedGlyph((int)infos[i].Codepoint, (int)infos[i].Cluster, positions[i].XAdvance, positions[i].XOffset, yOffset);
        }

        return glyphs;
    }
}

/// <summary>A font for HarfBuzz: the blob, face and font over one copy of the font's bytes in native memory.</summary>
internal sealed class HarfBuzzFont : ShaperFont
{
    private readonly Blob _blob;

    internal HarfBuzzFont(byte[] bytes, int unitsPerEm)
    {
        // The bytes are copied to native memory that HarfBuzz owns until it releases the blob: a managed buffer it did not pin could move.
        IntPtr native = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, native, bytes.Length);
        _blob = new Blob(native, bytes.Length, MemoryMode.ReadOnly, () => Marshal.FreeHGlobal(native));
        Face = new Face(_blob, 0);
        Font = new HarfBuzzSharp.Font(Face);
        Font.SetScale(unitsPerEm, unitsPerEm);
    }

    internal Face Face { get; }

    internal HarfBuzzSharp.Font Font { get; }

    public override void Dispose()
    {
        Font.Dispose();
        Face.Dispose();
        _blob.Dispose();
    }
}
