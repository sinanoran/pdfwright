using System.Runtime.InteropServices;

namespace Pdfwright.Text;

/// <summary>
/// The OpenType layout and table entry points of HarfBuzz that HarfBuzzSharp has no managed binding for and that the check for word-by-word shaping (<see cref="HarfBuzzSpaceCheck"/>) needs.
/// They come from the same native library as <see cref="HarfBuzzSubsetNative"/> (<see cref="HarfBuzzSubsetNative.Library"/>). A library that cannot be loaded or lacks an export makes the
/// calls throw <see cref="DllNotFoundException"/>, <see cref="EntryPointNotFoundException"/> or <see cref="BadImageFormatException"/>, which the check turns into the answer "no".
/// </summary>
internal static class HarfBuzzLayoutNative
{
    private const string Library = HarfBuzzSubsetNative.Library;

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern uint hb_ot_layout_table_get_lookup_count(IntPtr face, uint tableTag);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void hb_ot_layout_lookup_collect_glyphs(IntPtr face, uint tableTag, uint lookupIndex, IntPtr glyphsBefore, IntPtr glyphsInput, IntPtr glyphsAfter, IntPtr glyphsOutput);

    /// <summary>The class of a glyph by GDEF: 0 unclassified, 1 base glyph, 2 ligature, 3 mark, 4 component.</summary>
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int hb_ot_layout_get_glyph_class(IntPtr face, uint glyph);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr hb_set_create();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void hb_set_destroy(IntPtr set);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int hb_set_has(IntPtr set, uint codepoint);

    /// <summary>Whether every allocation the set needed succeeded; <c>hb_set_create</c> itself returns an immutable empty set, not NULL, when it cannot allocate.</summary>
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int hb_set_allocation_successful(IntPtr set);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr hb_face_reference_table(IntPtr face, uint tag);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern uint hb_blob_get_length(IntPtr blob);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void hb_blob_destroy(IntPtr blob);
}
