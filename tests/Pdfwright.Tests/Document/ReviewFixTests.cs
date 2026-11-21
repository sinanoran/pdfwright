using System.Text;
using Pdfwright.Document;
using UglyToad.PdfPig.Content;

namespace Pdfwright.Tests.Document;

/// <summary>What the review of the first public API found, each as a test that failed before it was fixed.</summary>
public class ReviewFixTests
{
    private const string Mixed = "Hello مرحبا नमस्ते হ্যালো வணக்கம் สวัสดี שלום";

    private static PdfDocumentOptions Small(Action<DocumentTestKit.OptionsBuilder>? change = null) =>
        DocumentTestKit.Options(b =>
        {
            b.PageSize = new PageSize(Length.Pt(300), Length.Pt(200));
            b.Margins = Margins.Uniform(Length.Pt(10));
            change?.Invoke(b);
        });

    private sealed class RecordingStream : MemoryStream
    {
        internal int Writes { get; private set; }

        public override void Write(byte[] buffer, int offset, int count)
        {
            Writes++;
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Writes++;
            base.Write(buffer);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Writes++;
            return base.WriteAsync(buffer, cancellationToken);
        }
    }

    // 1. MaxBytes

    [Fact]
    public void A_byte_limit_that_only_the_fonts_exceed_is_still_held_and_the_stream_gets_nothing()
    {
        PdfDocument unlimited = new(DocumentTestKit.Options());
        unlimited.Add(new Paragraph(Mixed));
        Assert.True(DocumentTestKit.Save(unlimited).Length > 20_000, "the test needs a file whose fonts push it over the limit");

        PdfDocument document = new(DocumentTestKit.Options(b => b.Limits = new DocumentLimits { MaxBytes = 20_000 }));
        document.Add(new Paragraph(Mixed));
        using RecordingStream destination = new();

        PdfLimitExceededException error = Assert.Throws<PdfLimitExceededException>(() => document.Save(destination));

        Assert.Equal(DocumentLimit.MaxBytes, error.Limit);
        Assert.Equal(0, destination.Writes);
        Assert.Equal(0, destination.Length);
    }

    [Fact]
    public async Task The_same_byte_limit_holds_for_SaveAsync()
    {
        PdfDocument document = new(DocumentTestKit.Options(b => b.Limits = new DocumentLimits { MaxBytes = 20_000 }));
        document.Add(new Paragraph(Mixed));
        using RecordingStream destination = new();

        PdfLimitExceededException error = await Assert.ThrowsAsync<PdfLimitExceededException>(() => document.SaveAsync(destination, TestContext.Current.CancellationToken));

        Assert.Equal(DocumentLimit.MaxBytes, error.Limit);
        Assert.Equal(0, destination.Writes);
    }

    [Fact]
    public void A_file_within_the_byte_limit_is_written_whole()
    {
        PdfDocument document = new(DocumentTestKit.Options(b => b.Limits = new DocumentLimits { MaxBytes = 1_000_000 }));
        document.Add(new Paragraph(Mixed));
        using MemoryStream destination = new();

        document.Save(destination);

        Assert.Equal(destination.Length, document.Diagnostics.ByteCount);
        Assert.Equal(["Hello"], DocumentTestKit.PageTexts(destination.ToArray()).Select(t => t[..5]));
    }

    // 2. fsType

    private static byte[] PatchFsType(ushort fsType)
    {
        byte[] f = File.ReadAllBytes(TestPaths.Font("NotoSans-Regular.ttf"));
        int numTables = (f[4] << 8) | f[5];
        for (int i = 0; i < numTables; i++)
        {
            int record = 12 + (i * 16);
            if (Encoding.ASCII.GetString(f, record, 4) == "OS/2")
            {
                int offset = (f[record + 8] << 24) | (f[record + 9] << 16) | (f[record + 10] << 8) | f[record + 11];
                f[offset + 8] = (byte)(fsType >> 8);
                f[offset + 9] = (byte)fsType;
            }
        }

        return f;
    }

