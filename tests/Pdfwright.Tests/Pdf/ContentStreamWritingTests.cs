using System.Text;
using Pdfwright.Pdf;

namespace Pdfwright.Tests.Pdf;

/// <summary>
/// The content stream is written straight into bytes, without a string for each number. These tests hold it to the text the string writer made
/// (<see cref="ReferenceContentStream"/>): the number formats over many doubles, and the whole stream over a long random sequence of operators.
/// </summary>
public class ContentStreamWritingTests
{
    private static readonly double[] Edges =
    [
        0, -0.0, 1, -1, 0.5, -0.5, 0.1, 0.123455, 0.123454, 0.123456, 0.000005, 0.0000049999, 0.000004, -0.000005, -0.000004999, 0.00001, 0.999995, 0.9999949,
        -0.999995, 9.999995, 99999.999995, 1e-6, 1e-7, 1e-10, 1e-20, 1e-30, 1e-300, 1e-320, double.Epsilon, 1e5, 1e10, 1e15, 1e16,
        1e21, 1e22, 1e30, 1e100, 1e300, double.MaxValue, long.MaxValue, 123456789.123456789, 0.1 + 0.2, 1.0 / 3, 2.0 / 3, 595.275590551181,
        841.889763779528, 12.34567890123, 0.00000123456789, 1234567890.12345, 9.99999999995, 9.9999999995, 0.99999999995, 1.5, 2.5, 0.00002, 0.000015, 0.0000050001,
    ];

    private static IEnumerable<double> Doubles(int seed, int count)
    {
        Random random = new(seed);
        foreach (double edge in Edges)
        {
            yield return edge;
            yield return -edge;
        }

        for (int i = 0; i < count; i++)
        {
            double mantissa = (random.NextDouble() * 2) - 1;
            yield return mantissa * Math.Pow(10, random.Next(-25, 25));
            yield return Math.Round(mantissa * 1000, random.Next(0, 8)); // few decimals, many ties
            yield return random.Next(-100000, 100000) / 1000.0;
            yield return random.Next(-1000, 1000) * 0.000005; // on the rounding boundary of five decimals
            double bits = BitConverter.Int64BitsToDouble(random.NextInt64());
            yield return double.IsFinite(bits) ? bits : 0.25;
            yield return random.Next(-50, 50);
        }
    }

    private delegate int Writer(double value, Span<byte> destination);

    private static string Written(Writer write, double value)
    {
        byte[] buffer = new byte[400];
        int length = write(value, buffer);
        return Encoding.ASCII.GetString(buffer, 0, length);
    }

    [Fact]
    public void A_number_is_written_as_the_string_formatter_wrote_it()
    {
        int count = 0;
        foreach (double value in Doubles(1, 100_000))
        {
            string expected = ReferenceContentStream.Format(value);

            Assert.Equal(expected, PdfReal.Format(value));
            Assert.Equal(expected, Written(PdfReal.Write, value));
            count++;
        }

        Assert.True(count > 600_000);
    }

    [Fact]
    public void A_number_with_relative_precision_is_written_as_the_string_formatter_wrote_it()
    {
        foreach (double value in Doubles(2, 100_000))
        {
            string expected = ReferenceContentStream.FormatRelative(value);

            Assert.Equal(expected, PdfReal.FormatRelative(value));
            Assert.Equal(expected, Written(PdfReal.WriteRelative, value));
        }
    }

    [Fact]
    public void Negative_zero_and_the_numbers_that_round_to_it_are_written_as_zero()
    {
        foreach (double value in new[] { -0.0, -0.000001, -0.000004999, -1e-300 })
        {
            Assert.Equal("0", Written(PdfReal.Write, value));
        }

        Assert.Equal("0", Written(PdfReal.WriteRelative, -0.0));
        Assert.Equal("-0.00001", Written(PdfReal.Write, -0.000005));
        Assert.Equal("1.5", Written(PdfReal.Write, 1.5));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void A_number_that_is_not_finite_is_refused_when_written_too(double value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfReal.Write(value, new byte[400]));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfReal.WriteRelative(value, new byte[400]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ContentStreamBuilder().MoveTo(value, 0));
    }

