using System.Buffers;
using Pdfwright.Pdf;
using Pdfwright.Tests.Conformance;
using Pdfwright.Tests.Document;
using Pdfwright.Text;
using Pdfwright.Unicode;

namespace Pdfwright.Tests.Text;

/// <summary>
/// The working arrays of a layout (the breakers' scratch arrays and the line breaker's State) are rented from the shared array pool and given back, and the content buffer of a finished
/// page is kept for the next page of a save. None of it may change a result: a rented array holds whatever its last user left, and a buffer holds the bytes of the page before.
/// </summary>
public class LayoutStateReuseTests
{
    private static readonly string[] Pieces =
    [
        "hello", "World", "Row 12 cell 3 text", "price: $9.99", "(a [b] {c})", " ", "-", "1,234.5", "é", "straße", "مرحبا", "العربية", "123", "שלום", "😀", "日本語", "नमस्ते", "สวัสดี", "ก็",
        "—", "“q”", "­", "Á̂", "‍", "a\u2028b", "́", "\U0001F1E9\U0001F1EA", "ㅎㅏㄴ",
    ];

    private static string RandomText(Random random) =>
        string.Concat(Enumerable.Range(0, 1 + random.Next(14)).Select(_ => Pieces[random.Next(Pieces.Length)] + (random.Next(3) > 0 ? " " : string.Empty)));

    /// <summary>
    /// Fills pooled arrays of every type the layout rents with values that would change a result if they were read, and gives them back. A dozen arrays of each size are held at once before any is
    /// returned, so that the pool's per-core stacks are filled as well as the slot of this thread: a single rent and return only dirties that slot, which the next rent on the thread takes first.
    /// It fills only arrays it rented and gives every one back, so it leaves nothing of the pool's state corrupt for tests of other classes that run at the same time.
    /// </summary>
    private static void Poison()
    {
        static void Fill<T>(T value)
        {
            for (int size = 8; size <= ScratchArray.PoolLimit; size *= 2)
            {
                T[][] held = new T[12][];
                for (int k = 0; k < held.Length; k++)
                {
                    held[k] = ArrayPool<T>.Shared.Rent(size);
                    Array.Fill(held[k], value);
                }

                foreach (T[] array in held)
                {
                    ArrayPool<T>.Shared.Return(array);
                }
            }
        }

        Fill(1e9);
        Fill(true);
        Fill(12345);
        Fill(LineBreakKind.Mandatory);
        Fill(LineBreakClass.BK);
        Fill(CharacterProperties.Of(0x1F600));
        Fill((byte)7);
        Fill(BidiClass.R);
    }

    [Fact]
    public void The_breakers_give_the_same_answer_in_a_dirty_longer_array_as_in_a_new_one()
    {
        Random random = new(20261122);
        for (int i = 0; i < 500; i++)
        {
            int[] text = UnicodeText.CodePoints(RandomText(random), out _);
            LineBreakKind[] expected = UnicodeLineBreaker.Breaks(text);
            LineBreakKind[] dirty = new LineBreakKind[text.Length + 1 + random.Next(40)];
            Array.Fill(dirty, LineBreakKind.Mandatory);
            UnicodeLineBreaker.Breaks(text, dirty);
            Assert.True(expected.AsSpan().SequenceEqual(dirty.AsSpan(0, text.Length + 1)));

            bool[] graphemes = UnicodeGraphemeBreaker.Boundaries(text);
            bool[] dirtyGraphemes = new bool[text.Length + 1 + random.Next(40)];
            Array.Fill(dirtyGraphemes, true);
            UnicodeGraphemeBreaker.Boundaries(text, dirtyGraphemes);
            Assert.True(graphemes.AsSpan().SequenceEqual(dirtyGraphemes.AsSpan(0, text.Length + 1)));
        }
    }

    [Fact]
    public void The_breakers_refuse_an_array_that_is_too_short()
    {
        Assert.Throws<ArgumentException>(() => UnicodeLineBreaker.Breaks([65, 66], new LineBreakKind[2]));
        Assert.Throws<ArgumentException>(() => UnicodeGraphemeBreaker.Boundaries([65, 66], new bool[2]));
    }