    [Theory]
    [InlineData(0x0000, true)]
    [InlineData(0x0002, false)]
    [InlineData(0x0004, true)]
    [InlineData(0x0006, true)]
    [InlineData(0x0008, true)]
    [InlineData(0x000A, true)]
    [InlineData(0x0100, false)]
    [InlineData(0x0200, false)]
    [InlineData(0x0106, false)]
    public void The_least_restrictive_usage_bit_decides_whether_a_font_may_be_embedded(int fsType, bool allowed)
    {
        byte[] font = PatchFsType((ushort)fsType);
        FontCollection fonts = new();

        if (allowed)
        {
            Assert.Equal(1, fonts.Add(font).Count);
        }
        else
        {
            FontNotEmbeddableException error = Assert.Throws<FontNotEmbeddableException>(() => fonts.Add(font));
            Assert.Equal("fontData", error.ParamName);
            Assert.Equal(fsType, error.EmbeddingPermission);
        }
    }

    // 3. a heading is kept with the space before what follows

    [Fact]
    public void A_heading_is_kept_with_the_space_before_the_block_after_it()
    {
        PdfDocument document = new(Small());
        for (int i = 0; i < 7; i++)
        {
            document.Add(new Paragraph("line " + i) { SpaceAfter = Length.Zero });
        }

        document.Add(new Heading(3, "H") { SpaceBefore = Length.Zero, SpaceAfter = Length.Zero });
        document.Add(new Paragraph("body") { SpaceBefore = Length.Pt(60) });

        string[] pages = DocumentTestKit.PageTexts(DocumentTestKit.Save(document));

        Assert.Equal(2, pages.Length);
        Assert.DoesNotContain("H", pages[0], StringComparison.Ordinal);
        Assert.Contains("H", pages[1], StringComparison.Ordinal);
        Assert.Contains("body", pages[1], StringComparison.Ordinal);
    }

    [Fact]
    public void A_heading_stays_where_it_is_when_the_space_before_the_next_block_still_fits()
    {
        PdfDocument document = new(Small());
        for (int i = 0; i < 7; i++)
        {
            document.Add(new Paragraph("line " + i) { SpaceAfter = Length.Zero });
        }

        document.Add(new Heading(3, "H") { SpaceBefore = Length.Zero, SpaceAfter = Length.Zero });
        document.Add(new Paragraph("body") { SpaceBefore = Length.Pt(10) });

        Assert.Single(DocumentTestKit.PageTexts(DocumentTestKit.Save(document)));
    }

    // 4. a colour does not split the shaping of a word

    private static StyledLayout Layout(params TextRun[] runs)
    {
        FontCollection.Snapshot fonts = DocumentTestKit.Fonts.Current;
        ResolvedStyle baseStyle = ResolvedStyle.Base(fonts, new TextStyle { Family = "Noto Sans", Size = Length.Pt(10) }, null);
        return BlockText.Layout(runs, baseStyle, fonts, null, TextAlignment.Start, TextDirection.Auto, 1.0);
    }

    [Fact]
    public void An_arabic_word_with_one_letter_in_another_colour_has_the_width_of_the_word_without_it()
    {
        StyledLayout plain = Layout(new TextRun("مرحبا"));
        StyledLayout coloured = Layout(new TextRun("مر"), new TextRun("ح", new TextStyle { Color = Color.Red }), new TextRun("با"));

        Assert.Equal(plain.Layout.Width, coloured.Layout.Width, 6);
        Assert.Equal(plain.Layout.Lines[0].Runs.Count, coloured.Layout.Lines[0].Runs.Count);
        Assert.Equal(
            plain.Layout.Lines[0].Runs.SelectMany(r => r.Run.Glyphs).Select(g => g.Glyph),
            coloured.Layout.Lines[0].Runs.SelectMany(r => r.Run.Glyphs).Select(g => g.Glyph));
    }

    [Fact]
    public void A_ligature_keeps_its_shape_when_one_of_its_letters_is_in_another_colour()
    {
        StyledLayout plain = Layout(new TextRun("office"));
        StyledLayout coloured = Layout(new TextRun("o"), new TextRun("ffi", new TextStyle { Color = Color.Red }), new TextRun("ce"));

        Assert.Equal(plain.Layout.Width, coloured.Layout.Width, 6);
    }

