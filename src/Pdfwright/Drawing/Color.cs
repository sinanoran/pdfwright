namespace Pdfwright.Drawing;

/// <summary>
/// A colour in the device RGB or the device gray space, with every component from 0 to 1 (what a PDF's <c>rg</c> and <c>g</c> operators take).
/// A gray colour is written with the shorter operator, and is not the same value as the RGB colour with three equal components.
/// </summary>
internal readonly record struct Color
{
    private Color(double r, double g, double b, bool isGray)
    {
        R = r;
        G = g;
        B = b;
        IsGray = isGray;
    }

    internal static Color Black { get; } = Gray(0);

    internal static Color White { get; } = Gray(1);

    /// <summary>Red, from 0 to 1 (for a gray colour, the gray).</summary>
    internal double R { get; }

    /// <summary>Green, from 0 to 1 (for a gray colour, the gray).</summary>
    internal double G { get; }

    /// <summary>Blue, from 0 to 1 (for a gray colour, the gray).</summary>
    internal double B { get; }

    /// <summary>The colour is in the gray space: it is written as one component.</summary>
    internal bool IsGray { get; }

    /// <summary>A colour from components of 0 to 255.</summary>
    internal static Color FromRgb(byte red, byte green, byte blue) => new(red / 255.0, green / 255.0, blue / 255.0, isGray: false);

    /// <summary>A colour from components of 0 to 1.</summary>
    /// <exception cref="ArgumentException">A component is not a number from 0 to 1.</exception>
    internal static Color FromRgb(double red, double green, double blue) =>
        new(Component(red, nameof(red)), Component(green, nameof(green)), Component(blue, nameof(blue)), isGray: false);

    /// <summary>A gray from 0 (black) to 1 (white).</summary>
    /// <exception cref="ArgumentException">The gray is not a number from 0 to 1.</exception>
    internal static Color Gray(double gray)
    {
        double value = Component(gray, nameof(gray));
        return new Color(value, value, value, isGray: true);
    }

    private static double Component(double value, string name) =>
        value is >= 0 and <= 1 ? value : throw new ArgumentException($"A colour component is a number from 0 to 1, not {value}.", name);
}
