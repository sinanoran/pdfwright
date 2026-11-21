using Pdfwright.Graphics;
using Path = Pdfwright.Graphics.Path;

namespace Pdfwright.Tests.Drawing;

public class PathBuilderTests
{
    private static Point Cubic(Point p0, PathSegment s, double t)
    {
        double u = 1 - t;
        double a = u * u * u;
        double b = 3 * u * u * t;
        double c = 3 * u * t * t;
        double d = t * t * t;
        return new Point(
            (a * p0.X) + (b * s.P1.X) + (c * s.P2.X) + (d * s.P3.X),
            (a * p0.Y) + (b * s.P1.Y) + (c * s.P2.Y) + (d * s.P3.Y));
    }

    [Fact]
    public void A_path_is_immutable_once_built()
    {
        PathBuilder builder = new PathBuilder().MoveTo(0, 0).LineTo(1, 1);
        Path first = builder.Build();
        builder.LineTo(2, 2);
        Assert.Equal(2, first.Segments.Count);
        Assert.Equal(3, builder.Build().Segments.Count);
    }

    [Fact]
    public void A_quadratic_is_raised_to_the_cubic_that_draws_the_same_curve()
    {
        Path path = new PathBuilder().MoveTo(0, 0).QuadTo(3, 6, 6, 0).Build();
        PathSegment cubic = path.Segments[1];
        Assert.Equal(PathSegmentKind.CubicTo, cubic.Kind);
        Assert.Equal(2, cubic.P1.X, 12);
        Assert.Equal(4, cubic.P1.Y, 12);
        Assert.Equal(4, cubic.P2.X, 12);
        Assert.Equal(4, cubic.P2.Y, 12);
        Assert.Equal(new Point(6, 0), cubic.P3);

        // The same points on both curves, along the way.
        for (double t = 0; t <= 1; t += 0.125)
        {
            double u = 1 - t;
            Point q = new((u * u * 0) + (2 * u * t * 3) + (t * t * 6), (u * u * 0) + (2 * u * t * 6) + (t * t * 0));
            Point c = Cubic(new Point(0, 0), cubic, t);
            Assert.Equal(q.X, c.X, 12);
            Assert.Equal(q.Y, c.Y, 12);
        }
    }

    [Fact]
    public void An_arc_is_cut_into_curves_of_at_most_a_quarter_turn_that_stay_on_the_circle()
    {
        Path path = new PathBuilder().Arc(50, 40, 10, 10, 20, 250).Build();

        // A move to the start, then ceil(250 / 90) = 3 curves.
        Assert.Equal(PathSegmentKind.MoveTo, path.Segments[0].Kind);
        Assert.Equal(3, path.Segments.Count(s => s.Kind == PathSegmentKind.CubicTo));
        Assert.Equal(50 + (10 * Math.Cos(20 * Math.PI / 180)), path.Segments[0].P1.X, 9);

        Point from = path.Segments[0].P1;
        double angle = 20;
        foreach (PathSegment s in path.Segments.Skip(1))
        {
            for (double t = 0; t <= 1; t += 0.1)
            {
                Point p = Cubic(from, s, t);
                double radius = Math.Sqrt(Math.Pow(p.X - 50, 2) + Math.Pow(p.Y - 40, 2));
                Assert.Equal(10, radius, 1); // a cubic is within 0.03 percent of the circle for a quarter turn
            }

            // Each curve covers no more than 90 degrees.
            double startAngle = Math.Atan2(from.Y - 40, from.X - 50) * 180 / Math.PI;
            double endAngle = Math.Atan2(s.P3.Y - 40, s.P3.X - 50) * 180 / Math.PI;
            double covered = ((endAngle - startAngle) % 360 + 360) % 360;
            Assert.True(covered <= 90.0001, $"a curve covers {covered} degrees");
            angle += covered;
            from = s.P3;
        }

        Assert.Equal(270, angle, 6);
        Assert.Equal(50 + (10 * Math.Cos(270 * Math.PI / 180)), from.X, 9);
        Assert.Equal(40 + (10 * Math.Sin(270 * Math.PI / 180)), from.Y, 9);
    }

    [Fact]
    public void A_negative_sweep_turns_the_other_way_and_a_sweep_beyond_a_turn_is_one_turn()
    {
        Path back = new PathBuilder().Arc(0, 0, 5, 5, 0, -90).Build();
        Assert.Equal(5, back.Segments[0].P1.X, 9);
        Assert.Equal(0, back.Segments[1].P3.X, 9);
        Assert.Equal(-5, back.Segments[1].P3.Y, 9); // a quarter turn against the clock, up the canvas

        Path many = new PathBuilder().Arc(0, 0, 5, 5, 0, 1000).Build();
        Assert.Equal(4, many.Segments.Count(s => s.Kind == PathSegmentKind.CubicTo));
        Assert.Equal(5, many.Segments[^1].P3.X, 9);
    }

    [Fact]
    public void An_arc_after_a_point_is_joined_to_it_with_a_line()
    {
        Path path = new PathBuilder().MoveTo(0, 0).Arc(10, 0, 5, 5, 0, 90).Build();
        Assert.Equal(PathSegmentKind.MoveTo, path.Segments[0].Kind);
        Assert.Equal(PathSegmentKind.LineTo, path.Segments[1].Kind);
        Assert.Equal(new Point(15, 0), path.Segments[1].P1);
    }