    [Fact]
    public void A_change_of_size_or_language_still_ends_a_shaping_run()
    {
        Assert.Equal(2, Layout(new TextRun("ab"), new TextRun("cd", new TextStyle { Size = Length.Pt(20) })).Layout.Lines[0].Runs.Count);
        Assert.Equal(2, Layout(new TextRun("ab"), new TextRun("cd", new TextStyle { Language = "tr" })).Layout.Lines[0].Runs.Count);
        Assert.Single(Layout(new TextRun("ab"), new TextRun("cd", new TextStyle { Color = Color.Red })).Layout.Lines[0].Runs);
    }

    private static bool IsRed(Letter letter) => letter.Color.ToRGBValues() is (1.0, 0.0, 0.0);

    [Fact]
    public void The_colours_land_on_the_glyphs_of_the_letters_that_have_them()
    {
        PdfDocument document = new(DocumentTestKit.Options());
        document.Add(new Paragraph([new TextRun("مر"), new TextRun("ح", new TextStyle { Color = Color.Red }), new TextRun("با")]));

        using UglyToad.PdfPig.PdfDocument read = UglyToad.PdfPig.PdfDocument.Open(DocumentTestKit.Save(document));
        Letter[] letters = [.. read.GetPage(1).Letters];

        Assert.Contains(letters, l => l.Value == "ح");
        Assert.All(letters, l => Assert.Equal(l.Value == "ح", IsRed(l)));
        Assert.Equal("مرحبا", DocumentTestKit.Strip(string.Concat(letters.Select(l => l.Value))));
    }

    [Fact]
    public void The_colours_land_on_the_right_glyphs_of_latin_and_right_to_left_runs_in_one_line()
    {
        PdfDocument document = new(DocumentTestKit.Options());
        document.Add(new Paragraph([new TextRun("ab "), new TextRun("שלום", new TextStyle { Color = Color.Red }), new TextRun(" cd")]));

        using UglyToad.PdfPig.PdfDocument read = UglyToad.PdfPig.PdfDocument.Open(DocumentTestKit.Save(document));
        Letter[] letters = [.. read.GetPage(1).Letters.Where(l => !string.IsNullOrWhiteSpace(l.Value))];

        Assert.Equal(4, letters.Count(IsRed));
        Assert.All(letters.Where(IsRed), l => Assert.Contains(l.Value, "שלום", StringComparison.Ordinal));
        Assert.All(letters.Where(l => "abcd".Contains(l.Value, StringComparison.Ordinal)), l => Assert.False(IsRed(l)));
    }

    // 5. value equality

    [Fact]
    public void Paragraphs_and_headings_with_the_same_runs_are_equal_and_hash_alike()
    {
        Assert.Equal(new Paragraph("a"), new Paragraph("a"));
        Assert.True(new Paragraph("a") == new Paragraph("a"));
        Assert.Equal(new Paragraph("a").GetHashCode(), new Paragraph("a").GetHashCode());
        Assert.Equal(new Heading(2, "a"), new Heading(2, "a"));
        Assert.Equal(new Heading(2, "a").GetHashCode(), new Heading(2, "a").GetHashCode());

        TextRun[] runs = [new TextRun("a", new TextStyle { Weight = 700 }), new TextRun("b")];
        TextRun[] same = [new TextRun("a", new TextStyle { Weight = 700 }), new TextRun("b")];
        Assert.Equal(new Paragraph(runs) { Alignment = TextAlignment.Center }, new Paragraph(same) { Alignment = TextAlignment.Center });
        Assert.Equal(new Paragraph(runs).GetHashCode(), new Paragraph(same).GetHashCode());
    }

    [Fact]
    public void Paragraphs_and_headings_that_differ_are_not_equal()
    {
        Assert.NotEqual(new Paragraph("a"), new Paragraph("b"));
        Assert.NotEqual(new Paragraph("a"), new Paragraph("a") { Alignment = TextAlignment.End });
        Assert.NotEqual(new Paragraph("a"), new Paragraph("a") { LineSpacing = 2 });
        Assert.NotEqual(new Paragraph("a"), new Paragraph("a") { SpaceBefore = Length.Pt(1) });
        Assert.NotEqual(new Paragraph([new TextRun("a"), new TextRun("b")]), new Paragraph("ab"));
        Assert.NotEqual(new Heading(1, "a"), new Heading(2, "a"));
        Assert.NotEqual(new Heading(1, "a"), new Heading(1, "a") { Direction = TextDirection.RightToLeft });
        Assert.NotEqual<Block>(new Paragraph("a"), new Heading(1, "a"));
    }

