using System.Text;
using DrawingBlock = Pdfwright.Drawing;

namespace Pdfwright.Tests.Document;

/// <summary>The findings of the review of the S5c blocks, header and footer, each with the case that showed it.</summary>
public class ReviewS5cTests
{
    // A page of 300 by 200 points with a margin of 10: a content area of 280 by 180.
    private static PdfDocumentOptions Small(Action<DocumentTestKit.OptionsBuilder>? change = null) =>
        DocumentTestKit.Options(b =>
        {
            b.PageSize = new PageSize(Length.Pt(300), Length.Pt(200));
            b.Margins = Margins.Uniform(Length.Pt(10));
            change?.Invoke(b);
        });

    private static PdfDocumentOptions Headed(Action<DocumentTestKit.OptionsBuilder> change) =>
        DocumentTestKit.Options(b =>
        {
            b.PageSize = new PageSize(Length.Pt(300), Length.Pt(200));
            b.Margins = Margins.Pt(30, 10, 30, 10);
            change(b);
        });

    private static byte[] Save(PdfDocumentOptions options, params Block[] blocks)
    {
        PdfDocument document = new(options);
        foreach (Block block in blocks)
        {
            document.Add(block);
        }

        return DocumentTestKit.Save(document);
    }

    private static string Ten() => string.Join('\n', Enumerable.Range(1, 10).Select(i => "line" + i));

    private static PdfImage Jpeg() => PdfImage.FromBytes(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Images", "Files", "gradient-24x16.jpg")));

    // ---- 1. a heading goes with the whole of a block that is kept together

    [Fact]
    public void A_heading_goes_to_the_next_page_with_a_keep_together_paragraph_that_would_not_fit_under_it()
    {
        string[] pages = DocumentTestKit.PageTexts(Save(Small(), new Spacer(Length.Pt(100)), new Heading(2, "Head"), new Paragraph(Ten()) { KeepTogether = true }));

        Assert.Equal(2, pages.Length);
        Assert.DoesNotContain("Head", pages[0]);
        Assert.Contains("Head", pages[1]);
        Assert.Contains("line1", pages[1]);
        Assert.Contains("line10", pages[1]);
    }

    [Fact]
    public void A_heading_goes_to_the_next_page_with_a_keep_together_callout_that_would_not_fit_under_it()
    {
        string[] pages = DocumentTestKit.PageTexts(Save(Small(), new Spacer(Length.Pt(100)), new Heading(2, "Head"), new Callout(new Paragraph(string.Join('\n', Enumerable.Range(1, 8).Select(i => "line" + i)))) { KeepTogether = true }));

        Assert.Equal(2, pages.Length);
        Assert.DoesNotContain("Head", pages[0]);
        Assert.Contains("Head", pages[1]);
        Assert.Contains("line8", pages[1]);
    }

    [Fact]
    public void A_heading_before_a_keep_together_block_that_fills_a_page_does_not_move_forever()
    {
        // Heading and block together are taller than a page: the rule falls back to the first line, as it did.
        string[] pages = DocumentTestKit.PageTexts(Save(Small(), new Paragraph("before"), new Heading(2, "Head"), new Paragraph(string.Join('\n', Enumerable.Range(1, 13).Select(i => "row" + i))) { KeepTogether = true }));

        Assert.Contains("Head", pages[0]);
    }

    // ---- 2. a callout whose padding leaves no room

    [Fact]
    public void A_callout_whose_vertical_padding_is_taller_than_a_page_fails_the_save()
    {
        Callout callout = new(new Paragraph("x")) { Padding = Margins.Pt(100, 10, 100, 10) };

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => Save(Small(), callout));

        Assert.Contains("callout", error.Message);
        Assert.Contains("padding", error.Message);
    }

    [Fact]
    public void A_callout_whose_padding_and_first_block_do_not_fit_a_page_fails_the_save()
    {
        Callout callout = new(new DrawingBlock(Length.Pt(10), Length.Pt(100), _ => { })) { Padding = Margins.Pt(40, 10, 40, 10) };

        Assert.Throws<InvalidOperationException>(() => Save(Small(), callout));
    }

    [Fact]
    public void A_callout_whose_horizontal_padding_leaves_no_width_fails_the_save()
    {
        Callout callout = new(new Paragraph("x")) { Padding = Margins.Pt(1, 140, 1, 139) };

        Assert.Contains("padding", Assert.Throws<InvalidOperationException>(() => Save(Small(), callout)).Message);
    }

