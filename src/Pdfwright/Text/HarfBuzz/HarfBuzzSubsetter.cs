using System.Runtime.InteropServices;
using HarfBuzzSharp;
using Microsoft.Win32.SafeHandles;

namespace Pdfwright.Text;

/// <summary>The flags of <c>hb_subset_input_set_flags</c> (<c>hb_subset_flags_t</c>) that a caller may want.</summary>
[Flags]
internal enum HarfBuzzSubsetFlags : uint
{
    None = 0,
    NoHinting = 0x1,

    /// <summary>Keep the glyph ids as they are (glyphs not kept become empty): the subset then has as many glyphs as the original up to the highest id kept.</summary>
    RetainGlyphIds = 0x2,
    Desubroutinize = 0x4,
    NameLegacy = 0x8,
    NotdefOutline = 0x40,
}

/// <summary>A subset font: its bytes (an sfnt, as the original's outline format) and the new glyph id of each old glyph id that was kept (the glyphs asked for and what they need, such as the parts of a composite glyph).</summary>
internal sealed record HarfBuzzSubset(byte[] Font, IReadOnlyDictionary<int, int> OldToNew);

/// <summary>
/// Subsets a font with <c>hb-subset</c> (the subsetter of HarfBuzz, which handles TrueType, CFF and CFF2 outlines), through the declarations of
/// <see cref="HarfBuzzSubsetNative"/>. Every native handle is owned by a <see cref="SafeHandle"/> and released on every path. Not used by the writer yet.
/// </summary>
internal static class HarfBuzzSubsetter
{
    /// <summary>Subsets the font held by <paramref name="font"/> to <paramref name="glyphs"/>.</summary>
    internal static HarfBuzzSubset Subset(HarfBuzzFont font, IReadOnlyCollection<int> glyphs, HarfBuzzSubsetFlags flags = HarfBuzzSubsetFlags.None, bool pinAxesToDefault = false) =>
        Subset(font.Face, glyphs, flags, pinAxesToDefault);

    /// <summary>Subsets the font of the sfnt <paramref name="fontBytes"/> to <paramref name="glyphs"/>.</summary>
    internal static HarfBuzzSubset Subset(byte[] fontBytes, IReadOnlyCollection<int> glyphs, HarfBuzzSubsetFlags flags = HarfBuzzSubsetFlags.None, bool pinAxesToDefault = false)
    {
        using HarfBuzzFont font = new(fontBytes, 1000);
        return Subset(font.Face, glyphs, flags, pinAxesToDefault);
    }

    /// <summary>
    /// Subsets <paramref name="face"/> to <paramref name="glyphs"/> (old glyph ids; glyph 0, .notdef, is always kept by <c>hb-subset</c>) and returns the font and the old-to-new map.
    /// New ids are given in the order of the old ones unless <see cref="HarfBuzzSubsetFlags.RetainGlyphIds"/> keeps them. When the native library lacks an export,
    /// <see cref="PlatformNotSupportedException"/> says which and on what platform; a font that <c>hb-subset</c> refuses gives <see cref="InvalidDataException"/>.
    /// </summary>
    internal static HarfBuzzSubset Subset(Face face, IReadOnlyCollection<int> glyphs, HarfBuzzSubsetFlags flags = HarfBuzzSubsetFlags.None, bool pinAxesToDefault = false)
    {
        ArgumentNullException.ThrowIfNull(face);
        ArgumentNullException.ThrowIfNull(glyphs);
        int glyphCount = face.GlyphCount;
        foreach (int glyph in glyphs)
        {
            if (glyph < 0 || glyph >= glyphCount)
            {
                throw new ArgumentOutOfRangeException(nameof(glyphs), $"Glyph {glyph} is outside the font's {glyphCount} glyphs.");
            }
        }

        HarfBuzzSubsetNative.EnsureAvailable(pinAxesToDefault);
        try
        {
            return Run(face, glyphs, flags, pinAxesToDefault, glyphCount);
        }
        catch (EntryPointNotFoundException e)
        {
            throw HarfBuzzSubsetNative.Unavailable("an export was not found (" + e.Message + ")", e);
        }
    }