    [Fact]
    public void The_runs_of_a_paragraph_cannot_be_changed_through_the_list()
    {
        Paragraph paragraph = new("a");

        Assert.IsAssignableFrom<IList<TextRun>>(paragraph.Runs);
        Assert.Throws<NotSupportedException>(() => ((IList<TextRun>)paragraph.Runs)[0] = new TextRun("b"));
    }

    // 6. and 7. early validation with the public names

    [Fact]
    public void A_bad_value_is_refused_where_it_is_set_and_names_the_public_property()
    {
        Assert.Equal("LineSpacing", Assert.Throws<ArgumentOutOfRangeException>(() => new Paragraph("x") { LineSpacing = -1 }).ParamName);
        Assert.Equal("LineSpacing", Assert.Throws<ArgumentOutOfRangeException>(() => new Paragraph("x") { LineSpacing = double.NaN }).ParamName);
        Assert.Equal("SpaceBefore", Assert.Throws<ArgumentOutOfRangeException>(() => new Paragraph("x") { SpaceBefore = Length.Pt(double.MaxValue) * 2 }).ParamName);
        Assert.Equal("SpaceAfter", Assert.Throws<ArgumentOutOfRangeException>(() => new Paragraph("x") { SpaceAfter = Length.Pt(-1) }).ParamName);
        Assert.Equal("SpaceBefore", Assert.Throws<ArgumentOutOfRangeException>(() => new Heading(1, "x") { SpaceBefore = Length.Pt(-1) }).ParamName);
        Assert.Equal("SpaceAfter", Assert.Throws<ArgumentOutOfRangeException>(() => new Heading(1, "x") { SpaceAfter = Length.Pt(double.MaxValue) * 2 }).ParamName);
        Assert.Equal("Size", Assert.Throws<ArgumentOutOfRangeException>(() => new TextStyle { Size = Length.Pt(-3) }).ParamName);
        Assert.Equal("Size", Assert.Throws<ArgumentOutOfRangeException>(() => new TextStyle { Size = Length.Zero }).ParamName);
        Assert.Equal("Weight", Assert.Throws<ArgumentOutOfRangeException>(() => new TextStyle { Weight = 0 }).ParamName);
        Assert.Equal("Weight", Assert.Throws<ArgumentOutOfRangeException>(() => new TextStyle { Weight = 1001 }).ParamName);
        Assert.Equal("MaxPages", Assert.Throws<ArgumentOutOfRangeException>(() => new DocumentLimits { MaxPages = 0 }).ParamName);
        Assert.Equal("MaxBytes", Assert.Throws<ArgumentOutOfRangeException>(() => new DocumentLimits { MaxBytes = 0 }).ParamName);
        Assert.Equal("Margins", Assert.Throws<ArgumentException>(() => new PdfDocumentOptions { Fonts = DocumentTestKit.Fonts, Margins = Margins.Pt(-1, 0, 0, 0) }).ParamName);
        Assert.Equal("Fonts", Assert.Throws<ArgumentNullException>(() => new PdfDocumentOptions { Fonts = null! }).ParamName);
        Assert.Equal("Limits", Assert.Throws<ArgumentNullException>(() => new PdfDocumentOptions { Fonts = DocumentTestKit.Fonts, Limits = null! }).ParamName);
    }

    [Fact]
    public void A_default_style_of_size_zero_is_refused_before_the_document_exists()
    {
        Assert.Equal("Size", Assert.Throws<ArgumentOutOfRangeException>(() => new PdfDocumentOptions { Fonts = DocumentTestKit.Fonts, DefaultStyle = new TextStyle { Size = Length.Zero } }).ParamName);
    }

