using System.Reflection;

namespace Pdfwright.Tests.Document;

/// <summary>The blocks are immutable records in a closed set.</summary>
public class BlockTests
{
    private sealed record Rogue : Block;

    [Fact]
    public void The_set_of_blocks_is_closed_nobody_outside_the_library_can_make_another()
    {
        ConstructorInfo[] constructors = [.. typeof(Block).GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)];

        Assert.NotEmpty(constructors);
        Assert.DoesNotContain(constructors, c => c.IsPublic);
        Assert.True(typeof(Block).IsAbstract);

        // This test assembly can see the internal constructor, as a friend; the library still refuses a type made outside its own assembly.
        Assert.Throws<InvalidOperationException>(() => new Rogue());
    }

    [Fact]
    public void Every_public_block_is_a_sealed_record_derived_from_block()
    {
        Type[] blocks = [.. typeof(Block).Assembly.GetExportedTypes().Where(t => t != typeof(Block) && typeof(Block).IsAssignableFrom(t))];

        Assert.Equal(["Callout", "Columns", "Drawing", "Heading", "Image", "PageBreak", "Paragraph", "Spacer", "Table"], blocks.Select(t => t.Name).Order());
        Assert.All(blocks, t =>
        {
            Assert.True(t.IsSealed, t.Name);
            Assert.NotNull(t.GetMethod("<Clone>$", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
        });
    }

    [Fact]
    public void A_paragraph_from_a_string_has_one_run_and_the_defaults()
    {
        Paragraph p = new("Hello");

        Assert.Equal("Hello", Assert.Single(p.Runs).Text);
        Assert.Null(p.Runs[0].Style);
        Assert.Equal(TextAlignment.Start, p.Alignment);
        Assert.Null(p.Direction);
        Assert.Null(p.SpaceBefore);
        Assert.Null(p.SpaceAfter);
        Assert.Equal(1.0, p.LineSpacing);
    }

    [Fact]
    public void A_block_does_not_change_when_the_list_it_was_made_from_does()
    {
        List<TextRun> runs = [new TextRun("a")];
        Paragraph p = new(runs);
        Heading h = new(2, runs);
        runs.Add(new TextRun("b"));

        Assert.Single(p.Runs);
        Assert.Single(h.Runs);
    }

    [Fact]
    public void A_record_can_be_copied_with_a_change_and_the_original_stays()
    {
        Paragraph original = new("x");

        Paragraph centered = original with { Alignment = TextAlignment.Center, SpaceAfter = Length.Pt(3) };

        Assert.Equal(TextAlignment.Start, original.Alignment);
        Assert.Equal(TextAlignment.Center, centered.Alignment);
        Assert.Same(original.Runs, centered.Runs);
        Assert.NotEqual(original, centered);
    }

    [Fact]
    public void Constructors_refuse_what_cannot_be_laid_out()
    {
        Assert.Throws<ArgumentNullException>(() => new TextRun(null!));
        Assert.Throws<ArgumentNullException>(() => new Paragraph((string)null!));
        Assert.Throws<ArgumentNullException>(() => new Paragraph((IEnumerable<TextRun>)null!));
        Assert.Throws<ArgumentException>(() => new Paragraph(new TextRun[] { null! }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Spacer(Length.Pt(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Spacer(Length.Pt(double.MaxValue) * 2));
        Assert.Equal(Length.Zero, new Spacer(Length.Zero).Height);
    }

    [Fact]
    public void A_bad_space_or_line_spacing_is_refused_where_it_is_set()
    {
        Assert.Equal("SpaceAfter", Assert.Throws<ArgumentOutOfRangeException>(() => new Paragraph("x") { SpaceAfter = Length.Pt(-2) }).ParamName);
        Assert.Equal("LineSpacing", Assert.Throws<ArgumentOutOfRangeException>(() => new Paragraph("x") { LineSpacing = 0 }).ParamName);
    }

    [Fact]
    public void Line_spacing_makes_lines_further_apart()
    {
        double Gap(double spacing)
        {
            PdfDocument d = new(DocumentTestKit.Options());
            d.Add(new Paragraph("a\nb") { LineSpacing = spacing });
            using UglyToad.PdfPig.PdfDocument read = UglyToad.PdfPig.PdfDocument.Open(DocumentTestKit.Save(d));
            UglyToad.PdfPig.Content.Letter[] letters = [.. read.GetPage(1).Letters];
            return letters[0].StartBaseLine.Y - letters[1].StartBaseLine.Y;
        }

        Assert.Equal(1.5, Gap(1.5) / Gap(1.0), 2);
    }

    [Fact]
    public void Space_before_and_after_are_honoured_between_paragraphs()
    {
        double Gap(Length? after, Length? before)
        {
            PdfDocument d = new(DocumentTestKit.Options());
            d.Add(new Paragraph("a") { SpaceAfter = after ?? Length.Zero });
            d.Add(new Paragraph("b") { SpaceBefore = before });
            using UglyToad.PdfPig.PdfDocument read = UglyToad.PdfPig.PdfDocument.Open(DocumentTestKit.Save(d));
            UglyToad.PdfPig.Content.Letter[] letters = [.. read.GetPage(1).Letters];
            return letters[0].StartBaseLine.Y - letters[1].StartBaseLine.Y;
        }

        double none = Gap(null, null);

        Assert.Equal(10, Gap(Length.Pt(10), null) - none, 3);
        Assert.Equal(7, Gap(null, Length.Pt(7)) - none, 3);
        Assert.Equal(17, Gap(Length.Pt(10), Length.Pt(7)) - none, 3);
    }
}