    private static HarfBuzzSubset Run(Face face, IReadOnlyCollection<int> glyphs, HarfBuzzSubsetFlags flags, bool pinAxesToDefault, int glyphCount)
    {
        using NativeHandle input = new(HarfBuzzSubsetNative.hb_subset_input_create_or_fail(), HarfBuzzSubsetNative.hb_subset_input_destroy);
        if (input.IsInvalid)
        {
            throw new InvalidOperationException("hb-subset could not create its input (out of memory).");
        }

        IntPtr set = HarfBuzzSubsetNative.hb_subset_input_glyph_set(input.DangerousGetHandle()); // owned by the input
        foreach (int glyph in glyphs)
        {
            HarfBuzzSubsetNative.hb_set_add(set, (uint)glyph);
        }

        HarfBuzzSubsetNative.hb_subset_input_set_flags(input.DangerousGetHandle(), (uint)flags);
        if (pinAxesToDefault && HarfBuzzSubsetNative.hb_subset_input_pin_all_axes_to_default(input.DangerousGetHandle(), face.Handle) == 0)
        {
            throw new InvalidDataException("hb-subset could not pin the font's variation axes to their defaults.");
        }

        using NativeHandle plan = new(HarfBuzzSubsetNative.hb_subset_plan_create_or_fail(face.Handle, input.DangerousGetHandle()), HarfBuzzSubsetNative.hb_subset_plan_destroy);
        if (plan.IsInvalid)
        {
            throw new InvalidDataException("hb-subset refused the font: it could not plan the subset.");
        }

        using NativeHandle result = new(HarfBuzzSubsetNative.hb_subset_plan_execute_or_fail(plan.DangerousGetHandle()), HarfBuzzSubsetNative.hb_face_destroy);
        if (result.IsInvalid)
        {
            throw new InvalidDataException("hb-subset refused the font: it could not make the subset.");
        }

        byte[] bytes = BytesOf(result.DangerousGetHandle());

        // The map belongs to the plan, which is still alive here. Every old glyph is asked: the glyphs that were kept include those the glyphs asked for need.
        IntPtr map = HarfBuzzSubsetNative.hb_subset_plan_old_to_new_glyph_mapping(plan.DangerousGetHandle());
        Dictionary<int, int> oldToNew = [];
        for (int old = 0; old < glyphCount; old++)
        {
            uint mapped = HarfBuzzSubsetNative.hb_map_get(map, (uint)old);
            if (mapped != HarfBuzzSubsetNative.MapValueInvalid)
            {
                oldToNew[old] = (int)mapped;
            }
        }

        return new HarfBuzzSubset(bytes, oldToNew);
    }

    private static byte[] BytesOf(IntPtr face)
    {
        IntPtr blob = HarfBuzzSubsetNative.hb_face_reference_blob(face);
        try
        {
            IntPtr data = HarfBuzzSubsetNative.hb_blob_get_data(blob, out uint length);
            if (data == IntPtr.Zero || length == 0)
            {
                throw new InvalidDataException("hb-subset made an empty font.");
            }

            byte[] bytes = new byte[length];
            Marshal.Copy(data, bytes, 0, (int)length);
            return bytes;
        }
        finally
        {
            HarfBuzzSubsetNative.hb_blob_destroy(blob);
        }
    }

    /// <summary>A native pointer released by one of the library's destroy functions.</summary>
    private sealed class NativeHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private readonly Action<IntPtr> _destroy;

        internal NativeHandle(IntPtr handle, Action<IntPtr> destroy)
            : base(true)
        {
            _destroy = destroy;
            SetHandle(handle);
        }

        protected override bool ReleaseHandle()
        {
            _destroy(handle);
            return true;
        }
    }
}
