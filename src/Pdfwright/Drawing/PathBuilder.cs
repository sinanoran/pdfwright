namespace Pdfwright.Drawing;

/// <summary>Builds a <see cref="Path"/> one operation after another. Every number is checked when it is given, so a path that exists is always finite.</summary>
internal sealed class PathBuilder
{
    /// <summary>The control point distance for a quarter circle of radius 1: 4/3 tan(pi/8).</summary>
    private const double Kappa = 0.5522847498307936;

    private readonly List<PathSegment> _segments = [];
    private Point _current;
    private Point _start;
    private bool _hasCurrent;

    /// <summary>Begins a new figure at a point.</summary>
    internal PathBuilder MoveTo(double x, double y)
    {
        Finite(x, y);
        _segments.Add(new PathSegment(PathSegmentKind.MoveTo, new Point(x, y), default, default));
        _current = _start = new Point(x, y);
        _hasCurrent = true;
        return this;
    }

    internal PathBuilder LineTo(double x, double y)
    {
        Finite(x, y);
        RequireCurrent();
        _segments.Add(new PathSegment(PathSegmentKind.LineTo, new Point(x, y), default, default));
        _current = new Point(x, y);
        return this;
    }

    /// <summary>A cubic Bezier curve from the current point, through two control points, to an end point.</summary>
    internal PathBuilder CubicTo(double x1, double y1, double x2, double y2, double x3, double y3)
    {
        Finite(x1, y1, x2, y2, x3, y3);
        RequireCurrent();
        AddCubic(new Point(x1, y1), new Point(x2, y2), new Point(x3, y3));
        return this;
    }

    /// <summary>
    /// A quadratic Bezier curve, written as the cubic that draws exactly the same curve (each control point two thirds of the way from
    /// an end of the curve to the quadratic's own control point).
    /// </summary>
    internal PathBuilder QuadTo(double x1, double y1, double x2, double y2)
    {
        Finite(x1, y1, x2, y2);
        RequireCurrent();
        Point p0 = _current;
        AddCubic(
            new Point(p0.X + (2.0 / 3.0 * (x1 - p0.X)), p0.Y + (2.0 / 3.0 * (y1 - p0.Y))),
            new Point(x2 + (2.0 / 3.0 * (x1 - x2)), y2 + (2.0 / 3.0 * (y1 - y2))),
            new Point(x2, y2));
        return this;
    }

    /// <summary>
    /// An arc of an ellipse with its centre at (<paramref name="cx"/>, <paramref name="cy"/>), from <paramref name="startDegrees"/> by
    /// <paramref name="sweepDegrees"/>. An angle of 0 is the point to the right of the centre and a positive angle turns clockwise as seen
    /// on the canvas (y down). The arc is drawn as cubic curves of at most a quarter turn each. When a figure is open the path is joined to the start of the arc with a line;
    /// otherwise the arc begins a figure. A sweep of more than 360 degrees is one full turn.
    /// </summary>
    internal PathBuilder Arc(double cx, double cy, double rx, double ry, double startDegrees, double sweepDegrees)
    {
        Finite(cx, cy, rx, ry, startDegrees, sweepDegrees);
        if (rx < 0 || ry < 0)
        {
            throw new ArgumentException("A radius is not negative.");
        }

        double sweep = Math.Clamp(sweepDegrees, -360, 360);
        double startRadians = startDegrees * Math.PI / 180.0;
        Point first = new(cx + (rx * Math.Cos(startRadians)), cy + (ry * Math.Sin(startRadians)));
        if (_hasCurrent)
        {
            if (first != _current)
            {
                _segments.Add(new PathSegment(PathSegmentKind.LineTo, first, default, default));
                _current = first;
            }
        }
        else
        {
            MoveTo(first.X, first.Y);
        }

        if (sweep == 0)
        {
            return this;
        }

        int parts = (int)Math.Ceiling(Math.Abs(sweep) / 90.0);
        double step = sweep / parts * Math.PI / 180.0;
        double k = 4.0 / 3.0 * Math.Tan(step / 4.0);
        double angle = startRadians;
        for (int i = 0; i < parts; i++)
        {
            double cos0 = Math.Cos(angle);
            double sin0 = Math.Sin(angle);
            double cos1 = Math.Cos(angle + step);
            double sin1 = Math.Sin(angle + step);
            AddCubic(
                new Point(cx + (rx * (cos0 - (k * sin0))), cy + (ry * (sin0 + (k * cos0)))),
                new Point(cx + (rx * (cos1 + (k * sin1))), cy + (ry * (sin1 - (k * cos1)))),
                new Point(cx + (rx * cos1), cy + (ry * sin1)));
            angle += step;
        }

        return this;
    }

