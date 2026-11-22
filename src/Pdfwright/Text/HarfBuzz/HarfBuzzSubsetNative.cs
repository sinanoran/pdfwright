using System.Runtime.InteropServices;


namespace Pdfwright.Text;

/// <summary>
/// The <c>hb_subset_*</c> entry points of HarfBuzz, declared here because HarfBuzzSharp has no managed binding for them. They are taken from the native library
/// HarfBuzzSharp itself loads (<see cref="Library"/>, the name of its own <c>DllImport</c>: <c>libHarfBuzzSharp.dll</c> on Windows, <c>libHarfBuzzSharp.so</c> on Linux,
/// <c>libHarfBuzzSharp.dylib</c> on macOS), so the same native file serves shaping and subsetting on every platform and no second library is shipped.
/// <see cref="EnsureAvailable"/> checks every export before the first call, so a build of the library that lacks one fails with a message and not with a crash.
/// </summary>
internal static class HarfBuzzSubsetNative
{
    /// <summary>The library name HarfBuzzSharp declares for its own calls; the runtime adds the platform's prefix and suffix.</summary>
    internal const string Library = "libHarfBuzzSharp";

    /// <summary>The exports a subset needs.</summary>
    internal static readonly string[] RequiredExports =
    [
        "hb_subset_input_create_or_fail",
        "hb_subset_input_destroy",
        "hb_subset_input_glyph_set",
        "hb_subset_input_set_flags",
        "hb_set_add",
        "hb_subset_plan_create_or_fail",
        "hb_subset_plan_old_to_new_glyph_mapping",
        "hb_subset_plan_execute_or_fail",
        "hb_subset_plan_destroy",
        "hb_subset_or_fail",
        "hb_map_get",
        "hb_face_reference_blob",
        "hb_face_destroy",
        "hb_blob_get_data",
        "hb_blob_destroy",
    ];

    /// <summary>The export that pins a variable font at its default instance; the subsetter needs it only when a caller asks for that.</summary>
    internal const string PinAllAxesExport = "hb_subset_input_pin_all_axes_to_default";

    private static readonly Lazy<(IntPtr Handle, string? LoadError)> Loaded = new(Load);

    /// <summary>The exports (of <see cref="RequiredExports"/> and <see cref="PinAllAxesExport"/>) that the loaded library does not have; empty when all are there. Throws if the library cannot be loaded.</summary>
    internal static IReadOnlyList<string> MissingExports()
    {
        (IntPtr handle, string? error) = Loaded.Value;
        if (handle == IntPtr.Zero)
        {
            throw Unavailable(error ?? "it could not be loaded", null);
        }

        List<string> missing = [];
        foreach (string name in RequiredExports.Append(PinAllAxesExport))
        {
            if (!NativeLibrary.TryGetExport(handle, name, out _))
            {
                missing.Add(name);
            }
        }

        return missing;
    }

    /// <summary>Throws <see cref="PlatformNotSupportedException"/> naming the platform and the exports when a required export (and, with <paramref name="needPin"/>, the pin export) is missing.</summary>
    internal static void EnsureAvailable(bool needPin = false)
    {
        IReadOnlyList<string> missing = MissingExports();
        if (!needPin)
        {
            missing = [.. missing.Where(m => m != PinAllAxesExport)];
        }

        if (missing.Count > 0)
        {
            throw Unavailable("it does not export " + string.Join(", ", missing), null);
        }
    }

    /// <summary>The message for a library that cannot do <c>hb-subset</c>: it names the library, the platform and the reason.</summary>
    internal static PlatformNotSupportedException Unavailable(string reason, Exception? inner) =>
        new($"Font subsetting through hb-subset is not available on this platform ({Platform()}): the native HarfBuzz library '{Library}' is not usable for it, because {reason}.", inner);

    internal static string Platform() => $"{RuntimeInformation.RuntimeIdentifier}, {RuntimeInformation.OSDescription.Trim()}, {RuntimeInformation.ProcessArchitecture}";

    private static (IntPtr, string?) Load()
    {
        try
        {
            return NativeLibrary.TryLoad(Library, typeof(HarfBuzzSubsetNative).Assembly, null, out IntPtr handle) ? (handle, null) : (IntPtr.Zero, "it could not be loaded");
        }
        catch (Exception e) when (e is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        {
            return (IntPtr.Zero, "it could not be loaded (" + e.Message + ")");
        }
    }

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr hb_subset_input_create_or_fail();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void hb_subset_input_destroy(IntPtr input);

    /// <summary>The set of glyph ids to keep; owned by the input.</summary>
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr hb_subset_input_glyph_set(IntPtr input);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void hb_subset_input_set_flags(IntPtr input, uint flags);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int hb_subset_input_pin_all_axes_to_default(IntPtr input, IntPtr face);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void hb_set_add(IntPtr set, uint codepoint);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr hb_subset_plan_create_or_fail(IntPtr face, IntPtr input);

    /// <summary>The old-to-new glyph id map; owned by the plan.</summary>
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr hb_subset_plan_old_to_new_glyph_mapping(IntPtr plan);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr hb_subset_plan_execute_or_fail(IntPtr plan);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void hb_subset_plan_destroy(IntPtr plan);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr hb_subset_or_fail(IntPtr face, IntPtr input);

    /// <summary>The value for a key, or <see cref="MapValueInvalid"/> when the key is not in the map.</summary>
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern uint hb_map_get(IntPtr map, uint key);

    internal const uint MapValueInvalid = 0xFFFFFFFF;

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr hb_face_reference_blob(IntPtr face);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void hb_face_destroy(IntPtr face);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr hb_blob_get_data(IntPtr blob, out uint length);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void hb_blob_destroy(IntPtr blob);
}
