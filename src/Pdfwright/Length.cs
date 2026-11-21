using System.Globalization;

namespace Pdfwright;

/// <summary>
/// A distance with its unit built in, so that a number is never taken for points when it was meant as millimetres. A length is stored in points
/// (1/72 inch, the PDF unit). There is no implicit conversion from a number: write <c>Length.Pt(12)</c>, <c>Length.Mm(20)</c>, <c>Length.Cm(2)</c> or <c>Length.In(1)</c>.
/// </summary>
public readonly struct Length : IEquatable<Length>, IComparable<Length>
{
    private const double PointsPerInch = 72.0;

    private Length(double points)
    {
        Points = points;
    }

    /// <summary>A length of zero.</summary>
    public static Length Zero => default;

    /// <summary>The length in points (1/72 inch).</summary>
    public double Points { get; }

    /// <summary>The length in millimetres.</summary>
    public double Millimeters => Points * 25.4 / PointsPerInch;

    /// <summary>The length in centimetres.</summary>
    public double Centimeters => Points * 2.54 / PointsPerInch;

    /// <summary>The length in inches.</summary>
    public double Inches => Points / PointsPerInch;

    /// <summary>Whether the length is a finite number (not NaN and not infinity).</summary>
    public bool IsFinite => double.IsFinite(Points);

    /// <summary>A length in points.</summary>
    /// <param name="value">The number of points.</param>
    /// <returns>The length.</returns>
    public static Length Pt(double value) => new(value);

    /// <summary>A length in millimetres.</summary>
    /// <param name="value">The number of millimetres.</param>
    /// <returns>The length.</returns>
    public static Length Mm(double value) => new(value * PointsPerInch / 25.4);

    /// <summary>A length in centimetres.</summary>
    /// <param name="value">The number of centimetres.</param>
    /// <returns>The length.</returns>
    public static Length Cm(double value) => new(value * PointsPerInch / 2.54);

    /// <summary>A length in inches.</summary>
    /// <param name="value">The number of inches.</param>
    /// <returns>The length.</returns>
    public static Length In(double value) => new(value * PointsPerInch);

    /// <summary>The sum of two lengths.</summary>
    /// <param name="left">The first length.</param>
    /// <param name="right">The second length.</param>
    /// <returns>The sum.</returns>
    public static Length operator +(Length left, Length right) => new(left.Points + right.Points);

    /// <summary>The difference of two lengths.</summary>
    /// <param name="left">The length to subtract from.</param>
    /// <param name="right">The length to subtract.</param>
    /// <returns>The difference.</returns>
    public static Length operator -(Length left, Length right) => new(left.Points - right.Points);

    /// <summary>The negation of a length.</summary>
    /// <param name="value">The length.</param>
    /// <returns>The same distance the other way.</returns>
    public static Length operator -(Length value) => new(-value.Points);

    /// <summary>A length times a number.</summary>
    /// <param name="length">The length.</param>
    /// <param name="factor">The factor.</param>
    /// <returns>The scaled length.</returns>
    public static Length operator *(Length length, double factor) => new(length.Points * factor);

    /// <summary>A number times a length.</summary>
    /// <param name="factor">The factor.</param>
    /// <param name="length">The length.</param>
    /// <returns>The scaled length.</returns>
    public static Length operator *(double factor, Length length) => new(length.Points * factor);

    /// <summary>A length divided by a number.</summary>
    /// <param name="length">The length.</param>
    /// <param name="divisor">The divisor.</param>
    /// <returns>The scaled length.</returns>
    public static Length operator /(Length length, double divisor) => new(length.Points / divisor);

    /// <summary>The ratio of two lengths.</summary>
    /// <param name="left">The dividend.</param>
    /// <param name="right">The divisor.</param>
    /// <returns>How many times <paramref name="right"/> fits in <paramref name="left"/>.</returns>
    public static double operator /(Length left, Length right) => left.Points / right.Points;

    /// <summary>Whether the first length is shorter than the second.</summary>
    /// <param name="left">The first length.</param>
    /// <param name="right">The second length.</param>
    /// <returns><see langword="true"/> when it is.</returns>
    public static bool operator <(Length left, Length right) => left.Points < right.Points;

    /// <summary>Whether the first length is longer than the second.</summary>
    /// <param name="left">The first length.</param>
    /// <param name="right">The second length.</param>
    /// <returns><see langword="true"/> when it is.</returns>
    public static bool operator >(Length left, Length right) => left.Points > right.Points;

    /// <summary>Whether the first length is shorter than or equal to the second.</summary>
    /// <param name="left">The first length.</param>
    /// <param name="right">The second length.</param>
    /// <returns><see langword="true"/> when it is.</returns>
    public static bool operator <=(Length left, Length right) => left.Points <= right.Points;

    /// <summary>Whether the first length is longer than or equal to the second.</summary>
    /// <param name="left">The first length.</param>
    /// <param name="right">The second length.</param>
    /// <returns><see langword="true"/> when it is.</returns>
    public static bool operator >=(Length left, Length right) => left.Points >= right.Points;

    /// <summary>Whether two lengths are the same number of points.</summary>
    /// <param name="left">The first length.</param>
    /// <param name="right">The second length.</param>
    /// <returns><see langword="true"/> when they are.</returns>
    public static bool operator ==(Length left, Length right) => left.Points == right.Points;

    /// <summary>Whether two lengths differ.</summary>
    /// <param name="left">The first length.</param>
    /// <param name="right">The second length.</param>
    /// <returns><see langword="true"/> when they do.</returns>
    public static bool operator !=(Length left, Length right) => left.Points != right.Points;

    /// <inheritdoc />
    public bool Equals(Length other) => Points.Equals(other.Points);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is Length other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => Points.GetHashCode();

    /// <inheritdoc />
    public int CompareTo(Length other) => Points.CompareTo(other.Points);

    /// <summary>The length written in points, such as <c>12.5pt</c>, with a period as the decimal separator whatever the culture.</summary>
    /// <returns>The text.</returns>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Points:0.####}pt");
}