    [Fact]
    public void A_callout_with_ordinary_padding_is_still_laid_out()
    {
        Assert.Single(DocumentTestKit.PageTexts(Save(Small(), new Callout(new Paragraph("fine")) { Padding = Margins.Pt(60, 20, 60, 20) })));
    }

    // ---- 3. a gap that is too wide

    [Fact]
    public void A_columns_gap_that_leaves_the_cells_no_width_fails_the_save()
    {
        Columns columns = new([new Paragraph("a"), new Paragraph("b")]) { Gap = Length.Pt(2000) };

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => Save(Small(), columns));

        Assert.Contains("gap", error.Message);
    }

    [Fact]
    public void A_gap_that_leaves_cells_of_four_points_is_accepted_and_three_is_not()
    {
        Assert.Single(DocumentTestKit.PageTexts(Save(Small(), new Columns([new Paragraph("a"), new Paragraph("b")]) { Gap = Length.Pt(272) })));
        Assert.Throws<InvalidOperationException>(() => Save(Small(), new Columns([new Paragraph("a"), new Paragraph("b")]) { Gap = Length.Pt(274) }));
    }

    // ---- 4. a header or a footer that cannot be laid out says where

    [Fact]
    public void A_table_as_a_footer_names_the_page_and_the_footer_and_keeps_the_original_exception()
    {
        PdfDocument document = new(Headed(b => b.Footer = _ => new Table([new TableColumn()], [new TableRow(["x"])])));
        document.Add(new Paragraph("body"));

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => DocumentTestKit.Save(document));

        Assert.Contains("footer of page 1", error.Message);
        Assert.Contains("Table", error.Message);
        Assert.NotNull(error.InnerException);
    }

    [Fact]
    public void A_drawing_that_is_too_tall_for_the_header_of_page_two_names_the_page_and_the_header()
    {
        PdfDocument document = new(Headed(b => b.Header = page => page.Number == 2 ? new DrawingBlock(Length.Pt(10), Length.Pt(200), _ => { }) : null));
        document.Add(new Paragraph("a"));
        document.Add(new PageBreak());
        document.Add(new Paragraph("b"));

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => DocumentTestKit.Save(document));

        Assert.Contains("header of page 2", error.Message);
        Assert.NotNull(error.InnerException);
    }

    // ---- 5. the message of an image's size is about an image

    [Fact]
    public void A_zero_image_width_or_height_does_not_talk_about_a_font()
    {
        ArgumentOutOfRangeException width = Assert.Throws<ArgumentOutOfRangeException>(() => new Image(Jpeg()) { Width = Length.Zero });
        ArgumentOutOfRangeException height = Assert.Throws<ArgumentOutOfRangeException>(() => new Image(Jpeg()) { Height = Length.Pt(-1) });

        Assert.Equal("Width", width.ParamName);
        Assert.Equal("Height", height.ParamName);
        Assert.DoesNotContain("font", width.Message);
        Assert.DoesNotContain("font", height.Message);
        Assert.Contains("image width", width.Message);
        Assert.Contains("font size", Assert.Throws<ArgumentOutOfRangeException>(() => new TextStyle { Size = Length.Zero }).Message);
    }

    // ---- 6. the public canvas names its own parameters

    private static List<ArgumentException> Errors(params Action<DrawingCanvas>[] calls)
    {
        List<ArgumentException> errors = [];
        PdfDocument document = new(Small());
        document.Add(new DrawingBlock(Length.Pt(50), Length.Pt(50), canvas =>
        {
            foreach (Action<DrawingCanvas> call in calls)
            {
                try
                {
                    call(canvas);
                }
                catch (ArgumentException error)
                {
                    errors.Add(error);
                }
            }
        }));
        _ = DocumentTestKit.Save(document);
        return errors;
    }

    [Fact]
    public void The_canvas_reports_the_public_parameter_that_was_wrong()
    {
        PdfImage image = Jpeg();
        List<ArgumentException> errors = Errors(
            c => c.DrawText("a", double.NaN, 0),
            c => c.DrawText("a", 0, double.PositiveInfinity),
            c => c.Scale(0, 1),
            c => c.Scale(1, double.NaN),
            c => c.Translate(double.NaN, 0),
            c => c.Transform(1, 0, 0, 1, double.NaN, 0),
            c => c.DrawImage(image, 0, 0, 0, 5),
            c => c.DrawImage(image, 0, 0, 5, -1),
            c => c.DrawImage(image, double.NaN, 0, 5, 5),
            c => c.DrawImage(image, 0, 0, 5, 5, 2));

        Assert.Equal(["x", "y", "sx", "sy", "dx", "e", "width", "height", "x", "opacity"], errors.Select(e => e.ParamName));
    }

    [Fact]
    public void A_singular_transform_is_refused_without_naming_an_internal_parameter()
    {
        ArgumentException error = Assert.Single(Errors(c => c.Transform(1, 1, 1, 1, 0, 0)));

        Assert.NotEqual("matrix", error.ParamName);
    }

    [Fact]
    public void The_path_builder_reports_the_public_parameter_that_was_wrong()
    {
        Assert.Equal("x", Assert.Throws<ArgumentException>(() => new DrawingPathBuilder().MoveTo(double.NaN, 0)).ParamName);
        Assert.Equal("y", Assert.Throws<ArgumentException>(() => new DrawingPathBuilder().MoveTo(0, double.NaN).LineTo(1, 1)).ParamName);
        Assert.Equal("y3", Assert.Throws<ArgumentException>(() => new DrawingPathBuilder().MoveTo(0, 0).CubicTo(1, 1, 2, 2, 3, double.NaN)).ParamName);
        Assert.Equal("x2", Assert.Throws<ArgumentException>(() => new DrawingPathBuilder().MoveTo(0, 0).QuadraticTo(1, 1, double.NaN, 3)).ParamName);
        Assert.Equal("height", Assert.Throws<ArgumentException>(() => new DrawingPathBuilder().Rectangle(0, 0, 1, double.NaN)).ParamName);
        Assert.Equal("radiusY", Assert.Throws<ArgumentException>(() => new DrawingPathBuilder().RoundedRectangle(0, 0, 5, 5, 1, -1)).ParamName);
        Assert.Equal("rx", Assert.Throws<ArgumentException>(() => new DrawingPathBuilder().Ellipse(0, 0, -1, 1)).ParamName);
        Assert.Equal("sweepDegrees", Assert.Throws<ArgumentException>(() => new DrawingPathBuilder().Arc(0, 0, 1, 1, 0, double.PositiveInfinity)).ParamName);
        Assert.Throws<InvalidOperationException>(() => new DrawingPathBuilder().LineTo(1, 1));
    }

    // ---- 7. the pen's dashes cannot be changed, and pens have value equality

    [Fact]
    public void The_dashes_of_a_pen_are_not_the_array_it_was_given_and_cannot_be_changed_by_a_cast()
    {
        double[] given = [2, 3];
        DrawingPen pen = new(Color.Red, 1, dashes: given);

        given[0] = 99;

        Assert.Equal([2.0, 3.0], pen.Dashes);
        Assert.False(pen.Dashes is double[]);
        Assert.True(pen.Dashes is System.Collections.ObjectModel.ReadOnlyCollection<double>);
    }

    [Fact]
    public void Pens_are_equal_when_their_settings_and_dashes_are_equal()
    {
        DrawingPen a = new(Color.Red, 2, dashes: [1, 2]);
        DrawingPen b = new(Color.Red, 2, dashes: [1.0, 2.0]);

        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, new DrawingPen(Color.Red, 2, dashes: [1, 3]));
        Assert.NotEqual(a, new DrawingPen(Color.Red, 2));
        Assert.NotEqual(a, new DrawingPen(Color.Red, 3, dashes: [1, 2]));
        Assert.True(a != null);
        Assert.Equal(new DrawingPen(Color.Blue), new DrawingPen(Color.Blue));
    }

    // ---- 8. a corner radius larger than the box

    [Fact]
    public void A_corner_radius_larger_than_the_box_gives_circular_corners_not_elliptical_ones()
    {
        // A box 280 wide and about 25 high with a radius of 200: the radius is cut to half the height for both axes.
        string content = Content(Save(Small(), new Callout(new Paragraph("radius")) { CornerRadius = Length.Pt(200), Border = null }));

        // The first curve of the rounded rectangle starts at the end of a straight edge: its horizontal radius is the cut radius (about 15), not 140.
        double[] numbers = [.. System.Text.RegularExpressions.Regex.Matches(content, @"(-?\d+(?:\.\d+)?) (-?\d+(?:\.\d+)?) m\b").Select(m => double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))];
        Assert.NotEmpty(numbers);
        Assert.InRange(numbers[0], 1, 40);
    }

    private static string Content(byte[] pdf) => Encoding.Latin1.GetString(pdf);

    // ---- 9. the exception of a bad picture is public

    [Fact]
    public void A_bad_picture_has_the_reason_in_the_message_and_no_internal_inner_exception()
    {
        ArgumentException error = Assert.Throws<ArgumentException>(() => PdfImage.FromBytes(new byte[] { 1, 2, 3 }));

        Assert.Null(error.InnerException);
        Assert.Equal("bytes", error.ParamName);
        Assert.Contains("JPEG, PNG or WebP", error.Message);
    }

    // ---- 10. a callout that cannot be split and is taller than a page

    [Fact]
    public void A_nested_callout_taller_than_a_page_fails_the_save()
    {
        Callout nested = new(new Callout(new Paragraph(string.Join('\n', Enumerable.Range(1, 60).Select(i => "row" + i)))));

        // Nothing is written; the exception is the one the documentation names.
        Assert.Throws<InvalidOperationException>(() => Save(Small(), nested));
    }

    // ---- a. a drawing takes the direction of the container it is in

    private static double LeftOfText(byte[] pdf, string text)
    {
        using UglyToad.PdfPig.PdfDocument read = UglyToad.PdfPig.PdfDocument.Open(pdf);
        return read.GetPage(1).GetWords().First(w => w.Text.Contains(text)).BoundingBox.Left;
    }

    [Fact]
    public void Text_drawn_in_a_right_to_left_callout_or_columns_sits_at_the_right_of_its_point_by_default()
    {
        // Start alignment at x = 100: left-to-right text begins there and runs right; right-to-left text ends there and runs left.
        static DrawingBlock Label() => new(Length.Pt(280), Length.Pt(30), c => c.DrawText("Label", 100, 20));

        double plain = LeftOfText(Save(Small(), Label()), "Label");
        double inCallout = LeftOfText(Save(Small(), new Callout(Label()) { Direction = TextDirection.RightToLeft, Padding = Margins.Pt(0, 0, 0, 0), Border = null }), "Label");
        double inColumns = LeftOfText(Save(Small(), new Columns([Label()]) { Direction = TextDirection.RightToLeft }), "Label");
        double inRtlDocument = LeftOfText(Save(Small(b => b.Direction = TextDirection.RightToLeft), Label()), "Label");

        Assert.True(inCallout < plain - 5, $"callout {inCallout} plain {plain}");
        Assert.True(inColumns < plain - 5, $"columns {inColumns} plain {plain}");
        Assert.Equal(inRtlDocument, inCallout, 3);
    }

    // ---- c. a page number in a right-to-left document

    private static string FooterText(TextDirection document, Func<int, int, Block> footer)
    {
        byte[] pdf = Save(Headed(b =>
        {
            b.Direction = document;
            b.Footer = page => footer(page.Number, page.Count);
        }), new Paragraph("body"), new PageBreak(), new Paragraph("body"), new PageBreak(), new Paragraph("body"));
        using UglyToad.PdfPig.PdfDocument read = UglyToad.PdfPig.PdfDocument.Open(pdf);

        // The footer as it is seen, from left to right (the order the glyphs are in on the page is not the order of reading).
        return string.Join(' ', read.GetPage(1).GetWords().Where(w => w.BoundingBox.Bottom < 30).OrderBy(w => w.BoundingBox.Left).Select(w => new string([.. w.Text.Where(ch => ch is not ((char)0x034F or (char)0x2066 or (char)0x2069))])).Where(t => t.Length > 0));
    }

    [Fact]
    public void In_a_right_to_left_document_a_page_number_and_count_read_in_bidi_order_unless_kept_as_written()
    {
        string plain = FooterText(TextDirection.RightToLeft, (n, c) => new Paragraph($"{n} / {c}"));
        string isolated = FooterText(TextDirection.RightToLeft, (n, c) => new Paragraph($"\u2066{n} / {c}\u2069"));
        string leftToRight = FooterText(TextDirection.RightToLeft, (n, c) => new Paragraph($"{n} / {c}") { Direction = TextDirection.LeftToRight });
        string ltrDocument = FooterText(TextDirection.LeftToRight, (n, c) => new Paragraph($"{n} / {c}"));

        Assert.Contains("3 / 1", plain); // the numbers swap places in the visual order of a right-to-left paragraph
        Assert.Contains("1 / 3", ltrDocument);
        Assert.Contains("1 / 3", leftToRight);
        Assert.Contains("1 / 3", isolated);
    }
}
