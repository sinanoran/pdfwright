using Pdfwright.Drawing;
using Pdfwright.Pdf;
using Path = Pdfwright.Drawing.Path;

namespace Pdfwright.Tests.Drawing;

/// <summary>The content stream of a canvas, operator by operator.</summary>
public class CanvasContentTests
{
    [Fact]
    public void A_canvas_is_one_q_and_one_flip_and_one_Q()
    {
        Drawn drawn = CanvasTestKit.Run(_ => { });
        Assert.Equal(CanvasTestKit.Wrap(), drawn.Lines);
    }

    [Fact]
    public void The_flip_puts_the_box_top_at_the_given_y_and_the_box_origin_at_the_given_x()
    {
        Drawn drawn = CanvasTestKit.Run(_ => { }, left: 30, top: 700);
        Assert.Equal("1 0 0 -1 30 700 cm", drawn.Lines[1]);
    }

    [Fact]
    public void A_rectangle_at_10_10_lands_at_the_page_top_minus_10_minus_its_height()
    {
        Drawn drawn = CanvasTestKit.Run(c => c.Fill(CanvasTestKit.Rect(10, 10, 30, 20), new Brush(Color.Black)));

        // Read back: apply the matrix the canvas wrote to the corners of the `re`.
        double[] cm = CanvasTestKit.Numbers(drawn.Lines[1]);
        Matrix flip = new(cm[0], cm[1], cm[2], cm[3], cm[4], cm[5]);
        double[] re = CanvasTestKit.Numbers(Assert.Single(drawn.Inner, l => l.EndsWith(" re", StringComparison.Ordinal)));
        Point a = flip.Apply(new Point(re[0], re[1]));
        Point b = flip.Apply(new Point(re[0] + re[2], re[1] + re[3]));

        Assert.Equal(10, Math.Min(a.X, b.X));
        Assert.Equal(40, Math.Max(a.X, b.X));
        Assert.Equal(CanvasTestKit.PageHeight - 10 - 20, Math.Min(a.Y, b.Y));
        Assert.Equal(CanvasTestKit.PageHeight - 10, Math.Max(a.Y, b.Y));
    }

    [Fact]
    public void A_fill_is_a_colour_a_path_and_a_fill_operator()
    {
        Drawn drawn = CanvasTestKit.Run(c => c.Fill(CanvasTestKit.Rect(10, 10, 50, 20), new Brush(Color.FromRgb(1.0, 0.0, 0.0))));
        Assert.Equal(CanvasTestKit.Wrap("1 0 0 rg", "10 10 50 20 re", "f"), drawn.Lines);
    }

    [Fact]
    public void A_gray_is_written_with_the_short_operator_and_the_default_black_is_not_written_at_all()
    {
        Drawn drawn = CanvasTestKit.Run(c =>
        {
            c.Fill(CanvasTestKit.Rect(0, 0, 1, 1), new Brush(Color.Black));
            c.Fill(CanvasTestKit.Rect(0, 0, 1, 1), new Brush(Color.Gray(0.5)));
        });
        Assert.Equal(CanvasTestKit.Wrap("0 0 1 1 re", "f", "0.5 g", "0 0 1 1 re", "f"), drawn.Lines);
    }

    [Fact]
    public void A_colour_is_written_only_when_it_changes()
    {
        Drawn drawn = CanvasTestKit.Run(c =>
        {
            Brush red = new(Color.FromRgb(255, 0, 0));
            c.Fill(CanvasTestKit.Rect(0, 0, 1, 1), red);
            c.Fill(CanvasTestKit.Rect(2, 0, 1, 1), red);
        });
        Assert.Equal(CanvasTestKit.Wrap("1 0 0 rg", "0 0 1 1 re", "f", "2 0 1 1 re", "f"), drawn.Lines);
    }