    [Fact]
    public void A_content_stream_of_every_operator_is_the_bytes_the_string_writer_made()
    {
        Random random = new(3);
        double[] pool = [.. Doubles(4, 50)];
        double next() => pool[random.Next(pool.Length)];
        string[] names = ["F1", "X2", "G3", "a.b-c_d", "Z"];
        string name() => names[random.Next(names.Length)];
        ContentStreamBuilder actual = new();
        ReferenceContentStream expected = new();

        for (int i = 0; i < 20_000; i++)
        {
            switch (random.Next(34))
            {
                case 0:
                    _ = actual.SaveState();
                    _ = expected.SaveState();
                    break;
                case 1:
                    _ = actual.RestoreState();
                    _ = expected.RestoreState();
                    break;
                case 2:
                {
                    double a = next(), b = next(), c = next(), d = next(), e = next(), f = next();
                    _ = actual.Transform(a, b, c, d, e, f);
                    _ = expected.Transform(a, b, c, d, e, f);
                    break;
                }

                case 3:
                {
                    double v = next();
                    _ = actual.LineWidth(v);
                    _ = expected.LineWidth(v);
                    break;
                }

                case 4:
                {
                    int v = random.Next(-3, 4);
                    _ = actual.LineCap(v);
                    _ = expected.LineCap(v);
                    break;
                }

                case 5:
                {
                    int v = random.Next(-3, 4);
                    _ = actual.LineJoin(v);
                    _ = expected.LineJoin(v);
                    break;
                }

                case 6:
                {
                    double v = next();
                    _ = actual.MiterLimit(v);
                    _ = expected.MiterLimit(v);
                    break;
                }

                case 7:
                {
                    double[] pattern = [.. Enumerable.Range(0, random.Next(0, 5)).Select(_ => next())];
                    double phase = next();
                    _ = actual.Dash(pattern, phase);
                    _ = expected.Dash(pattern, phase);
                    break;
                }

                case 8:
                {
                    string n = name();
                    _ = actual.ExtGState(n);
                    _ = expected.ExtGState(n);
                    break;
                }

                case 9:
                {
                    double v = next();
                    _ = actual.FillGray(v);
                    _ = expected.FillGray(v);
                    break;
                }

                case 10:
                {
                    double v = next();
                    _ = actual.StrokeGray(v);
                    _ = expected.StrokeGray(v);
                    break;
                }

                case 11:
                {
                    double a = next(), b = next(), c = next();
                    _ = actual.FillRgb(a, b, c);
                    _ = expected.FillRgb(a, b, c);
                    break;
                }

                case 12:
                {
                    double a = next(), b = next(), c = next();
                    _ = actual.StrokeRgb(a, b, c);
                    _ = expected.StrokeRgb(a, b, c);
                    break;
                }

                case 13:
                {
                    double a = next(), b = next();
                    _ = actual.MoveTo(a, b);
                    _ = expected.MoveTo(a, b);
                    break;
                }

                case 14:
                {
                    double a = next(), b = next();
                    _ = actual.LineTo(a, b);
                    _ = expected.LineTo(a, b);
                    break;
                }

                case 15:
                {
                    double a = next(), b = next(), c = next(), d = next(), e = next(), f = next();
                    _ = actual.CurveTo(a, b, c, d, e, f);
                    _ = expected.CurveTo(a, b, c, d, e, f);
                    break;
                }

                case 16:
                {
                    double a = next(), b = next(), c = next(), d = next();
                    _ = actual.Rectangle(a, b, c, d);
                    _ = expected.Rectangle(a, b, c, d);
                    break;
                }

                case 17:
                    _ = actual.ClosePath();
                    _ = expected.ClosePath();
                    break;
                case 18:
                {
                    bool evenOdd = random.Next(2) == 0;
                    _ = actual.Fill(evenOdd);
                    _ = expected.Fill(evenOdd);
                    break;
                }

                case 19:
                    _ = actual.Stroke();
                    _ = expected.Stroke();
                    break;
                case 20:
                {
                    bool evenOdd = random.Next(2) == 0;
                    _ = actual.FillAndStroke(evenOdd);
                    _ = expected.FillAndStroke(evenOdd);
                    break;
                }

                case 21:
                {
                    bool evenOdd = random.Next(2) == 0;
                    _ = actual.Clip(evenOdd);
                    _ = expected.Clip(evenOdd);
                    break;
                }

                case 22:
                    _ = actual.EndPath();
                    _ = expected.EndPath();
                    break;
                case 23:
                {
                    string n = name();
                    _ = actual.DrawXObject(n);
                    _ = expected.DrawXObject(n);
                    break;
                }

                case 24:
                    _ = actual.BeginText();
                    _ = expected.BeginText();
                    break;
                case 25:
                    _ = actual.EndText();
                    _ = expected.EndText();
                    break;
                case 26:
                {
                    string n = name();
                    double size = next();
                    _ = actual.Font(n, size);
                    _ = expected.Font(n, size);
                    break;
                }

                case 27:
                {
                    double a = next(), b = next(), c = next(), d = next(), e = next(), f = next();
                    _ = actual.TextMatrix(a, b, c, d, e, f);
                    _ = expected.TextMatrix(a, b, c, d, e, f);
                    break;
                }

                case 28:
                {
                    double a = next(), b = next();
                    _ = actual.TextMove(a, b);
                    _ = expected.TextMove(a, b);
                    break;
                }

                case 29:
                {
                    int v = random.Next(0, 8);
                    _ = actual.TextRenderMode(v);
                    _ = expected.TextRenderMode(v);
                    break;
                }

                case 30:
                {
                    byte[] codes = new byte[random.Next(0, 9)];
                    random.NextBytes(codes);
                    _ = actual.ShowHex(codes);
                    _ = expected.ShowHex(codes);
                    break;
                }

                case 31:
                {
                    int code = random.Next(0, 0x10000);
                    _ = actual.ShowCode(code);
                    _ = expected.ShowHex(PdfEmbeddedFont.Code(code));
                    break;
                }

                case 32:
                {
                    (byte[] Codes, double? AdjustAfter)[] parts = [.. Enumerable.Range(0, random.Next(0, 5)).Select(_ =>
                    {
                        byte[] codes = new byte[random.Next(0, 5)];
                        random.NextBytes(codes);
                        return (codes, random.Next(2) == 0 ? (double?)null : next());
                    })];
                    _ = actual.ShowAdjusted(parts);
                    _ = expected.ShowAdjusted(parts);
                    break;
                }

                default:
                {
                    int mcid = random.Next(-5, 100000);
                    string text = new([.. Enumerable.Range(0, random.Next(0, 6)).Select(_ => (char)random.Next(0x20, 0x2000))]);
                    _ = actual.BeginMarkedContent("P", mcid);
                    _ = expected.BeginMarkedContent("P", mcid);
                    _ = actual.BeginActualText(text);
                    _ = expected.BeginActualText(text);
                    _ = actual.EndMarkedContent();
                    _ = expected.EndMarkedContent();
                    break;
                }
            }
        }

        Assert.True(actual.Length > 200_000);
        Assert.Equal(expected.ToArray(), actual.ToMemory().ToArray());
    }