    [Fact]
    public void A_value_that_is_not_in_an_enumeration_is_refused_and_names_the_property()
    {
        Assert.Equal("Alignment", Assert.Throws<ArgumentOutOfRangeException>(() => new Paragraph("x") { Alignment = (TextAlignment)7 }).ParamName);
        Assert.Equal("Alignment", Assert.Throws<ArgumentOutOfRangeException>(() => new Heading(1, "x") { Alignment = (TextAlignment)(-1) }).ParamName);
        Assert.Equal("Direction", Assert.Throws<ArgumentOutOfRangeException>(() => new Paragraph("x") { Direction = (TextDirection)9 }).ParamName);
        Assert.Equal("Direction", Assert.Throws<ArgumentOutOfRangeException>(() => new Heading(1, "x") { Direction = (TextDirection)9 }).ParamName);
        Assert.Equal("Direction", Assert.Throws<ArgumentOutOfRangeException>(() => new PdfDocumentOptions { Fonts = DocumentTestKit.Fonts, Direction = (TextDirection)9 }).ParamName);
        Assert.Equal("direction", Assert.Throws<ArgumentOutOfRangeException>(() => new TextMeasurer(DocumentTestKit.Fonts).Measure("x", null, null, (TextDirection)9)).ParamName);
        Assert.Equal(TextAlignment.End, new Paragraph("x") { Alignment = TextAlignment.End }.Alignment);
        Assert.Null(new Paragraph("x") { Direction = null }.Direction);
    }

    [Fact]
    public void A_page_size_that_was_never_set_says_so()
    {
        ArgumentException error = Assert.Throws<ArgumentException>(() => new PdfDocumentOptions { Fonts = DocumentTestKit.Fonts, PageSize = default });

        Assert.Equal("PageSize", error.ParamName);
        Assert.Contains("not set", error.Message, StringComparison.Ordinal);
    }

    // 8. HeadingStyles is copied

    [Fact]
    public void The_heading_styles_are_copied_so_the_callers_list_cannot_break_a_save()
    {
        List<TextStyle> styles = [.. Enumerable.Range(0, 6).Select(_ => new TextStyle { Weight = 700 })];
        PdfDocumentOptions options = DocumentTestKit.Options(b => b.HeadingStyles = styles);
        PdfDocument document = new(options);
        styles.RemoveAt(5);
        styles[0] = new TextStyle { Size = Length.Pt(99) };
        document.Add(new Heading(1, "x"));

        using UglyToad.PdfPig.PdfDocument read = UglyToad.PdfPig.PdfDocument.Open(DocumentTestKit.Save(document));

        Assert.Equal(6, options.HeadingStyles!.Count);
        Assert.Equal(10, read.GetPage(1).Letters.Single().PointSize, 3);
        Assert.Throws<NotSupportedException>(() => ((IList<TextStyle>)options.HeadingStyles!)[0] = new TextStyle());
    }

    [Fact]
    public void Heading_styles_that_are_not_six_or_hold_a_null_are_refused_where_they_are_set()
    {
        Assert.Equal("HeadingStyles", Assert.Throws<ArgumentException>(() => new PdfDocumentOptions { Fonts = DocumentTestKit.Fonts, HeadingStyles = [new TextStyle()] }).ParamName);
        Assert.Equal("HeadingStyles", Assert.Throws<ArgumentException>(() => new PdfDocumentOptions { Fonts = DocumentTestKit.Fonts, HeadingStyles = [.. Enumerable.Range(0, 6).Select(_ => (TextStyle)null!)] }).ParamName);
    }

    // 9. Creation and Deterministic

