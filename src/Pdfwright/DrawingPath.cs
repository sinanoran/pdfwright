using Pdfwright.Graphics;
using Path = Pdfwright.Graphics.Path;

namespace Pdfwright;

/// <summary>
/// A shape made of lines and curves, in the coordinates of a <see cref="DrawingCanvas"/> (points, the origin at the top left, y down). A path never changes once built, and may be used on any number of canvases;
/// make one with <see cref="DrawingPathBuilder"/>.
/// </summary>
public sealed class DrawingPath
{
    internal DrawingPath(Path inner)
    {
        Inner = inner;
    }

    internal Path Inner { get; }

    /// <summary>Whether the path draws nothing: it has no segment but moves and closes.</summary>
    public bool IsEmpty => Inner.IsEmpty;
}

/// <summary>Builds a <see cref="DrawingPath"/> one operation after another. Every number is checked as it is given (it must be finite), so a path that exists is always finite. Angles are in degrees.</summary>
public sealed class DrawingPathBuilder
{
    private readonly PathBuilder _inner = new();

    /// <summary>Begins a new figure at a point.</summary>
    /// <param name="x">The x of the point.</param>
    /// <param name="y">The y of the point.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentException">A number is not finite.</exception>
    public DrawingPathBuilder MoveTo(double x, double y)
    {
        _ = _inner.MoveTo(x, y);
        return this;
    }

    /// <summary>A straight line from the current point.</summary>
    /// <param name="x">The x of the end.</param>
    /// <param name="y">The y of the end.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentException">A number is not finite.</exception>
    /// <exception cref="InvalidOperationException">There is no current point (no <see cref="MoveTo"/> yet).</exception>
    public DrawingPathBuilder LineTo(double x, double y)
    {
        _ = _inner.LineTo(x, y);
        return this;
    }

    /// <summary>A cubic Bezier curve from the current point, through two control points, to an end point.</summary>
    /// <param name="x1">The x of the first control point.</param>
    /// <param name="y1">The y of the first control point.</param>
    /// <param name="x2">The x of the second control point.</param>
    /// <param name="y2">The y of the second control point.</param>
    /// <param name="x3">The x of the end.</param>
    /// <param name="y3">The y of the end.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentException">A number is not finite.</exception>
    /// <exception cref="InvalidOperationException">There is no current point.</exception>
    public DrawingPathBuilder CubicTo(double x1, double y1, double x2, double y2, double x3, double y3)
    {
        _ = _inner.CubicTo(x1, y1, x2, y2, x3, y3);
        return this;
    }

    /// <summary>A quadratic Bezier curve, written as the cubic that draws exactly the same curve.</summary>
    /// <param name="x1">The x of the control point.</param>
    /// <param name="y1">The y of the control point.</param>
    /// <param name="x2">The x of the end.</param>
    /// <param name="y2">The y of the end.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentException">A number is not finite.</exception>
    /// <exception cref="InvalidOperationException">There is no current point.</exception>
    public DrawingPathBuilder QuadraticTo(double x1, double y1, double x2, double y2)
    {
        _ = _inner.QuadTo(x1, y1, x2, y2);
        return this;
    }

    /// <summary>
    /// An arc of an ellipse with its centre at (<paramref name="cx"/>, <paramref name="cy"/>), from <paramref name="startDegrees"/> by <paramref name="sweepDegrees"/>. An angle of 0 is the point to the right of
    /// the centre and a positive angle turns clockwise as seen on the canvas (y down). With a current point, a line joins it to the start of the arc; with none, the arc begins a figure.
    /// </summary>
    /// <param name="cx">The x of the centre.</param>
    /// <param name="cy">The y of the centre.</param>
    /// <param name="rx">The horizontal radius, not negative.</param>
    /// <param name="ry">The vertical radius, not negative.</param>
    /// <param name="startDegrees">Where the arc starts.</param>
    /// <param name="sweepDegrees">How far it goes (negative turns the other way).</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentException">A number is not finite or a radius is negative.</exception>
    public DrawingPathBuilder Arc(double cx, double cy, double rx, double ry, double startDegrees, double sweepDegrees)
    {
        _ = _inner.Arc(cx, cy, rx, ry, startDegrees, sweepDegrees);
        return this;
    }

