namespace Pdfwright.Drawing;

/// <summary>A place on the canvas: <see cref="X"/> from the left edge and <see cref="Y"/> down from the top.</summary>
internal readonly record struct Point(double X, double Y);

/// <summary>
/// An affine transform, in the form of a PDF's <c>cm</c>: a point (x, y) goes to (a x + c y + e, b x + d y + f). On the canvas (y down) a
/// positive rotation turns clockwise on the page.
/// </summary>
internal readonly record struct Matrix(double A, double B, double C, double D, double E, double F)
{
    internal static Matrix Identity { get; } = new(1, 0, 0, 1, 0, 0);

    internal static Matrix Translation(double dx, double dy) => new(1, 0, 0, 1, dx, dy);

    internal static Matrix Scaling(double sx, double sy) => new(sx, 0, 0, sy, 0, 0);

    /// <summary>A rotation about the origin by an angle in degrees; positive is clockwise as seen on the canvas, whose y axis points down.</summary>
    internal static Matrix Rotation(double degrees)
    {
        double turns = degrees % 360.0;
        double sin;
        double cos;

        // The right angles are exact, so that a quarter turn does not leave a 1e-16 in the file.
        switch (turns)
        {
            case 0:
                (sin, cos) = (0, 1);
                break;
            case 90 or -270:
                (sin, cos) = (1, 0);
                break;
            case 180 or -180:
                (sin, cos) = (0, -1);
                break;
            case 270 or -90:
                (sin, cos) = (-1, 0);
                break;
            default:
                double radians = turns * Math.PI / 180.0;
                (sin, cos) = (Math.Sin(radians), Math.Cos(radians));
                break;
        }

        return new Matrix(cos, sin, -sin, cos, 0, 0);
    }

    /// <summary>The transform that does this one first and then <paramref name="next"/>.</summary>
    internal Matrix Then(Matrix next) => new(
        (A * next.A) + (B * next.C),
        (A * next.B) + (B * next.D),
        (C * next.A) + (D * next.C),
        (C * next.B) + (D * next.D),
        (E * next.A) + (F * next.C) + next.E,
        (E * next.B) + (F * next.D) + next.F);

    internal Point Apply(Point point) => new((A * point.X) + (C * point.Y) + E, (B * point.X) + (D * point.Y) + F);

    internal bool IsFinite => double.IsFinite(A) && double.IsFinite(B) && double.IsFinite(C) && double.IsFinite(D) && double.IsFinite(E) && double.IsFinite(F);
}