    [Fact]
    public void A_layout_is_the_same_whatever_the_pooled_arrays_hold()
    {
        ShapingFont[] fonts = LayoutTestKit.Fonts(LayoutTestKit.Latin, LayoutTestKit.Arabic, LayoutTestKit.Hebrew);
        Random random = new(20261123);
        List<(string Text, double? Width, int Level)> cases = [];
        foreach (CorpusTests.Case c in CorpusTests.Load())
        {
            cases.Add((c.Text, 120, -1));
            cases.Add((c.Text, null, 0));
        }

        for (int i = 0; i < 300; i++)
        {
            cases.Add((RandomText(random), random.Next(3) == 0 ? null : 30 + random.Next(300), new[] { -1, 0, 1 }[random.Next(3)]));
        }

        // Cells of a table: printable ASCII, which the shaper's simple path rents its paragraph's arrays for.
        foreach (string cell in new[] { "Row 12 cell 3 text", "Some longer text of a cell that wraps over a few lines in a narrow column", "  padded  cell  ", "a\tb c", "x" })
        {
            cases.Add((cell, 60, -1));
            cases.Add((cell, null, 0));
            cases.Add((cell, 25, 1));
        }

        List<string> expected = [.. cases.Select(c => Describe(LayoutTestKit.Layout(c.Text, fonts, c.Width, paragraphLevel: c.Level)))];
        for (int round = 0; round < 3; round++)
        {
            Poison();
            for (int i = 0; i < cases.Count; i++)
            {
                // A different text in between leaves other contents in the arrays the next layout rents.
                _ = LayoutTestKit.Layout(cases[((i * 7) + 3) % cases.Count].Text, fonts, 60, paragraphLevel: -1);
                Assert.Equal(expected[i], Describe(LayoutTestKit.Layout(cases[i].Text, fonts, cases[i].Width, paragraphLevel: cases[i].Level)));
            }
        }
    }

    private static string Describe(ParagraphLayout layout) =>
        string.Concat(layout.Lines.Select(line =>
            System.FormattableString.Invariant($"<{line.Start},{line.Length},{line.Width:R},{line.X:R},{line.Ascent:R},{line.Descent:R},{line.Pitch:R}")
            + string.Concat(line.Runs.Select(r => System.FormattableString.Invariant($"({r.Run.Utf16Start},{r.Run.Utf16Length},{r.Run.FontIndex},{r.X:R},{r.Width:R},{r.IsTrailingSpace})")))
            + ">"))
        + "|" + string.Join(',', layout.Missing);

    [Fact]
    public void Documents_saved_at_the_same_time_on_many_threads_are_the_same_bytes_as_saved_one_after_the_other()
    {
        List<(PageCorpus Corpus, bool IsLong)> jobs = [.. PageCorpus.LoadAll().SelectMany(c => new[] { (c, false), (c, true) })];
        byte[][] serial = [.. jobs.Select(j => DocumentTestKit.Save(j.Corpus.Build(j.IsLong).Document))];

        // Every job runs three times, and eight threads at once, so that threads lay out and write the same and different documents together.
        ParallelOptions options = new() { MaxDegreeOfParallelism = 8, CancellationToken = TestContext.Current.CancellationToken };
        Parallel.For(0, jobs.Count * 3, options, n =>
        {
            int i = n % jobs.Count;
            byte[] bytes = DocumentTestKit.Save(jobs[i].Corpus.Build(jobs[i].IsLong).Document);
            Assert.True(bytes.AsSpan().SequenceEqual(serial[i]), $"{jobs[i].Corpus.Group} ({(jobs[i].IsLong ? "long" : "short")}) differs when saved in parallel");
        });
    }

    [Fact]
    public void A_document_saved_twice_gives_the_same_bytes_after_a_dense_page_and_a_sparse_one()
    {
        // The content buffer of a finished page is emptied and used for the next: a dense page followed by a nearly empty one must not leave bytes of the first in the second.
        PdfDocument document = new(DocumentTestKit.Options(o => o.Compress = false));
        document.Add(new Heading(1, "Dense"));
        for (int i = 0; i < 40; i++)
        {
            document.Add(new Paragraph($"Line {i} of a dense page with text to make the content of the page long enough to grow its buffer: " + DocumentTestKit.LongText(60, "w" + i + "_")));
        }

        document.Add(new PageBreak());
        document.Add(new Paragraph("Short."));
        byte[] first = DocumentTestKit.Save(document);
        byte[] second = DocumentTestKit.Save(document);
        Assert.True(first.AsSpan().SequenceEqual(second));

        string[] pages = DocumentTestKit.PageTexts(first);
        Assert.Equal("Short.", pages[^1].Trim());
        Assert.True(pages.Length > 2);
    }

    [Fact]
    public void A_scratch_array_above_the_pool_limit_is_allocated_and_never_pooled()
    {
        long[] big = ScratchArray.Rent<long>(ScratchArray.PoolLimit + 1);
        Assert.Equal(ScratchArray.PoolLimit + 1, big.Length);
        ScratchArray.Return(big);

        // The pool of this thread would hand the array it was given back to the next rent of its size.
        long[] again = ScratchArray.Rent<long>(ScratchArray.PoolLimit + 1);
        Assert.NotSame(big, again);
        Assert.All(again, v => Assert.Equal(0L, v));
        ScratchArray.Return(again);
    }

