using HarfBuzzSharp;

namespace Pdfwright.Text;

/// <summary>
/// Decides whether a font may be shaped word by word. Splitting a text at its spaces and shaping the pieces gives exactly the glyphs of shaping the whole text only when no rule of the font looks
/// at the space glyph, which is what Chromium relies on for its word cache. The check reads every lookup of GSUB and GPOS (<c>hb_ot_layout_lookup_collect_glyphs</c>: the glyphs a lookup may match before,
/// in and after its input, and may output) and refuses the font when the space glyph is in any of the four sets; it also refuses a font that has no space glyph, and a font with a table that HarfBuzz
/// may apply outside GSUB and GPOS (<c>kern</c>, <c>kerx</c>, <c>morx</c>, <c>mort</c>, <c>trak</c>), which is the careful choice. A lookup that skips base glyphs could still match across a space that is in none
/// of its sets, which the check cannot see; the shaper answers that by taking only words whose glyphs are all base glyphs (<see cref="HarfBuzzFont.IsBaseGlyph"/>).
/// </summary>
internal static class HarfBuzzSpaceCheck
{
    private static readonly string[] OutsideTables = ["kern", "kerx", "morx", "mort", "trak"];

    /// <summary>
    /// <see langword="null"/> when the font passes, else the reason it does not, in words (the table, the lookup and the set that holds the space glyph). A library that cannot be used for it, or any error, is a refusal.
    /// </summary>
    internal static string? Refusal(Face face, HarfBuzzSharp.Font font)
    {
        try
        {
            return Check(face, font);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return "the native HarfBuzz library does not offer the functions for the check (" + e.GetType().Name + ")";
        }
    }

    private static string? Check(Face face, HarfBuzzSharp.Font font)
    {
        if (!font.TryGetGlyph(0x20, out uint space))
        {
            return "the font has no glyph for U+0020";
        }

        foreach (string table in OutsideTables)
        {
            IntPtr blob = HarfBuzzLayoutNative.hb_face_reference_table(face.Handle, Tag(table));
            try
            {
                if (blob != IntPtr.Zero && HarfBuzzLayoutNative.hb_blob_get_length(blob) > 0)
                {
                    return "the font has a '" + table + "' table";
                }
            }
            finally
            {
                if (blob != IntPtr.Zero)
                {
                    HarfBuzzLayoutNative.hb_blob_destroy(blob);
                }
            }
        }

        string[] setNames = ["before", "input", "after", "output"];
        IntPtr[] sets = new IntPtr[4];
        try
        {
            for (int i = 0; i < sets.Length; i++)
            {
                sets[i] = HarfBuzzLayoutNative.hb_set_create();
                if (sets[i] == IntPtr.Zero)
                {
                    return "a glyph set could not be made";
                }
            }

            foreach (string table in new[] { "GSUB", "GPOS" })
            {
                uint tag = Tag(table);
                uint count = HarfBuzzLayoutNative.hb_ot_layout_table_get_lookup_count(face.Handle, tag);
                for (uint lookup = 0; lookup < count; lookup++)
                {
                    HarfBuzzLayoutNative.hb_ot_layout_lookup_collect_glyphs(face.Handle, tag, lookup, sets[0], sets[1], sets[2], sets[3]);
                    for (int i = 0; i < sets.Length; i++)
                    {
                        if (HarfBuzzLayoutNative.hb_set_has(sets[i], space) != 0)
                        {
                            return $"the space glyph {space} is in the {setNames[i]} set of {table} lookup {lookup}";
                        }
                    }
                }
            }
        }
        finally
        {
            foreach (IntPtr set in sets)
            {
                if (set != IntPtr.Zero)
                {
                    HarfBuzzLayoutNative.hb_set_destroy(set);
                }
            }
        }

        return null;
    }

    /// <summary>A four-letter table or feature tag as HarfBuzz's <c>hb_tag_t</c>.</summary>
    private static uint Tag(string tag) => ((uint)tag[0] << 24) | ((uint)tag[1] << 16) | ((uint)tag[2] << 8) | tag[3];
}
