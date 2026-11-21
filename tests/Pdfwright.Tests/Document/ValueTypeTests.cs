using System.Globalization;
using System.Reflection;

namespace Pdfwright.Tests.Document;

/// <summary>Length, PageSize, Margins and Color: the small public values.</summary>
public class ValueTypeTests
{
    [Fact]
    public void Units_convert_through_points()
    {
        Assert.Equal(72, Length.In(1).Points, 9);
        Assert.Equal(72 / 2.54, Length.Cm(1).Points, 9);
        Assert.Equal(72 / 25.4, Length.Mm(1).Points, 9);
        Assert.Equal(12, Length.Pt(12).Points);
        Assert.Equal(25.4, Length.In(1).Millimeters, 9);
        Assert.Equal(2.54, Length.In(1).Centimeters, 9);
        Assert.Equal(1, Length.Pt(72).Inches, 9);
    }

    [Fact]
    public void Length_has_no_implicit_conversion_from_a_number_so_a_unit_is_never_guessed()
    {
        MethodInfo[] implicits = [.. typeof(Length).GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.Name == "op_Implicit")];

        Assert.Empty(implicits);
    }

    [Fact]
    public void Lengths_add_subtract_scale_and_compare()
    {
        Length a = Length.Pt(10);
        Length b = Length.Pt(4);

        Assert.Equal(Length.Pt(14), a + b);
        Assert.Equal(Length.Pt(6), a - b);
        Assert.Equal(Length.Pt(-10), -a);
        Assert.Equal(Length.Pt(20), a * 2);
        Assert.Equal(Length.Pt(20), 2 * a);
        Assert.Equal(Length.Pt(5), a / 2);
        Assert.Equal(2.5, a / b);
        Assert.True(b < a);
        Assert.True(a > b);
        Length same = Length.Pt(10);
        Assert.True(a >= same);
        Assert.True(a <= same);
        Assert.True(a != b);
        Assert.Equal(1, a.CompareTo(b));
        Assert.Equal(Length.Zero, default);
        Assert.Equal(a.GetHashCode(), Length.Pt(10).GetHashCode());
    }

    [Fact]
    public void Length_checks_for_a_finite_value()
    {
        Assert.True(Length.Pt(1e9).IsFinite);
        Assert.False(Length.Pt(double.NaN).IsFinite);
        Assert.False(Length.Pt(double.PositiveInfinity).IsFinite);
        Assert.False((Length.Pt(double.MaxValue) * 2).IsFinite);
    }

    [Fact]
    public void Length_is_written_with_a_period_under_any_culture()
    {
        CultureInfo before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");

            Assert.Equal("12.5pt", Length.Pt(12.5).ToString());
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    [Fact]
    public void Page_sizes_are_the_standard_ones_and_landscape_turns_them()
    {
        Assert.Equal(595.2756, PageSize.A4.Width.Points, 3);
        Assert.Equal(841.8898, PageSize.A4.Height.Points, 3);
        Assert.Equal(419.5276, PageSize.A5.Width.Points, 3);
        Assert.Equal(612, PageSize.Letter.Width.Points, 6);
        Assert.Equal(792, PageSize.Letter.Height.Points, 6);
        Assert.Equal(1008, PageSize.Legal.Height.Points, 6);

        PageSize landscape = PageSize.A4.Landscape();
        Assert.Equal(PageSize.A4.Height, landscape.Width);
        Assert.Equal(PageSize.A4.Width, landscape.Height);
        Assert.Equal(landscape, landscape.Landscape());
    }

    [Fact]
    public void A_custom_page_size_must_be_a_pdf_page()
    {
        PageSize custom = new(Length.Mm(100), Length.Mm(50));

        Assert.Equal(Length.Mm(100), custom.Width);
        Assert.True(custom == new PageSize(Length.Mm(100), Length.Mm(50)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PageSize(Length.Pt(2), Length.Pt(100)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PageSize(Length.Pt(100), Length.Pt(14401)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PageSize(Length.Pt(double.NaN), Length.Pt(100)));
    }

    [Fact]
    public void Margins_keep_their_four_physical_sides()
    {
        Margins m = Margins.Mm(top: 20, right: 15, bottom: 25, left: 10);

        Assert.Equal(Length.Mm(20), m.Top);
        Assert.Equal(Length.Mm(15), m.Right);
        Assert.Equal(Length.Mm(25), m.Bottom);
        Assert.Equal(Length.Mm(10), m.Left);
        Assert.Equal(Margins.Pt(1, 2, 3, 4).Bottom, Length.Pt(3));
        Assert.Equal(Margins.Uniform(Length.Pt(7)), Margins.Pt(7, 7, 7, 7));
        Assert.True(Margins.Pt(1, 2, 3, 4) != Margins.Pt(1, 2, 3, 5));
    }

    [Theory]
    [InlineData("#ff8000", 255, 128, 0)]
    [InlineData("ff8000", 255, 128, 0)]
    [InlineData("#f80", 255, 136, 0)]
    [InlineData("#0000FF", 0, 0, 255)]
    public void A_colour_comes_from_hex(string hex, int red, int green, int blue)
    {
        Color c = Color.FromHex(hex);

        Assert.Equal(red / 255.0, c.R, 9);
        Assert.Equal(green / 255.0, c.G, 9);
        Assert.Equal(blue / 255.0, c.B, 9);
        Assert.False(c.IsGray);
    }

    [Theory]
    [InlineData("")]
    [InlineData("#12")]
    [InlineData("#12345")]
    [InlineData("#gg0000")]
    public void A_colour_that_is_not_hex_is_refused(string hex) => Assert.Throws<ArgumentException>(() => Color.FromHex(hex));

    [Fact]
    public void Colour_components_are_checked_and_gray_is_a_space_of_its_own()
    {
        Assert.Throws<ArgumentException>(() => Color.FromRgb(1.5, 0, 0));
        Assert.Throws<ArgumentException>(() => Color.Gray(double.NaN));
        Assert.True(Color.Black.IsGray);
        Assert.NotEqual(Color.Gray(0.5), Color.FromRgb(0.5, 0.5, 0.5));
        Assert.Equal(Color.FromRgb(255, 0, 0), Color.Red);
        Assert.Equal(1, Color.White.R);
        Assert.Equal(Color.FromRgb((byte)0, (byte)0, (byte)255), Color.Blue);
    }
}