    [Fact]
    public void The_even_odd_rule_writes_f_star()
    {
        Drawn drawn = CanvasTestKit.Run(c => c.Fill(CanvasTestKit.Rect(0, 0, 4, 4), new Brush(Color.Black, rule: FillRule.EvenOdd)));
        Assert.Equal(CanvasTestKit.Wrap("0 0 4 4 re", "f*"), drawn.Lines);
    }

    [Fact]
    public void A_path_is_written_segment_by_segment()
    {
        Path path = new PathBuilder().MoveTo(1, 2).LineTo(3, 4).CubicTo(5, 6, 7, 8, 9, 10).Close().Build();
        Drawn drawn = CanvasTestKit.Run(c => c.Fill(path, new Brush(Color.Black)));
        Assert.Equal(CanvasTestKit.Wrap("1 2 m", "3 4 l", "5 6 7 8 9 10 c", "h", "f"), drawn.Lines);
    }

    [Fact]
    public void A_pen_writes_its_width_ends_corners_miter_limit_dashes_and_colour_then_strokes()
    {
        Path line = new PathBuilder().MoveTo(10, 10).LineTo(90, 10).Build();
        Pen pen = new(Color.FromRgb(0.0, 0.0, 1.0), width: 2, cap: LineCap.Round, join: LineJoin.Bevel, miterLimit: 4, dashes: [3, 2], dashPhase: 1, opacity: 0.25);
        Drawn drawn = CanvasTestKit.Run(c => c.Stroke(line, pen));
        Assert.Equal(
            CanvasTestKit.Wrap("0 0 1 RG", "2 w", "1 J", "2 j", "4 M", "[3 2] 1 d", "/G1 gs", "10 10 m", "90 10 l", "S"),
            drawn.Lines);
        Assert.Equal([("G1", 1.0, 0.25)], drawn.States);
    }

    [Fact]
    public void The_state_a_second_stroke_shares_is_not_written_again_and_a_changed_dash_is()
    {
        Path line = new PathBuilder().MoveTo(0, 0).LineTo(5, 5).Build();
        Pen dashed = new(Color.Black, width: 2, dashes: [4]);
        Pen solid = new(Color.Black, width: 2);
        Drawn drawn = CanvasTestKit.Run(c =>
        {
            c.Stroke(line, dashed);
            c.Stroke(line, dashed);
            c.Stroke(line, solid);
        });
        Assert.Equal(
            CanvasTestKit.Wrap("2 w", "[4] 0 d", "0 0 m", "5 5 l", "S", "0 0 m", "5 5 l", "S", "[] 0 d", "0 0 m", "5 5 l", "S"),
            drawn.Lines);
    }

    [Fact]
    public void Fill_and_stroke_opacity_are_separate_and_one_state_is_one_ExtGState()
    {
        Path box = CanvasTestKit.Rect(0, 0, 10, 10);
        Drawn drawn = CanvasTestKit.Run(c =>
        {
            c.Fill(box, new Brush(Color.Black, opacity: 0.5));
            c.Fill(box, new Brush(Color.Black, opacity: 0.5));
            c.Stroke(box, new Pen(Color.Black, opacity: 0.25));
            c.Fill(box, new Brush(Color.Black, opacity: 0.5));
            c.Stroke(box, new Pen(Color.Black, opacity: 0.25));
            c.Fill(box, new Brush(Color.Black));
        });

        Assert.Equal(
            CanvasTestKit.Wrap(
                "/G1 gs", "0 0 10 10 re", "f", "0 0 10 10 re", "f",
                "/G2 gs", "0 0 10 10 re", "S",
                "0 0 10 10 re", "f",
                "0 0 10 10 re", "S",
                "/G3 gs", "0 0 10 10 re", "f"),
            drawn.Lines);
        Assert.Equal([("G1", 0.5, 1.0), ("G2", 0.5, 0.25), ("G3", 1.0, 0.25)], drawn.States);
    }