    [Fact]
    public void A_creation_date_is_written_whenever_it_is_set_and_a_deterministic_document_without_one_has_no_date()
    {
        DateTimeOffset date = new(2001, 2, 3, 4, 5, 6, TimeSpan.Zero);

        PdfDocument free = new(DocumentTestKit.Options(b => { b.Deterministic = false; b.Creation = date; }));
        free.Add(new Paragraph("x"));
        Assert.Contains("D:20010203040506", Encoding.Latin1.GetString(DocumentTestKit.Save(free)), StringComparison.Ordinal);

        PdfDocument fixedDate = new(DocumentTestKit.Options(b => { b.Deterministic = true; b.Creation = date; }));
        fixedDate.Add(new Paragraph("x"));
        Assert.Contains("D:20010203040506", Encoding.Latin1.GetString(DocumentTestKit.Save(fixedDate)), StringComparison.Ordinal);

        PdfDocument none = new(DocumentTestKit.Options(b => b.Deterministic = true));
        none.Add(new Paragraph("x"));
        Assert.DoesNotContain("CreationDate", Encoding.Latin1.GetString(DocumentTestKit.Save(none)), StringComparison.Ordinal);

        PdfDocument now = new(DocumentTestKit.Options(b => b.Deterministic = false));
        now.Add(new Paragraph("x"));
        Assert.Contains("CreationDate", Encoding.Latin1.GetString(DocumentTestKit.Save(now)), StringComparison.Ordinal);
    }

    // a. nothing a user makes is made by the user

    [Fact]
    public void The_types_a_user_only_reads_have_no_public_constructor()
    {
        Assert.Empty(typeof(DocumentDiagnostics).GetConstructors());
        Assert.Empty(typeof(MissingGlyph).GetConstructors());
        Assert.Empty(typeof(TextMeasurement).GetConstructors());
        Assert.Empty(typeof(PdfLimitExceededException).GetConstructors());
        Assert.Empty(typeof(FontNotEmbeddableException).GetConstructors());
    }

    [Fact]
    public void The_page_limit_is_named_by_an_enumeration()
    {
        PdfDocument document = new(DocumentTestKit.Options(b => b.Limits = new DocumentLimits { MaxPages = 1 }));
        document.Add(new Paragraph("a")).Add(new PageBreak()).Add(new Paragraph("b"));

        PdfLimitExceededException error = Assert.Throws<PdfLimitExceededException>(() => DocumentTestKit.Save(document));

        Assert.Equal(DocumentLimit.MaxPages, error.Limit);
        Assert.Equal(1, error.Maximum);
    }

    // c. a family that is not in the collection leaves a trace

    [Fact]
    public void A_family_that_the_collection_does_not_have_is_reported_once_and_the_text_is_still_drawn()
    {
        PdfDocument document = new(DocumentTestKit.Options());
        document.Add(new Paragraph("one") { Style = new TextStyle { Family = "Nope Sans" } });
        document.Add(new Paragraph([new TextRun("two", new TextStyle { Family = "nope sans" }), new TextRun("three", new TextStyle { Family = "Other" })]));
        document.Add(new Heading(1, [new TextRun("four", new TextStyle { Family = "noto sans" })]));

        string[] pages = DocumentTestKit.PageTexts(DocumentTestKit.Save(document));

        Assert.Equal(["Nope Sans", "Other"], document.Diagnostics.MissingFamilies);
        Assert.Contains("one", pages[0], StringComparison.Ordinal);
        Assert.Contains("four", pages[0], StringComparison.Ordinal);
    }

    [Fact]
    public void A_document_that_names_only_the_families_it_has_reports_none()
    {
        PdfDocument document = new(DocumentTestKit.Options());
        document.Add(new Paragraph("x"));
        DocumentTestKit.Save(document);

        Assert.Empty(document.Diagnostics.MissingFamilies);
        Assert.Empty(DocumentDiagnostics.Empty.MissingFamilies);
    }

    // d. the measurer can match a document

    [Fact]
    public void A_measurer_can_be_given_a_line_spacing_and_a_language()
    {
        TextMeasurer single = new(DocumentTestKit.Fonts, new TextStyle { Family = "Noto Sans", Size = Length.Pt(10) });
        TextMeasurer doubled = new(DocumentTestKit.Fonts, new TextStyle { Family = "Noto Sans", Size = Length.Pt(10) }, lineSpacing: 2.0);

        Assert.Equal(2.0, doubled.Measure("a").Height / single.Measure("a").Height, 6);

        TextMeasurer inLanguage = new(DocumentTestKit.Fonts, new TextStyle { Family = "Noto Sans" }, "tr");
        TextMeasurer styled = new(DocumentTestKit.Fonts, new TextStyle { Family = "Noto Sans", Language = "tr" });
        Assert.Equal(styled.Measure("İstanbul ışığı").Width, inLanguage.Measure("İstanbul ışığı").Width);

        Assert.Equal("lineSpacing", Assert.Throws<ArgumentOutOfRangeException>(() => new TextMeasurer(DocumentTestKit.Fonts, null, null, 0)).ParamName);
    }

