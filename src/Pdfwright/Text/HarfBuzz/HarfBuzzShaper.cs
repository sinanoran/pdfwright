using System.Runtime.InteropServices;
using HarfBuzzSharp;

namespace Pdfwright.Text;

/// <summary>The one <see cref="IShaper"/>: HarfBuzz through HarfBuzzSharp. Only the files of the HarfBuzz folder name HarfBuzz types.</summary>
internal sealed class HarfBuzzShaper : IShaper
{
    internal static HarfBuzzShaper Instance { get; } = new();

    [ThreadStatic]
    private static HarfBuzzSharp.Buffer? t_buffer;

    /// <summary>The longest run (in UTF-16 units) that is shaped in the buffer of the thread; a longer one gets a buffer of its own that is freed at once, so no thread keeps native memory in proportion to the longest text it ever shaped.</summary>
    internal const int ReusedBufferLimit = 4096;

    /// <summary>The length of the buffer this thread keeps, or -1 when it has none. For tests.</summary>
    internal static int ThreadBufferLength => t_buffer is { } b ? b.Length : -1;
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

        // One buffer for each thread for the usual short run: a buffer is a native object that costs more to make than to reset. A long run uses a buffer that is disposed after it.
        bool reused = length <= ReusedBufferLimit;
        HarfBuzzSharp.Buffer buffer = reused ? (t_buffer ??= new HarfBuzzSharp.Buffer()) : new HarfBuzzSharp.Buffer();
        try
        {
            return ShapeIn(buffer, hb, text, start, length, rightToLeft, scriptTag, language);
        }
        finally
        {
            if (!reused)
            {
                buffer.Dispose();
            }
            else if (buffer.Length > ReusedBufferLimit)
            {
                // The glyphs of a short run outgrew the limit (a run of marks that expands): do not keep the memory.
                buffer.Dispose();
                t_buffer = null;
            }
        }
    }

    private static ShapedGlyph[] ShapeIn(HarfBuzzSharp.Buffer buffer, HarfBuzzFont hb, string text, int start, int length, bool rightToLeft, string scriptTag, string? language)
    {
        buffer.ClearContents();
        buffer.ClusterLevel = ClusterLevel.Default;

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
        ReadOnlySpan<GlyphInfo> infos = buffer.GetGlyphInfoSpan();
        ReadOnlySpan<GlyphPosition> positions = buffer.GetGlyphPositionSpan();
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

/// <summary>A font for HarfBuzz: the blob, face and font over one copy of the font's bytes in native memory. After <see cref="Dispose"/> (which may be called again) every use throws <see cref="ObjectDisposedException"/>.</summary>
internal sealed class HarfBuzzFont : ShaperFont
{
    private readonly Blob _blob;
    private readonly Face _face;
    private readonly HarfBuzzSharp.Font _font;
    private volatile bool _disposed;
    private readonly Lazy<string?> _wordShapingRefusal;

    internal HarfBuzzFont(byte[] bytes, int unitsPerEm)
    {
        // The bytes are copied to native memory that HarfBuzz owns until it releases the blob: a managed buffer it did not pin could move.
        IntPtr native = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, native, bytes.Length);
        _blob = new Blob(native, bytes.Length, MemoryMode.ReadOnly, () => Marshal.FreeHGlobal(native));
        _face = new Face(_blob, 0);
        _font = new HarfBuzzSharp.Font(_face);
        _font.SetScale(unitsPerEm, unitsPerEm);
        _wordShapingRefusal = new Lazy<string?>(() => HarfBuzzSpaceCheck.Refusal(Face, Font), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>The verdict of <see cref="HarfBuzzSpaceCheck"/>, computed on the first call and kept.</summary>
    public override string? WordShapingRefusal => _wordShapingRefusal.Value;

    /// <summary>A base glyph is one that GDEF calls a base glyph or does not classify. A library without the function answers no, so no word is taken.</summary>
    public override bool IsBaseGlyph(int glyph)
    {
        try
        {
            int glyphClass = HarfBuzzLayoutNative.hb_ot_layout_get_glyph_class(Face.Handle, (uint)glyph);
            return glyphClass is 0 or 1;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return false;
        }
    }

    internal Face Face
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _face;
        }
    }

    internal HarfBuzzSharp.Font Font
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _font;
        }
    }

    public override void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _font.Dispose();
        _face.Dispose();
        _blob.Dispose();
    }
}
