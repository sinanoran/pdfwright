using System.Buffers;
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

    /// <summary>Fills pooled arrays of every type the layout rents with values that would change a result if they were read, and gives them back.</summary>
    private static void Poison()
    {
        for (int size = 8; size <= 2048; size *= 2)
        {
            double[] doubles = ArrayPool<double>.Shared.Rent(size);
            Array.Fill(doubles, 1e9);
            ArrayPool<double>.Shared.Return(doubles);
            bool[] bools = ArrayPool<bool>.Shared.Rent(size);
            Array.Fill(bools, true);
            ArrayPool<bool>.Shared.Return(bools);
            int[] ints = ArrayPool<int>.Shared.Rent(size);
            Array.Fill(ints, 12345);
            ArrayPool<int>.Shared.Return(ints);
            LineBreakKind[] kinds = ArrayPool<LineBreakKind>.Shared.Rent(size);
            Array.Fill(kinds, LineBreakKind.Mandatory);
            ArrayPool<LineBreakKind>.Shared.Return(kinds);
            LineBreakClass[] classes = ArrayPool<LineBreakClass>.Shared.Rent(size);
            Array.Fill(classes, LineBreakClass.BK);
            ArrayPool<LineBreakClass>.Shared.Return(classes);
            CharacterInfo[] infos = ArrayPool<CharacterInfo>.Shared.Rent(size);
            Array.Fill(infos, CharacterProperties.Of(0x1F600));
            ArrayPool<CharacterInfo>.Shared.Return(infos);
        }
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
}