    [Fact]
    public void A_glyph_code_is_four_upper_case_hexadecimal_digits()
    {
        ContentStreamBuilder content = new();

        _ = content.ShowCode(0).ShowCode(0xABCD).ShowCode(0xFFFF).ShowCode(0x0A1F);

        Assert.Equal("<0000> Tj\n<ABCD> Tj\n<FFFF> Tj\n<0A1F> Tj\n", Encoding.ASCII.GetString(content.ToMemory().Span));
    }

    [Fact]
    public void A_long_stream_grows_past_its_first_buffer_without_changing_a_byte()
    {
        ContentStreamBuilder actual = new();
        ReferenceContentStream expected = new();
        for (int i = 0; i < 5000; i++)
        {
            double x = i * 1.000007;
            _ = actual.MoveTo(x, -x).LineTo(x / 3, 1e-9 * i);
            _ = expected.MoveTo(x, -x).LineTo(x / 3, 1e-9 * i);
        }

        Assert.True(actual.Length > 4096 * 4);
        Assert.Equal(expected.ToArray(), actual.ToMemory().ToArray());
    }

    [Fact]
    public void A_text_position_too_large_to_track_is_written_absolute_and_the_next_one_starts_over()
    {
        // A canvas with a tiny scale can ask for text at 1e14; a running position in hundred-thousandths would overflow there.
        ContentStreamBuilder content = new();

        _ = content.BeginText().TextPlace(10, 20).TextPlace(1e14, -2e14).TextPlace(10.5, 20).TextPlace(11, 20).EndText();

        Assert.Equal("BT\n1 0 0 1 10 20 Tm\n1 0 0 1 100000000000000 -200000000000000 Tm\n1 0 0 1 10.5 20 Tm\n0.5 0 Td\nET\n", Encoding.ASCII.GetString(content.ToMemory().Span));
    }
}