    [Fact]
    public void An_ellipse_is_four_curves_and_closed()
    {
        Path path = new PathBuilder().Ellipse(50, 50, 30, 20).Build();
        Assert.Equal(
            [PathSegmentKind.MoveTo, PathSegmentKind.CubicTo, PathSegmentKind.CubicTo, PathSegmentKind.CubicTo, PathSegmentKind.CubicTo, PathSegmentKind.Close],
            path.Segments.Select(s => s.Kind).ToArray());
        Assert.Equal(new Point(80, 50), path.Segments[0].P1);
        Assert.Equal(new Point(80, 50), path.Segments[4].P3);
        Assert.Equal(new Point(50, 70), path.Segments[1].P3);
        Assert.Equal(new Point(20, 50), path.Segments[2].P3);
        Assert.Equal(new Point(50, 30), path.Segments[3].P3);
    }

    [Fact]
    public void A_rounded_rectangle_clamps_its_radii_and_closes()
    {
        Path path = new PathBuilder().RoundedRectangle(10, 20, 100, 40, 15, 10).Build();
        Assert.Equal(PathSegmentKind.Close, path.Segments[^1].Kind);
        Assert.Equal(4, path.Segments.Count(s => s.Kind == PathSegmentKind.CubicTo));
        Assert.Equal(new Point(25, 20), path.Segments[0].P1);

        // A radius of more than half the side is half of it: a stadium whose straight parts have no length.
        Path stadium = new PathBuilder().RoundedRectangle(0, 0, 40, 20, 99, 99).Build();
        Assert.Contains(stadium.Segments, s => s.Kind == PathSegmentKind.CubicTo && s.P3 == new Point(40, 10));

        // No radius is a plain rectangle.
        Path plain = new PathBuilder().RoundedRectangle(1, 2, 3, 4, 0, 0).Build();
        Assert.Equal(PathSegmentKind.Rectangle, Assert.Single(plain.Segments).Kind);
    }

    [Fact]
    public void A_rectangle_with_a_negative_size_is_the_same_box_from_the_other_corner()
    {
        Path path = new PathBuilder().RoundedRectangle(100, 60, -100, -40, 0, 0).Build();
        PathSegment rectangle = Assert.Single(path.Segments);
        Assert.Equal(new Point(0, 20), rectangle.P1);
        Assert.Equal(new Point(100, 40), rectangle.P2);
    }

    [Fact]
    public void Close_does_nothing_twice_or_with_no_figure()
    {
        Assert.Empty(new PathBuilder().Close().Build().Segments);
        Path path = new PathBuilder().MoveTo(0, 0).LineTo(1, 1).Close().Close().Build();
        Assert.Equal(3, path.Segments.Count);
    }

    [Fact]
    public void A_line_or_curve_needs_a_current_point()
    {
        Assert.Throws<InvalidOperationException>(() => new PathBuilder().LineTo(1, 1));
        Assert.Throws<InvalidOperationException>(() => new PathBuilder().CubicTo(1, 1, 2, 2, 3, 3));
        Assert.Throws<InvalidOperationException>(() => new PathBuilder().QuadTo(1, 1, 2, 2));
    }

    public static TheoryData<string> Operations() =>
    [
        "move", "line", "cubic", "quad", "arc", "rect", "round", "ellipse",
    ];

    [Theory]
    [MemberData(nameof(Operations))]
    public void A_coordinate_that_is_not_finite_is_refused_wherever_it_stands(string operation)
    {
        double[] bad = [double.NaN, double.PositiveInfinity, double.NegativeInfinity];
        foreach (double x in bad)
        {
            for (int position = 0; position < 6; position++)
            {
                double[] v = [1, 2, 3, 4, 5, 6];
                v[position] = x;
                PathBuilder builder = new PathBuilder().MoveTo(0, 0);
                Action call = operation switch
                {
                    "move" when position < 2 => () => builder.MoveTo(v[0], v[1]),
                    "line" when position < 2 => () => builder.LineTo(v[0], v[1]),
                    "cubic" => () => builder.CubicTo(v[0], v[1], v[2], v[3], v[4], v[5]),
                    "quad" when position < 4 => () => builder.QuadTo(v[0], v[1], v[2], v[3]),
                    "arc" => () => builder.Arc(v[0], v[1], v[2], v[3], v[4], v[5]),
                    "rect" when position < 4 => () => builder.Rectangle(v[0], v[1], v[2], v[3]),
                    "round" => () => builder.RoundedRectangle(v[0], v[1], v[2], v[3], v[4], v[5]),
                    "ellipse" when position < 4 => () => builder.Ellipse(v[0], v[1], v[2], v[3]),
                    _ => () => throw new ArgumentException("positions past the operands of the operation"),
                };

                Assert.Throws<ArgumentException>(call);
            }
        }
    }

    [Fact]
    public void A_negative_radius_is_refused()
    {
        Assert.Throws<ArgumentException>(() => new PathBuilder().Arc(0, 0, -1, 1, 0, 90));
        Assert.Throws<ArgumentException>(() => new PathBuilder().Ellipse(0, 0, 1, -1));
        Assert.Throws<ArgumentException>(() => new PathBuilder().RoundedRectangle(0, 0, 1, 1, -1, 0));
    }
}