    /// <summary>A rectangle from its top left corner, as a closed figure of its own.</summary>
    /// <param name="x">The x of the left edge.</param>
    /// <param name="y">The y of the top edge.</param>
    /// <param name="width">The width (a negative width puts the corner on the other side).</param>
    /// <param name="height">The height.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentException">A number is not finite.</exception>
    public DrawingPathBuilder Rectangle(double x, double y, double width, double height)
    {
        _ = _inner.Rectangle(x, y, width, height);
        return this;
    }

    /// <summary>A rectangle with rounded corners, from its top left corner. A radius larger than half the side is cut to half of it.</summary>
    /// <param name="x">The x of the left edge.</param>
    /// <param name="y">The y of the top edge.</param>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    /// <param name="radiusX">The horizontal radius of the corners, not negative.</param>
    /// <param name="radiusY">The vertical radius of the corners, not negative.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentException">A number is not finite, or a radius is negative.</exception>
    public DrawingPathBuilder RoundedRectangle(double x, double y, double width, double height, double radiusX, double radiusY)
    {
        _ = _inner.RoundedRectangle(x, y, width, height, radiusX, radiusY);
        return this;
    }

    /// <summary>An ellipse as a closed figure of four curves.</summary>
    /// <param name="cx">The x of the centre.</param>
    /// <param name="cy">The y of the centre.</param>
    /// <param name="rx">The horizontal radius, not negative.</param>
    /// <param name="ry">The vertical radius, not negative.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentException">A number is not finite or a radius is negative.</exception>
    public DrawingPathBuilder Ellipse(double cx, double cy, double rx, double ry)
    {
        _ = _inner.Ellipse(cx, cy, rx, ry);
        return this;
    }

    /// <summary>Closes the open figure with a line back to where it began. With no figure open it does nothing.</summary>
    /// <returns>This builder.</returns>
    public DrawingPathBuilder Close()
    {
        _ = _inner.Close();
        return this;
    }

    /// <summary>The path so far. The builder can go on, and a path already built does not change.</summary>
    /// <returns>The path.</returns>
    public DrawingPath Build() => new(_inner.Build());
}

/// <summary>What a stroke is drawn with: a colour, a width in points, the shapes of its ends and corners, a dash pattern and an opacity. Immutable.</summary>
public sealed class DrawingPen
{
    private readonly double[] _dashes;

    /// <summary>A pen.</summary>
    /// <param name="color">The colour of the line.</param>
    /// <param name="width">The line width in points (in the canvas's current units, if it has been scaled); zero draws the thinnest line the device can.</param>
    /// <param name="cap">The shape of the ends.</param>
    /// <param name="join">The shape of the corners.</param>
    /// <param name="miterLimit">Where a mitred corner is cut to a bevel, as a ratio of miter length to line width; at least 1.</param>
    /// <param name="dashes">The lengths of the dashes and the gaps in turn, or <see langword="null"/> or empty for a solid line.</param>
    /// <param name="dashPhase">How far into the pattern the line starts.</param>
    /// <param name="opacity">From 0 (nothing) to 1 (opaque).</param>
    /// <exception cref="ArgumentOutOfRangeException">A cap or a join is not a defined value.</exception>
    /// <exception cref="ArgumentException">A number is not finite, a width, dash or phase is negative, the miter limit is below 1, the opacity is not from 0 to 1, or the dashes are all zero.</exception>
    public DrawingPen(
        Color color,
        double width = 1,
        LineCap cap = LineCap.Butt,
        LineJoin join = LineJoin.Miter,
        double miterLimit = 10,
        IEnumerable<double>? dashes = null,
        double dashPhase = 0,
        double opacity = 1)
    {
        Pen = new Pen(color, width, cap, join, miterLimit, dashes, dashPhase, opacity);
        _dashes = [.. Pen.Dashes];
    }

    /// <summary>The colour of the line.</summary>
    public Color Color => Pen.Color;

    /// <summary>The line width.</summary>
    public double Width => Pen.Width;

    /// <summary>The shape of the ends.</summary>
    public LineCap Cap => Pen.Cap;

    /// <summary>The shape of the corners.</summary>
    public LineJoin Join => Pen.Join;

    /// <summary>The miter limit.</summary>
    public double MiterLimit => Pen.MiterLimit;

    /// <summary>The dashes and gaps in turn; empty for a solid line.</summary>
    public IReadOnlyList<double> Dashes => _dashes;

    /// <summary>How far into the dash pattern the line starts.</summary>
    public double DashPhase => Pen.DashPhase;

    /// <summary>The opacity, from 0 to 1.</summary>
    public double Opacity => Pen.Opacity;

    internal Pen Pen { get; }
}
