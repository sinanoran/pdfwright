using System.Globalization;
using System.Text;
using Pdfwright.Pdf;

namespace Pdfwright.Tests.Pdf;

/// <summary>The bytes the objects of the format become: numbers, names, strings, dictionaries.</summary>
public class PdfObjectTests
{
    private static string Serialize(PdfObject value)
    {
        using MemoryStream stream = new();
        PdfSerializer.Write(value, stream);
        return Encoding.Latin1.GetString(stream.ToArray());
    }

    [Theory]
    [InlineData(0.0, "0")]
    [InlineData(1.0, "1")]
    [InlineData(12.5, "12.5")]
    [InlineData(-0.5, "-0.5")]
    [InlineData(0.1 + 0.2, "0.3")]
    [InlineData(1.23456789, "1.23457")]
    [InlineData(0.000001, "0")]
    [InlineData(-0.000001, "0")]
    [InlineData(1e12, "1000000000000")]
    [InlineData(-123456789.125, "-123456789.125")]
    public void A_number_has_no_exponent_no_negative_zero_and_at_most_five_decimals(double value, string expected)
    {
        Assert.Equal(expected, PdfReal.Format(value));
    }

    [Fact]
    public void Negative_zero_is_written_as_zero()
    {
        Assert.Equal("0", PdfReal.Format(double.NegativeZero));
    }

    [Fact]
    public void A_number_is_written_the_same_under_a_culture_that_uses_a_comma()
    {
        CultureInfo before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");

            Assert.Equal("12.5", PdfReal.Format(12.5));
            Assert.Equal("12.5", Serialize(new PdfReal(12.5)));
            Assert.Equal("1234567", Serialize(new PdfInteger(1234567)));
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    [Fact]
    public void A_number_that_is_not_finite_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfReal.Format(double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PdfReal(double.PositiveInfinity));
    }

    [Theory]
    [InlineData("Type", "/Type")]
    [InlineData("A B", "/A#20B")]
    [InlineData("a#b", "/a#23b")]
    [InlineData("a(b)c", "/a#28b#29c")]
    [InlineData("a/b%c", "/a#2Fb#25c")]
    [InlineData("İ", "/#C4#B0")]
    [InlineData("", "/")]
    [InlineData("!~", "/!~")]
    [InlineData("a\u007Fb", "/a#7Fb")]
    [InlineData("a]b{c}", "/a#5Db#7Bc#7D")]
    public void A_name_escapes_what_is_not_a_regular_character(string name, string expected)
    {
        Assert.Equal(expected, Serialize(new PdfName(name)));
    }

    /// <summary>A regular name of up to 127 characters is written without the escaping loop; a longer one goes through it, and both write the same.</summary>
    [Theory]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(300)]
    public void A_long_regular_name_is_written_as_it_is(int length)
    {
        string name = new('N', length);
        Assert.Equal("/" + name, Serialize(new PdfName(name)));
    }

    [Theory]
    [InlineData(0, "0")]
    [InlineData(-42, "-42")]
    [InlineData(long.MaxValue, "9223372036854775807")]
    [InlineData(long.MinValue, "-9223372036854775808")]
    public void An_integer_and_a_reference_are_written_in_decimal(long value, string expected)
    {
        Assert.Equal(expected, Serialize(new PdfInteger(value)));
        Assert.Equal("12345 0 R", Serialize(new PdfReference(12345)));
    }

    [Fact]
    public void A_literal_string_escapes_its_delimiters_and_control_characters()
    {
        PdfString text = new(Encoding.ASCII.GetBytes("a(b)\\c\r\nd"), hex: false);

        Assert.Equal("(a\\(b\\)\\\\c\\r\\nd)", Serialize(text));
        Assert.Equal("(\\000\\377)", Serialize(new PdfString([0x00, 0xFF], hex: false)));
    }

    [Fact]
    public void A_text_string_is_ascii_when_it_can_be_and_utf16_with_a_byte_order_mark_when_it_cannot()
    {
        Assert.Equal("(Report)", Serialize(PdfString.Text("Report")));
        Assert.Equal("<FEFF0130>", Serialize(PdfString.Text("İ")));
        Assert.Equal("<FEFF06450631062D0628064E06270020D83DDE00>", Serialize(PdfString.Text("مرحبَا 😀")));
    }

    [Fact]
    public void A_date_is_in_utc_whatever_the_offset_it_was_given_in()
    {
        PdfString date = PdfString.Date(new DateTimeOffset(2026, 11, 21, 12, 30, 5, TimeSpan.FromHours(3)));

        Assert.Equal("(D:20261121093005Z)", Serialize(date));
    }

    [Fact]
    public void A_dictionary_keeps_the_order_its_keys_were_added_in_and_replaces_a_key_in_place()
    {
        PdfDictionary dictionary = new PdfDictionary().Set("B", 1).Set("A", "x").Set("B", 2);

        Assert.Equal("<<\n/B 2\n/A /x\n>>", Serialize(dictionary));
    }

    [Fact]
    public void An_array_and_a_reference_are_written_in_the_form_a_reader_expects()
    {
        PdfArray array = new(new PdfInteger(1), new PdfReal(2.5), new PdfReference(7), PdfNull.Instance, PdfBoolean.True, new PdfName("N"));

        Assert.Equal("[1 2.5 7 0 R null true /N]", Serialize(array));
    }

    [Fact]
    public void A_stream_is_an_indirect_object_and_cannot_be_written_inline()
    {
        Assert.Throws<InvalidOperationException>(() => Serialize(new PdfStream(new PdfDictionary(), new byte[] { 1 })));
    }
}
