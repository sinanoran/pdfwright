using Pdfwright.Pdf;
using Pdfwright.Tests.Conformance;
using Pdfwright.Tests.Document;
using Pdfwright.Text;
using Pdfwright.Unicode;

namespace Pdfwright.Tests.Text;

/// <summary>
/// The working arrays of a layout (the breakers' scratch arrays and the line breaker's State) are kept for reuse by each thread (<see cref="ScratchArray"/>) and given back, and the content buffer of a finished
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
    /// Fills kept arrays of every type the layout rents with values that would change a result if they were read, and gives them back. A dozen arrays of each size are held at once before any is
    /// returned, more than the slots a thread keeps of a size, so that every slot is dirty: a single rent and return only dirties one, which the next rent on the thread takes first.
    /// The arrays are kept by each thread (<see cref="ScratchArray"/>), so this dirties those of the thread the test runs on, which is the thread its layouts run on.
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
                    held[k] = ScratchArray.Rent<T>(size);
                    Array.Fill(held[k], value);
                }

                foreach (T[] array in held)
                {
                    ScratchArray.Return(array);
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
    public void A_layout_of_a_text_above_the_pool_limit_neither_takes_nor_adds_kept_arrays_of_the_largest_size()
    {
        ShapingFont[] fonts = LayoutTestKit.Fonts(LayoutTestKit.Latin);
        string text = LongText(100_000);

        // The thread keeps an array of the largest size of two types, marked and given back last: a layout of a text above the limit allocates its arrays of the text's length, so it takes none of these (they would come back
        // overwritten, or others would) and gives none back (the thread keeps as many of each as before). A kept array is not cleared, so the test looks for the marks and not for zeros.
        // What the thread keeps of the two types is taken first, so that the marked arrays fit within what a thread may keep of a type.
        static void Drain<T>()
        {
            for (int size = 16; size <= ScratchArray.PoolLimit; size *= 2)
            {
                while (ScratchArray.KeptCount<T>(size) > 0)
                {
                    _ = ScratchArray.Rent<T>(size);
                }
            }
        }

        Drain<bool>();
        Drain<int>();
        bool[] flags = ScratchArray.Rent<bool>(ScratchArray.PoolLimit);
        int[] ints = ScratchArray.Rent<int>(ScratchArray.PoolLimit);
        Array.Fill(flags, true);
        Array.Fill(ints, 7);
        ScratchArray.Return(flags);
        ScratchArray.Return(ints);
        int[] kept = [ScratchArray.KeptCount<bool>(ScratchArray.PoolLimit), ScratchArray.KeptCount<int>(ScratchArray.PoolLimit)];
        Assert.All(kept, count => Assert.True(count >= 1));

        ParagraphLayout layout = LayoutTestKit.Layout(text, fonts, 3000);
        Assert.True(layout.Lines.Count > 100);

        Assert.Equal(kept, (int[])[ScratchArray.KeptCount<bool>(ScratchArray.PoolLimit), ScratchArray.KeptCount<int>(ScratchArray.PoolLimit)]);
        bool[] flagsAfter = ScratchArray.Rent<bool>(ScratchArray.PoolLimit);
        int[] intsAfter = ScratchArray.Rent<int>(ScratchArray.PoolLimit);
        Assert.Same(flags, flagsAfter);
        Assert.Same(ints, intsAfter);
        Assert.All(flags, v => Assert.True(v));
        Assert.All(ints, v => Assert.Equal(7, v));
        ScratchArray.Return(flags);
        ScratchArray.Return(ints);
    }

    [Fact]
    public void A_thread_keeps_at_most_eight_arrays_of_a_size_and_a_bounded_number_of_bytes_of_the_large_ones()
    {
        // The bound of what a thread keeps, which is never trimmed: arrays given back past it are dropped.
        static void Drain()
        {
            for (int size = 16; size <= ScratchArray.PoolLimit; size *= 2)
            {
                while (ScratchArray.KeptCount<int>(size) > 0)
                {
                    _ = ScratchArray.Rent<int>(size);
                }
            }
        }

        for (int length = 16; length <= ScratchArray.PoolLimit; length *= 2)
        {
            Drain();
            int[][] held = [.. Enumerable.Range(0, 20).Select(_ => ScratchArray.Rent<int>(length))];
            foreach (int[] array in held)
            {
                ScratchArray.Return(array);
            }

            bool large = length > ScratchArray.LargestSmall;
            int expected = large ? Math.Min(8, ScratchArray.LargeBytesPerType / (length * sizeof(int))) : 8;
            Assert.Equal(expected, ScratchArray.KeptCount<int>(length));
            Assert.Equal(large ? expected * length * sizeof(int) : 0, ScratchArray.KeptBytes<int>());

            // What is kept is handed out again, the last given back first, and then new arrays.
            Assert.Same(held[expected - 1], ScratchArray.Rent<int>(length));
            Drain();
            Assert.Equal(0, ScratchArray.KeptBytes<int>());
            int[] fresh = ScratchArray.Rent<int>(length);
            Assert.DoesNotContain(held, array => ReferenceEquals(array, fresh));
        }
    }

    [Fact]
    public void Arrays_given_back_take_the_place_of_kept_arrays_of_other_large_sizes_within_the_bound()
    {
        static void Drain()
        {
            for (int size = 16; size <= ScratchArray.PoolLimit; size *= 2)
            {
                while (ScratchArray.KeptCount<int>(size) > 0)
                {
                    _ = ScratchArray.Rent<int>(size);
                }
            }
        }

        // What longer texts left: one array of 4,096 and one of 2,048 (24 KB of the 28 KB a thread keeps of a type).
        Drain();
        int[] largest = ScratchArray.Rent<int>(4096);
        int[] large = ScratchArray.Rent<int>(2048);
        ScratchArray.Return(largest);
        ScratchArray.Return(large);
        Assert.Equal(24 * 1024, ScratchArray.KeptBytes<int>());

        // The current work gives back seven arrays of 1,024 (28 KB): all are kept, in the place of the larger arrays, the largest dropped first, and the bound holds after each one.
        int[][] current = [.. Enumerable.Range(0, 7).Select(_ => ScratchArray.Rent<int>(1024))];
        for (int i = 0; i < current.Length; i++)
        {
            ScratchArray.Return(current[i]);
            Assert.True(ScratchArray.KeptBytes<int>() <= ScratchArray.LargeBytesPerType);
            if (i == 1)
            {
                Assert.Equal(0, ScratchArray.KeptCount<int>(4096));
                Assert.Equal(1, ScratchArray.KeptCount<int>(2048));
            }
        }

        Assert.Equal(7, ScratchArray.KeptCount<int>(1024));
        Assert.Equal(0, ScratchArray.KeptCount<int>(4096));
        Assert.Equal(0, ScratchArray.KeptCount<int>(2048));
        Assert.Equal(ScratchArray.LargeBytesPerType, ScratchArray.KeptBytes<int>());

        // An eighth array of the same size does not push out its own size: it is dropped.
        ScratchArray.Return(new int[1024]);
        Assert.Equal(7, ScratchArray.KeptCount<int>(1024));
        Assert.Equal(ScratchArray.LargeBytesPerType, ScratchArray.KeptBytes<int>());

        // A small array never takes the place of a large one, and is kept outside the bytes of the large ones.
        int smallBefore = ScratchArray.KeptCount<int>(256);
        ScratchArray.Return(new int[256]);
        Assert.Equal(smallBefore + 1, ScratchArray.KeptCount<int>(256));
        Assert.Equal(7, ScratchArray.KeptCount<int>(1024));
        Assert.Equal(ScratchArray.LargeBytesPerType, ScratchArray.KeptBytes<int>());
        Drain();
    }

    [Fact]
    public void An_array_that_cannot_fit_beside_the_arrays_of_its_own_size_drops_nothing_else()
    {
        static void Drain()
        {
            for (int size = 16; size <= ScratchArray.PoolLimit; size *= 2)
            {
                while (ScratchArray.KeptCount<int>(size) > 0)
                {
                    _ = ScratchArray.Rent<int>(size);
                }
            }
        }

        // One array of 4,096 (16 KB) and three of 1,024 (12 KB) fill the share; a second array of 4,096 would need 32 KB beside the first, so it is dropped and the arrays of 1,024 stay.
        Drain();
        int[] largest = ScratchArray.Rent<int>(4096);
        int[] second = ScratchArray.Rent<int>(4096);
        int[][] smaller = [.. Enumerable.Range(0, 3).Select(_ => ScratchArray.Rent<int>(1024))];
        ScratchArray.Return(largest);
        foreach (int[] array in smaller)
        {
            ScratchArray.Return(array);
        }

        Assert.Equal(ScratchArray.LargeBytesPerType, ScratchArray.KeptBytes<int>());
        ScratchArray.Return(second);
        Assert.Equal(1, ScratchArray.KeptCount<int>(4096));
        Assert.Equal(3, ScratchArray.KeptCount<int>(1024));
        Assert.Equal(ScratchArray.LargeBytesPerType, ScratchArray.KeptBytes<int>());
        Assert.Same(largest, ScratchArray.Rent<int>(4096));
        Drain();
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