    [Fact]
    public void A_scratch_array_up_to_the_pool_limit_comes_from_the_pool_and_goes_back_to_it()
    {
        foreach (int length in new[] { 1, 100, ScratchArray.PoolLimit })
        {
            short[] small = ScratchArray.Rent<short>(length);
            Assert.True(small.Length >= length);
            Assert.True(small.Length <= ScratchArray.PoolLimit);
            ScratchArray.Return(small);

            Assert.Same(small, ScratchArray.Rent<short>(length));
        }
    }

    [Fact]
    public void A_layout_of_a_text_above_the_pool_limit_leaves_nothing_in_the_pool()
    {
        ShapingFont[] fonts = LayoutTestKit.Fonts(LayoutTestKit.Latin);
        string text = LongText(100_000);
        int size = text.Length + 1;

        // An array of the size the layout needs, marked and given back to the pool of this thread: a layout that rents from the pool takes it first, and writes its own values over the marks.
        // (A pooled array is not cleared, so the test looks for the marks and not for zeros.)
        bool[] flags = ArrayPool<bool>.Shared.Rent(size);
        int[] ints = ArrayPool<int>.Shared.Rent(size);
        double[] doubles = ArrayPool<double>.Shared.Rent(size);
        Array.Fill(flags, true);
        Array.Fill(ints, 7);
        Array.Fill(doubles, 7.0);
        ArrayPool<bool>.Shared.Return(flags);
        ArrayPool<int>.Shared.Return(ints);
        ArrayPool<double>.Shared.Return(doubles);

        ParagraphLayout layout = LayoutTestKit.Layout(text, fonts, 3000);
        Assert.True(layout.Lines.Count > 100);

        Assert.All(flags, v => Assert.True(v));
        Assert.All(ints, v => Assert.Equal(7, v));
        Assert.All(doubles, v => Assert.Equal(7.0, v));
    }

    [Fact]
    public void A_layout_of_a_text_above_the_pool_limit_is_the_same_whatever_the_pooled_arrays_hold()
    {
        // The text is longer than the limit for every array the layout rents, so the allocated path is the one that runs; the arrays of the pool it does not use are dirty all the same.
        ShapingFont[] fonts = LayoutTestKit.Fonts(LayoutTestKit.Latin, LayoutTestKit.Arabic, LayoutTestKit.Hebrew);
        string text = LongText(100_000) + " مرحبا بالعالم שלום עולם " + LongText(5_000);
        ParagraphLayout clean = LayoutTestKit.Layout(text, fonts, 3000);
        string expected = Describe(clean);
        byte[] expectedDrawn = LayoutTestKit.Draw(clean, fonts);

        Poison();
        ParagraphLayout dirty = LayoutTestKit.Layout(text, fonts, 3000);
        Assert.Equal(expected, Describe(dirty));
        Assert.True(expectedDrawn.AsSpan().SequenceEqual(LayoutTestKit.Draw(dirty, fonts)));
    }

    private static string LongText(int length)
    {
        string[] words = ["lorem", "ipsum", "dolor", "sit", "amet", "consectetur", "adipiscing", "elit", "sed", "do", "eiusmod", "tempor", "incididunt-ut", "labore"];
        System.Text.StringBuilder builder = new(length + 20);
        for (int i = 0; builder.Length < length; i++)
        {
            _ = builder.Append(words[((i * 7) + (i / 5)) % words.Length]).Append(' ');
        }

        return builder.ToString(0, length);
    }

    [Fact]
    public void A_page_that_is_ended_does_not_allocate_a_builder_for_content_it_will_never_have_and_can_still_be_drawn_on()
    {
        ContentStreamBuilder body = new();
        PdfPageWriter page = new(null!, 0, 100, 100, body);
        _ = body.SaveState();

        long before = GC.GetAllocatedBytesForCurrentThread();
        ContentStreamBuilder dropped = page.DropBody();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // A builder owns a buffer of 4 KB, which a page that is written and finished has no use for.
        Assert.Same(body, dropped);
        Assert.True(allocated < 1024, $"Dropping the body allocated {allocated} bytes.");

        // What may still ask for the content gets a usable, empty builder, the same one each time, and a header or footer replaces it.
        Assert.Equal(0, page.Content.Length);
        Assert.Same(page.Content, page.Content);
        Assert.NotSame(body, page.Content);
        page.UseHeader();
        Assert.Same(page.HeaderContent, page.Content);
        page.UseFooter();
        Assert.Same(page.FooterContent, page.Content);
    }
}