    /// <summary>A rectangle from its top left corner. A negative width or height puts the corner on the other side; the rectangle is a closed figure of its own.</summary>
    internal PathBuilder Rectangle(double x, double y, double width, double height)
    {
        Finite(x, y, width, height);
        _segments.Add(new PathSegment(PathSegmentKind.Rectangle, new Point(x, y), new Point(width, height), default));
        _current = _start = new Point(x, y);
        _hasCurrent = true;
        return this;
    }

    /// <summary>A rectangle with rounded corners, from its top left corner. A radius larger than half the side is cut to half of it.</summary>
    internal PathBuilder RoundedRectangle(double x, double y, double width, double height, double radiusX, double radiusY)
    {
        Finite(x, y, width, height, radiusX, radiusY);
        if (radiusX < 0 || radiusY < 0)
        {
            throw new ArgumentException("A corner radius is not negative.");
        }

        if (width < 0)
        {
            x += width;
            width = -width;
        }

        if (height < 0)
        {
            y += height;
            height = -height;
        }

        double rx = Math.Min(radiusX, width / 2);
        double ry = Math.Min(radiusY, height / 2);
        if (rx == 0 || ry == 0)
        {
            return Rectangle(x, y, width, height);
        }

        double right = x + width;
        double bottom = y + height;
        double kx = rx * Kappa;
        double ky = ry * Kappa;
        MoveTo(x + rx, y);
        LineTo(right - rx, y);
        CubicTo(right - rx + kx, y, right, y + ry - ky, right, y + ry);
        LineTo(right, bottom - ry);
        CubicTo(right, bottom - ry + ky, right - rx + kx, bottom, right - rx, bottom);
        LineTo(x + rx, bottom);
        CubicTo(x + rx - kx, bottom, x, bottom - ry + ky, x, bottom - ry);
        LineTo(x, y + ry);
        CubicTo(x, y + ry - ky, x + rx - kx, y, x + rx, y);
        return Close();
    }

    /// <summary>An ellipse as a closed figure of four curves, from its centre and its two radii.</summary>
    internal PathBuilder Ellipse(double cx, double cy, double rx, double ry)
    {
        Finite(cx, cy, rx, ry);
        if (rx < 0 || ry < 0)
        {
            throw new ArgumentException("A radius is not negative.");
        }

        double kx = rx * Kappa;
        double ky = ry * Kappa;
        MoveTo(cx + rx, cy);
        CubicTo(cx + rx, cy + ky, cx + kx, cy + ry, cx, cy + ry);
        CubicTo(cx - kx, cy + ry, cx - rx, cy + ky, cx - rx, cy);
        CubicTo(cx - rx, cy - ky, cx - kx, cy - ry, cx, cy - ry);
        CubicTo(cx + kx, cy - ry, cx + rx, cy - ky, cx + rx, cy);
        return Close();
    }

    /// <summary>Closes the open figure with a line back to where it began. With no figure open it does nothing.</summary>
    internal PathBuilder Close()
    {
        if (_hasCurrent && _segments.Count > 0 && _segments[^1].Kind != PathSegmentKind.Close)
        {
            _segments.Add(new PathSegment(PathSegmentKind.Close, default, default, default));
            _current = _start;
        }

        return this;
    }

    /// <summary>The path so far. The builder can go on, and a path already built does not change.</summary>
    internal Path Build() => new([.. _segments]);

    private void AddCubic(Point c1, Point c2, Point end)
    {
        _segments.Add(new PathSegment(PathSegmentKind.CubicTo, c1, c2, end));
        _current = end;
    }

    private void RequireCurrent()
    {
        if (!_hasCurrent)
        {
            throw new InvalidOperationException("The path has no current point: begin with MoveTo.");
        }
    }

    private static void Finite(params double[] values)
    {
        foreach (double value in values)
        {
            if (!double.IsFinite(value))
            {
                throw new ArgumentException($"A coordinate is a finite number, not {value}.");
            }
        }
    }
}
