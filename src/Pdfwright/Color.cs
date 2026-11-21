using System.Globalization;

namespace Pdfwright;

/// <summary>
/// A colour in the device RGB or the device gray space, with every component from 0 to 1 (what a PDF's <c>rg</c> and <c>g</c> operators take).
/// A gray colour is written with the shorter operator, and is not the same value as the RGB colour with three equal components.
/// </summary>
public readonly record struct Color
{
    private Color(double r, double g, double b, bool isGray)
    {
        R = r;
        G = g;
        B = b;
        IsGray = isGray;
    }

    /// <summary>Black, as a gray.</summary>
    public static Color Black { get; } = Gray(0);

    /// <summary>White, as a gray.</summary>
    public static Color White { get; } = Gray(1);

    /// <summary>Pure red (255, 0, 0).</summary>
    public static Color Red { get; } = FromRgb(255, 0, 0);

    /// <summary>Pure green (0, 128, 0), the colour CSS calls green.</summary>
    public static Color Green { get; } = FromRgb(0, 128, 0);

    /// <summary>Pure blue (0, 0, 255).</summary>
    public static Color Blue { get; } = FromRgb(0, 0, 255);

    /// <summary>A mid gray (0.5).</summary>
    public static Color MidGray { get; } = Gray(0.5);

    /// <summary>Red, from 0 to 1 (for a gray colour, the gray).</summary>
    public double R { get; }

    /// <summary>Green, from 0 to 1 (for a gray colour, the gray).</summary>
    public double G { get; }

    /// <summary>Blue, from 0 to 1 (for a gray colour, the gray).</summary>
    public double B { get; }

    /// <summary>The colour is in the gray space: it is written as one component.</summary>
    public bool IsGray { get; }

    /// <summary>A colour from components of 0 to 255.</summary>
    /// <param name="red">Red, 0 to 255.</param>
    /// <param name="green">Green, 0 to 255.</param>
    /// <param name="blue">Blue, 0 to 255.</param>
    /// <returns>The colour, in the RGB space.</returns>
    public static Color FromRgb(byte red, byte green, byte blue) => new(red / 255.0, green / 255.0, blue / 255.0, isGray: false);

    /// <summary>A colour from components of 0 to 1.</summary>
    /// <param name="red">Red, 0 to 1.</param>
    /// <param name="green">Green, 0 to 1.</param>
    /// <param name="blue">Blue, 0 to 1.</param>
    /// <returns>The colour, in the RGB space.</returns>
    /// <exception cref="ArgumentException">A component is not a number from 0 to 1.</exception>
    public static Color FromRgb(double red, double green, double blue) =>
        new(Component(red, nameof(red)), Component(green, nameof(green)), Component(blue, nameof(blue)), isGray: false);

    /// <summary>A gray from 0 (black) to 1 (white).</summary>
    /// <param name="gray">The gray, 0 to 1.</param>
    /// <returns>The colour, in the gray space.</returns>
    /// <exception cref="ArgumentException">The gray is not a number from 0 to 1.</exception>
    public static Color Gray(double gray)
    {
        double value = Component(gray, nameof(gray));
        return new Color(value, value, value, isGray: true);
    }

    /// <summary>A colour from a hexadecimal string: <c>#RGB</c>, <c>#RRGGBB</c>, with or without the number sign.</summary>
    /// <param name="hex">The colour as three or six hexadecimal digits.</param>
    /// <returns>The colour, in the RGB space.</returns>
    /// <exception cref="ArgumentException">The text is not three or six hexadecimal digits.</exception>
    public static Color FromHex(string hex)
    {
        ArgumentNullException.ThrowIfNull(hex);
        string digits = hex.StartsWith('#') ? hex[1..] : hex;
        if (digits.Length is not (3 or 6) || !digits.All(Uri.IsHexDigit))
        {
            throw new ArgumentException($"A colour is written #RGB or #RRGGBB, not '{hex}'.", nameof(hex));
        }

        if (digits.Length == 3)
        {
            digits = string.Concat(digits.Select(c => new string(c, 2)));
        }

        return FromRgb(
            byte.Parse(digits.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(digits.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(digits.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }

    private static double Component(double value, string name) =>
        value is >= 0 and <= 1 ? value : throw new ArgumentException($"A colour component is a number from 0 to 1, not {value}.", name);
}
