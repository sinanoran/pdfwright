using System.Globalization;
using System.Text;
using Pdfwright.Pdf;

namespace Pdfwright.Tests.Pdf;

/// <summary>
/// The number writers have a fast path that skips the custom format of <see cref="double"/>. These tests hold it to the custom format itself (the
/// original code, <c>ToString("0.#####")</c> with "-0" turned into "0"), over every input class the fast path takes and the edges where it hands over.
/// </summary>
public class NumberFormatEquivalenceTests
{
    private static string Old(double value)
    {
        string text = value.ToString("0.#####", CultureInfo.InvariantCulture);
        return text == "-0" ? "0" : text;
    }

    private static string Fast(double value)
    {
        byte[] buffer = new byte[400];
        return Encoding.ASCII.GetString(buffer, 0, PdfReal.Write(value, buffer));
    }

    private static string Scaled(long count)
    {
        byte[] buffer = new byte[400];
        return Encoding.ASCII.GetString(buffer, 0, PdfReal.WriteScaled(count, buffer));
    }

    [Fact]
    public void Every_count_of_hundred_thousandths_near_zero_is_written_as_the_custom_format_wrote_it()
    {
        for (long count = -2_000_000; count <= 2_000_000; count++)
        {
            string expected = Old(count / 100000.0);
            Assert.Equal(expected, Scaled(count));
            Assert.Equal(expected, Fast(count / 100000.0));
        }
    }

    [Fact]
    public void Counts_of_every_magnitude_up_to_the_limit_and_past_it_are_written_as_the_custom_format_wrote_them()
    {
        Random random = new(11);
        List<long> counts = [];
        foreach (long edge in new long[] { 999_999_999_999_999, 1_000_000_000_000_000, 999_999_999_999_998, 1_000_000_000_000_001, 100_000_000_000_000, 99_999_999_999_999, long.MaxValue, long.MinValue + 1 })
        {
            counts.Add(edge);
            counts.Add(-edge);
        }

        for (int i = 0; i < 1_000_000; i++)
        {
            int digits = random.Next(1, 20);
            long magnitude = random.NextInt64(0, digits == 19 ? long.MaxValue : (long)Math.Pow(10, digits));
            counts.Add(random.Next(2) == 0 ? magnitude : -magnitude);
        }

        foreach (long count in counts)
        {
            Assert.Equal(Old(count / 100000.0), Scaled(count));
        }
    }

    [Fact]
    public void Doubles_of_every_size_and_the_ones_around_the_rounding_boundaries_are_written_as_the_custom_format_wrote_them()
    {
        Random random = new(12);
        List<double> values = [0, -0.0, 1e15, -1e15, 999_999_999_999_999, -999_999_999_999_999, 1e15 + 2, 1e12, 1e13, 1e-7, 0.000005, -0.000005, double.MaxValue, double.Epsilon];
        for (int i = 0; i < 3_000_000; i++)
        {
            double mantissa = random.NextDouble() * (random.Next(2) == 0 ? 1 : -1);
            values.Add(mantissa * Math.Pow(10, random.Next(-7, 14)));
            values.Add(Math.Round(mantissa * 1e6, random.Next(0, 3)));
            long k = random.NextInt64(-100_000_000, 100_000_000);
            foreach (double centre in new[] { k / 100000.0, (k + 0.5) / 100000.0 })
            {
                long bits = BitConverter.DoubleToInt64Bits(centre);
                values.Add(centre);
                values.Add(BitConverter.Int64BitsToDouble(bits + 1));
                values.Add(BitConverter.Int64BitsToDouble(bits - 1));
            }

            values.Add(random.NextInt64(-1_100_000_000_000_000, 1_100_000_000_000_000)); // whole numbers across the limit
        }

        foreach (double value in values)
        {
            Assert.Equal(Old(value), Fast(value));
        }
    }

    [Fact]
    public void The_largest_whole_numbers_of_the_fast_path_and_the_first_beyond_it_are_written_as_the_custom_format_wrote_them()
    {
        foreach (double value in new[] { 999_999_999_999_999.0, 1e15, 1e15 + 2, 999_999_999_999_998.0, 1e16, 123_456_789_012_345.0 })
        {
            Assert.Equal(Old(value), Fast(value));
            Assert.Equal(Old(-value), Fast(-value));
        }
    }

    [Fact]
    public void Text_placed_with_a_line_position_is_the_bytes_the_division_of_the_counts_wrote()
    {
        Random random = new(13);
        ContentStreamBuilder content = new();
        StringBuilder expected = new();
        long lineX = 0, lineY = 0;
        bool known = false;
        _ = content.BeginText();
        expected.Append("BT\n");
        for (int i = 0; i < 200_000; i++)
        {
            double scale = random.Next(5) switch { 0 => 1, 1 => 1e3, 2 => 1e8, 3 => 1e-3, _ => 1e10 };
            double x = ((random.NextDouble() * 2) - 1) * 800 * scale;
            double y = ((random.NextDouble() * 2) - 1) * 800 * scale;
            if (i % 7 == 0)
            {
                x = Math.Round(x, 2);
                y = Math.Round(y, 3);
            }

            _ = content.TextPlace(x, y);
            if (Math.Abs(x) >= 1e13 || Math.Abs(y) >= 1e13)
            {
                expected.Append(CultureInfo.InvariantCulture, $"{Old(1)} {Old(0)} {Old(0)} {Old(1)} {Old(x)} {Old(y)} Tm\n");
                known = false;
                continue;
            }

            long tx = (long)Math.Round(x * 100000, MidpointRounding.AwayFromZero);
            long ty = (long)Math.Round(y * 100000, MidpointRounding.AwayFromZero);
            if (!known)
            {
                expected.Append(CultureInfo.InvariantCulture, $"1 0 0 1 {Old(tx / 100000.0)} {Old(ty / 100000.0)} Tm\n");
                known = true;
            }
            else
            {
                expected.Append(CultureInfo.InvariantCulture, $"{Old((tx - lineX) / 100000.0)} {Old((ty - lineY) / 100000.0)} Td\n");
            }

            lineX = tx;
            lineY = ty;
        }

        Assert.Equal(expected.ToString(), Encoding.ASCII.GetString(content.ToMemory().Span));
    }
}