    // e. a spacer is intent

    [Fact]
    public void A_spacer_after_a_page_break_moves_the_text_of_the_new_page_down()
    {
        double Y(params Block[] blocks)
        {
            PdfDocument document = new(DocumentTestKit.Options());
            foreach (Block block in blocks)
            {
                document.Add(block);
            }

            using UglyToad.PdfPig.PdfDocument read = UglyToad.PdfPig.PdfDocument.Open(DocumentTestKit.Save(document));
            return read.GetPage(2).Letters.Single().StartBaseLine.Y;
        }

        double without = Y(new Paragraph("A"), new PageBreak(), new Paragraph("B"));
        double with = Y(new Paragraph("A"), new PageBreak(), new Spacer(Length.Pt(50)), new Paragraph("B"));

        Assert.Equal(50, without - with, 3);
    }

    [Fact]
    public void A_document_of_a_spacer_alone_is_one_page_and_a_spacer_the_height_of_the_page_pushes_text_to_the_next()
    {
        PdfDocument only = new(Small());
        only.Add(new Spacer(Length.Pt(50)));
        Assert.Single(DocumentTestKit.PageTexts(DocumentTestKit.Save(only)));

        PdfDocument tall = new(Small());
        tall.Add(new Spacer(Length.Pt(5000))).Add(new Paragraph("B"));
        Assert.Equal([string.Empty, "B"], DocumentTestKit.PageTexts(DocumentTestKit.Save(tall)));
    }

    // f. a font collection is disposable

    [Fact]
    public void A_disposed_font_collection_cannot_be_used_and_disposing_twice_is_harmless()
    {
        FontCollection fonts = DocumentTestKit.Collection("NotoSans-Regular.ttf");
        PdfDocument document = new(DocumentTestKit.Options(b => b.Fonts = fonts));
        document.Add(new Paragraph("x"));
        TextMeasurer measurer = new(fonts);
        Assert.NotEmpty(DocumentTestKit.Save(document));

        fonts.Dispose();
        fonts.Dispose();

        Assert.Throws<ObjectDisposedException>(() => DocumentTestKit.Save(document));
        Assert.Throws<ObjectDisposedException>(() => measurer.Measure("x"));
        Assert.Throws<ObjectDisposedException>(() => fonts.Add(File.ReadAllBytes(TestPaths.Font("NotoSans-Bold.ttf"))));
        Assert.IsAssignableFrom<IDisposable>(fonts);
    }

    [Fact]
    public async Task A_disposed_font_collection_fails_SaveAsync_too()
    {
        FontCollection fonts = DocumentTestKit.Collection("NotoSans-Regular.ttf");
        PdfDocument document = new(DocumentTestKit.Options(b => b.Fonts = fonts));
        document.Add(new Paragraph("x"));
        fonts.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => document.SaveAsync(new MemoryStream(), TestContext.Current.CancellationToken));
        Assert.Equal(DocumentDiagnostics.Empty, document.Diagnostics);
    }

    // g. a length is a finite number

    [Fact]
    public void A_length_cannot_be_made_from_a_number_that_is_not_finite()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Length.Pt(double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => Length.Mm(double.PositiveInfinity));
        Assert.Throws<ArgumentOutOfRangeException>(() => Length.Cm(double.NegativeInfinity));
        Assert.Throws<ArgumentOutOfRangeException>(() => Length.In(double.NaN));
    }

    [Fact]
    public void Equality_of_lengths_agrees_with_Equals_and_the_hash_even_for_a_length_that_arithmetic_made_not_a_number()
    {
        Length infinite = Length.Pt(double.MaxValue) * 2;
        Length notANumber = infinite - infinite;
        Length other = infinite - infinite;

        Assert.False(notANumber.IsFinite);
        Assert.True(notANumber.Equals(other));
        Assert.True(notANumber == other);
        Assert.False(notANumber != other);
        Assert.Equal(notANumber.GetHashCode(), other.GetHashCode());
    }
}
