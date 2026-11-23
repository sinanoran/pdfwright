using Pdfwright.Benchmarks.Scenarios;

namespace Pdfwright.Tests.Performance;

/// <summary>
/// Allocation budgets (section 7 of the plan): the documents of the measuring scenarios are built and saved to memory, and the managed bytes
/// allocated on this thread by one build and one save (after one warm-up) may not exceed the budget by more than 25 percent. Allocation counts, unlike times, are stable from one machine to the next.
/// The documents are the ones of the benchmark project, whose source files are compiled into this project too, so the tests and the benchmarks cannot drift apart.
/// </summary>
[Collection("Allocation budgets")]
public sealed class AllocationBudgetTests : IDisposable
{
    /// <summary>The margin over a budget that still passes.</summary>
    private const double Tolerance = 1.25;

    private const long Megabyte = 1024 * 1024;

    private readonly FontCollection _fonts = ScenarioDocuments.LoadFonts(TestPaths.FontsDirectory);

    public void Dispose() => _fonts.Dispose();

    /// <summary>The plan's target: the 1,000-row table under 20 MB (the figure was 188 MB before the allocation work).</summary>
    [Fact]
    public void Table_with_1000_rows_stays_within_20_MB() => AssertWithin(20 * Megabyte, fonts => ScenarioDocuments.Table(fonts), expectedPages: 21);

    /// <summary>The plan's target: the 20-page report under 10 MB.</summary>
    [Fact]
    public void Report_of_20_pages_stays_within_10_MB() => AssertWithin(10 * Megabyte, ScenarioDocuments.Report20, expectedPages: 20);

    /// <summary>The plan's target: the stress document of about 100 pages, 12 charts and 20 tables under 60 MB.</summary>
    [Fact]
    public void Stress_document_stays_within_60_MB() => AssertWithin(60 * Megabyte, ScenarioDocuments.Stress, expectedPages: 101);

    /// <summary>A regression guard, not a plan target: the Arabic table at 1.25 times the 43.2 MB it allocated when the guard was written (54 MB).</summary>
    [Fact]
    public void Arabic_table_stays_within_its_guard() => AssertWithin(ArabicBudget, fonts => ScenarioDocuments.TableArabic(fonts), expectedPages: 31);

    /// <summary>A regression guard, not a plan target: the Devanagari table at 1.25 times the 37.9 MB it allocated when the guard was written (48 MB).</summary>
    [Fact]
    public void Devanagari_table_stays_within_its_guard() => AssertWithin(DevanagariBudget, fonts => ScenarioDocuments.TableDevanagari(fonts), expectedPages: 20);

    private const long ArabicBudget = 54 * Megabyte;

    private const long DevanagariBudget = 48 * Megabyte;

    private void AssertWithin(long budget, Func<FontCollection, PdfDocument> build, int expectedPages)
    {
        using MemoryStream stream = new();
        stream.SetLength(0);
        build(_fonts).Save(stream);

        // The measured run: build and save, as the warm loop of the benchmark program measures it, with the stream already grown to its size.
        stream.SetLength(0);
        long before = GC.GetAllocatedBytesForCurrentThread();
        PdfDocument document = build(_fonts);
        document.Save(stream);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(expectedPages, document.Diagnostics.PageCount);
        Assert.True(
            allocated <= budget * Tolerance,
            $"allocated {allocated / (double)Megabyte:F2} MB ({allocated} bytes) against a budget of {budget / (double)Megabyte:F2} MB plus 25 percent ({(long)(budget * Tolerance)} bytes)");
    }
}