    [Fact]
    public void The_same_opacity_pair_asked_for_again_is_the_same_resource()
    {
        using MemoryStream stream = new();
        using PdfDocumentWriter document = new(stream, new PdfWriterOptions());
        PdfPageWriter page = document.BeginPage(100, 100);
        Assert.Equal(page.UseOpacity(0.5, 0.25), page.UseOpacity(0.5, 0.25));
        Assert.NotEqual(page.UseOpacity(0.5, 0.25), page.UseOpacity(0.25, 0.5));
        Assert.Equal(page.UseOpacity(0.5), page.UseOpacity(0.5, 0.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => page.UseOpacity(0.5, 1.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => page.UseOpacity(-0.1, 0.5));
    }

    [Fact]
    public void An_empty_path_draws_nothing_and_writes_nothing()
    {
        Drawn drawn = CanvasTestKit.Run(c =>
        {
            c.Fill(Path.Empty, new Brush(Color.FromRgb(1.0, 0.0, 0.0), opacity: 0.5));
            c.Stroke(new PathBuilder().Build(), new Pen(Color.FromRgb(1.0, 0.0, 0.0), width: 9));
            c.Fill(new PathBuilder().MoveTo(3, 3).Build(), new Brush(Color.Black));
        });
        Assert.Equal(CanvasTestKit.Wrap(), drawn.Lines);
        Assert.Empty(drawn.States);
    }

    [Fact]
    public void A_clip_is_W_then_n_and_lasts_until_the_restore()
    {
        Drawn drawn = CanvasTestKit.Run(c =>
        {
            c.Save();
            c.Clip(CanvasTestKit.Rect(0, 0, 100, 50));
            c.Fill(CanvasTestKit.Rect(0, 0, 200, 200), new Brush(Color.Black));
            c.Restore();
            c.Fill(CanvasTestKit.Rect(0, 0, 5, 5), new Brush(Color.Black));
        });
        Assert.Equal(
            CanvasTestKit.Wrap("q", "0 0 100 50 re", "W", "n", "0 0 200 200 re", "f", "Q", "0 0 5 5 re", "f"),
            drawn.Lines);
    }

    [Fact]
    public void An_even_odd_clip_is_W_star()
    {
        Drawn drawn = CanvasTestKit.Run(c => c.Clip(CanvasTestKit.Rect(0, 0, 9, 9), FillRule.EvenOdd));
        Assert.Equal(CanvasTestKit.Wrap("0 0 9 9 re", "W*", "n"), drawn.Lines);
    }

    [Fact]
    public void The_clip_of_an_empty_path_is_an_empty_area()
    {
        Drawn drawn = CanvasTestKit.Run(c => c.Clip(Path.Empty));
        Assert.Equal(CanvasTestKit.Wrap("0 0 0 0 re", "W", "n"), drawn.Lines);
    }

    [Fact]
    public void Ending_the_canvas_closes_every_open_save()
    {
        Drawn drawn = CanvasTestKit.Run(c =>
        {
            c.Save();
            c.Save();
            c.Save();
            c.Restore();
        });
        Assert.Equal(CanvasTestKit.Wrap("q", "q", "q", "Q", "Q", "Q"), drawn.Lines);
    }

    [Fact]
    public void A_restore_with_no_save_and_drawing_after_the_end_are_errors()
    {
        using MemoryStream stream = new();
        using PdfDocumentWriter document = new(stream, new PdfWriterOptions());
        PdfPageWriter page = document.BeginPage(100, 100);
        Canvas canvas = new(page, 0, 100, 100, 100);
        Assert.Throws<InvalidOperationException>(canvas.Restore);
        canvas.Save();
        canvas.Restore();
        Assert.Throws<InvalidOperationException>(canvas.Restore);
        canvas.End();
        canvas.End();
        Assert.Throws<InvalidOperationException>(() => canvas.Save());
        Assert.Throws<InvalidOperationException>(() => canvas.Fill(CanvasTestKit.Rect(0, 0, 1, 1), new Brush(Color.Black)));
    }

    [Fact]
    public void A_restore_brings_back_the_colour_the_canvas_tracks()
    {
        Drawn drawn = CanvasTestKit.Run(c =>
        {
            Path box = CanvasTestKit.Rect(0, 0, 1, 1);
            c.Save();
            c.Fill(box, new Brush(Color.FromRgb(0.0, 1.0, 0.0)));
            c.Restore();
            c.Fill(box, new Brush(Color.FromRgb(0.0, 1.0, 0.0)));
        });
        Assert.Equal(
            CanvasTestKit.Wrap("q", "0 1 0 rg", "0 0 1 1 re", "f", "Q", "0 1 0 rg", "0 0 1 1 re", "f"),
            drawn.Lines);
    }

    [Fact]
    public void Transforms_are_cm_operators()
    {
        Drawn drawn = CanvasTestKit.Run(c =>
        {
            c.Translate(5, 6);
            c.Scale(2, 3);
            c.Rotate(90);
            c.Rotate(-90);
            c.Transform(new Matrix(1, 2, 3, 4, 5, 6));
        });
        Assert.Equal(
            CanvasTestKit.Wrap("1 0 0 1 5 6 cm", "2 0 0 3 0 0 cm", "0 1 -1 0 0 0 cm", "0 -1 1 0 0 0 cm", "1 2 3 4 5 6 cm"),
            drawn.Lines);
    }

    [Fact]
    public void A_tiny_scale_is_written_with_its_own_precision_not_as_zeros()
    {
        Drawn drawn = CanvasTestKit.Run(c => c.Scale(1e-6, 2.5e-7));
        Assert.Equal(CanvasTestKit.Wrap("0.000001 0 0 0.00000025 0 0 cm"), drawn.Lines);
        Assert.DoesNotContain(drawn.Lines, l => l.Contains('E', StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_tiny_rotation_is_not_lost()
    {
        Drawn drawn = CanvasTestKit.Run(c => c.Rotate(0.0001));
        double[] n = CanvasTestKit.Numbers(drawn.Inner[0]);
        double radians = 0.0001 * Math.PI / 180;
        Assert.Equal(Math.Sin(radians), n[1], 14);
        Assert.Equal(-Math.Sin(radians), n[2], 14);
        Assert.NotEqual(0, n[1]);
        Assert.DoesNotContain("E", drawn.Inner[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Numbers_with_relative_precision_are_plain_decimals_that_round_trip()
    {
        foreach (double value in new[] { 1e-6, -3.3e-9, 0.1234567891234, 123456789.5, 1e15, 0.5, -2, 1.0 / 3 })
        {
            string text = PdfReal.FormatRelative(value);
            Assert.DoesNotContain("E", text, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(value, double.Parse(text, System.Globalization.CultureInfo.InvariantCulture), Math.Abs(value) * 1e-9);
        }

        Assert.Equal("0", PdfReal.FormatRelative(-0.0));
        Assert.Equal("0", PdfReal.FormatRelative(1e-30));
    }

    [Fact]
    public void A_transform_that_cannot_be_undone_is_refused()
    {
        Drawn ignored = CanvasTestKit.Run(c =>
        {
            Assert.Throws<ArgumentException>(() => c.Scale(0, 1));
            Assert.Throws<ArgumentException>(() => c.Scale(0, 0));
            Assert.Throws<ArgumentException>(() => c.Transform(new Matrix(1, 2, 2, 4, 0, 0)));
            Assert.Throws<ArgumentException>(() => c.Scale(1e-200, 1e-200));
            Assert.Throws<ArgumentException>(() => c.Scale(1e200, 1e200));
        });
        Assert.Equal(CanvasTestKit.Wrap(), ignored.Lines);
    }

    [Fact]
    public void A_rotation_is_clockwise_on_the_canvas()
    {
        // On a y-down canvas a clockwise quarter turn takes the point to the right of the origin to the point below it.
        Point turned = Matrix.Rotation(90).Apply(new Point(1, 0));
        Assert.Equal(0, turned.X, 12);
        Assert.Equal(1, turned.Y, 12);

        Drawn drawn = CanvasTestKit.Run(c => c.Rotate(30));
        double[] n = CanvasTestKit.Numbers(drawn.Inner[0]);
        Assert.Equal(Math.Cos(Math.PI / 6), n[0], 4);
        Assert.Equal(Math.Sin(Math.PI / 6), n[1], 4);
        Assert.Equal(-Math.Sin(Math.PI / 6), n[2], 4);
        Assert.Equal(Math.Cos(Math.PI / 6), n[3], 4);
    }

    [Fact]
    public void Matrices_compose_in_the_order_they_are_applied()
    {
        Matrix scaleThenMove = Matrix.Scaling(2, 2).Then(Matrix.Translation(10, 0));
        Assert.Equal(new Point(12, 2), scaleThenMove.Apply(new Point(1, 1)));
        Matrix moveThenScale = Matrix.Translation(10, 0).Then(Matrix.Scaling(2, 2));
        Assert.Equal(new Point(22, 2), moveThenScale.Apply(new Point(1, 1)));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void A_transform_with_a_number_that_is_not_finite_is_refused(double bad)
    {
        Drawn ignored = CanvasTestKit.Run(c =>
        {
            Assert.Throws<ArgumentException>(() => c.Translate(bad, 0));
            Assert.Throws<ArgumentException>(() => c.Scale(1, bad));
            Assert.Throws<ArgumentException>(() => c.Rotate(bad));
            Assert.Throws<ArgumentException>(() => c.Transform(new Matrix(1, 0, 0, 1, bad, 0)));
        });
        Assert.Equal(CanvasTestKit.Wrap(), ignored.Lines);
    }

    [Fact]
    public void A_pen_refuses_a_cap_or_a_join_that_is_not_defined()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Pen(Color.Black, cap: (LineCap)3));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Pen(Color.Black, cap: (LineCap)(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Pen(Color.Black, join: (LineJoin)7));
        Assert.Equal(LineCap.Square, new Pen(Color.Black, cap: LineCap.Square).Cap);
    }

    [Fact]
    public void A_brush_a_pen_and_a_colour_refuse_numbers_out_of_range()
    {
        Assert.Throws<ArgumentException>(() => Color.FromRgb(1.5, 0, 0));
        Assert.Throws<ArgumentException>(() => Color.FromRgb(double.NaN, 0, 0));
        Assert.Throws<ArgumentException>(() => Color.Gray(-0.1));
        Assert.Throws<ArgumentException>(() => new Brush(Color.Black, opacity: 2));
        Assert.Throws<ArgumentException>(() => new Pen(Color.Black, width: -1));
        Assert.Throws<ArgumentException>(() => new Pen(Color.Black, width: double.NaN));
        Assert.Throws<ArgumentException>(() => new Pen(Color.Black, miterLimit: 0.5));
        Assert.Throws<ArgumentException>(() => new Pen(Color.Black, dashes: [0, 0]));
        Assert.Throws<ArgumentException>(() => new Pen(Color.Black, dashes: [1, -1]));
        Assert.Throws<ArgumentException>(() => new Pen(Color.Black, opacity: -1));
        Assert.Equal(1.0, Color.FromRgb(255, 255, 255).R);
    }

    [Fact]
    public void Numbers_are_written_with_the_invariant_culture()
    {
        System.Globalization.CultureInfo before = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Drawn drawn = CanvasTestKit.Run(c => c.Fill(CanvasTestKit.Rect(1.5, 2.25, 3.125, 4), new Brush(Color.Black)));
            Assert.Equal(CanvasTestKit.Wrap("1.5 2.25 3.125 4 re", "f"), drawn.Lines);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = before;
        }
    }
}
