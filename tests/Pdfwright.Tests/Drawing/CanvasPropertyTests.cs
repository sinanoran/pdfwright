using Pdfwright.Drawing;
using Pdfwright.Pdf;
using Pdfwright.Tests.Pdf;
using Path = Pdfwright.Drawing.Path;

namespace Pdfwright.Tests.Drawing;

/// <summary>Random drawings: the stream is always balanced, and the same drawing is always the same bytes.</summary>
public class CanvasPropertyTests
{
    private static Path RandomPath(Random random)
    {
        PathBuilder builder = new();
        builder.MoveTo(random.NextDouble() * 400, random.NextDouble() * 300);
        for (int i = random.Next(0, 8); i > 0; i--)
        {
            double x() => (random.NextDouble() * 500) - 50;
            switch (random.Next(8))
            {
                case 0: builder.LineTo(x(), x()); break;
                case 1: builder.CubicTo(x(), x(), x(), x(), x(), x()); break;
                case 2: builder.QuadTo(x(), x(), x(), x()); break;
                case 3: builder.Arc(x(), x(), Math.Abs(x()), Math.Abs(x()), x(), x() * 3); break;
                case 4: builder.Rectangle(x(), x(), x(), x()); break;
                case 5: builder.RoundedRectangle(x(), x(), x(), x(), Math.Abs(x()), Math.Abs(x())); break;
                case 6: builder.Ellipse(x(), x(), Math.Abs(x()), Math.Abs(x())); break;
                default: builder.Close(); break;
            }
        }

        return builder.Build();
    }

    private static void Scribble(Canvas canvas, int seed)
    {
        Random random = new(seed);
        for (int step = 0; step < 200; step++)
        {
            switch (random.Next(11))
            {
                case 0:
                    canvas.Save();
                    break;
                case 1:
                    if (canvas.SaveDepth > 0)
                    {
                        canvas.Restore();
                    }

                    break;
                case 2:
                    canvas.Translate(random.NextDouble() * 50, random.NextDouble() * 50);
                    break;
                case 3:
                    canvas.Rotate(random.Next(-400, 400));
                    break;
                case 4:
                    canvas.Scale(0.5 + random.NextDouble(), 0.5 + random.NextDouble());
                    break;
                case 5:
                    canvas.Clip(RandomPath(random), random.Next(2) == 0 ? FillRule.NonZero : FillRule.EvenOdd);
                    break;
                case 6 or 7:
                    canvas.Fill(RandomPath(random), new Brush(random.Next(2) == 0 ? Color.Gray(random.NextDouble()) : Color.FromRgb(random.NextDouble(), random.NextDouble(), random.NextDouble()), random.Next(3) == 0 ? 1 : random.NextDouble(), random.Next(2) == 0 ? FillRule.NonZero : FillRule.EvenOdd));
                    break;
                case 8 or 9:
                    double[]? dashes = random.Next(2) == 0 ? null : [1 + random.Next(5), random.Next(1, 4)];
                    canvas.Stroke(RandomPath(random), new Pen(Color.FromRgb(random.NextDouble(), 0.5, 0.25), random.Next(0, 6), (LineCap)random.Next(3), (LineJoin)random.Next(3), 1 + random.Next(10), dashes, random.Next(3), random.NextDouble()));
                    break;
                default:
                    canvas.Transform(new Matrix(1, random.NextDouble() / 4, random.NextDouble() / 4, 1, 0, 0));
                    break;
            }
        }
    }

    private static string[] Lines(int seed)
    {
        Drawn drawn = CanvasTestKit.Run(c => Scribble(c, seed));
        return drawn.Lines;
    }

    [Fact]
    public void Random_drawings_with_random_nesting_always_make_a_balanced_stream()
    {
        for (int seed = 0; seed < 150; seed++)
        {
            string[] lines = Lines(seed);
            int depth = 0;
            foreach (string line in lines)
            {
                if (line == "q")
                {
                    depth++;
                }
                else if (line == "Q")
                {
                    depth--;
                    Assert.True(depth >= 0, $"seed {seed}: a Q with no q");
                }
            }

            Assert.True(depth == 0, $"seed {seed}: {depth} q left open");
            Assert.Equal("q", lines[0]);
            Assert.Equal("Q", lines[^1]);
        }
    }

    [Fact]
    public void Every_number_in_a_random_drawing_is_plain_decimal_text()
    {
        foreach (string line in Lines(3).Concat(Lines(4)))
        {
            Assert.DoesNotContain("E", line, StringComparison.Ordinal);
            Assert.DoesNotContain("NaN", line, StringComparison.Ordinal);
            Assert.DoesNotContain(",", line, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_same_drawing_twice_is_the_same_stream()
    {
        for (int seed = 0; seed < 25; seed++)
        {
            Assert.Equal(Lines(seed), Lines(seed));
        }
    }

    [Fact]
    public void The_same_document_twice_is_the_same_bytes_and_qpdf_accepts_it()
    {
        byte[] Make()
        {
            using MemoryStream stream = new();
            using (PdfDocumentWriter document = new(stream, new PdfDocumentOptions { Created = PdfTestKit.Created }))
            {
                PdfPageWriter page = document.BeginPage(595, 842);
                using (Canvas canvas = new(page, 20, 800, 500, 400))
                {
                    for (int seed = 0; seed < 5; seed++)
                    {
                        Scribble(canvas, seed);
                    }
                }

                page.End();
                document.Complete();
            }

            return stream.ToArray();
        }

        byte[] first = Make();
        Assert.Equal(first, Make());
        PdfTestKit.AssertQpdfAccepts(first, "canvas-random");
    }
}
