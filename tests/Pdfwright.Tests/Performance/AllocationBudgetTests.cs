using Pdfwright.Benchmarks.Scenarios;

namespace Pdfwright.Tests.Performance;

/// <summary>
/// Allocation budgets (section 7 of the plan): the documents of the measuring scenarios are built and saved to memory, and the managed bytes
/// allocated on this thread by one build and one save may not exceed a limit by more than 25 percent. Allocation counts, unlike times, are stable from one machine to the next.
/// Each test has up to two limits: the plan's target, where the plan has one (the plan's figure stays as it is), and a regression guard at the highest figure measured on net8.0 to net11.0 when the guard was written.
/// The figures differ by runtime (net8.0 allocates about 7 percent more than net10.0), and the measured save runs after one warm-up, before the runtime has fully tiered up the code (about 4 percent more than at steady state), so the guards are not the figures of the benchmark program.
/// The documents are the ones of the benchmark project, whose source files are compiled into this project too, so the tests and the benchmarks cannot drift apart.
/// </summary>
public sealed class AllocationBudgetTests : IDisposable
{
    /// <summary>The margin over a limit that still passes. It is the only margin: a guard is the measured figure itself.</summary>
    private const double Tolerance = 1.25;

    private const long Megabyte = 1024 * 1024;

    /// <summary>Plan targets (section 7 of the plan).</summary>
    private const long TableTarget = 20 * Megabyte;

    private const long ReportTarget = 10 * Megabyte;

    private const long StressTarget = 60 * Megabyte;

    /// <summary>Regression guards: the highest reading on net8.0 to net11.0 (Debug), rounded up to a tenth of a megabyte. Not plan targets.</summary>
    private const long TableGuard = 207 * Megabyte / 10;

    private const long ReportGuard = 63 * Megabyte / 10;

    private const long StressGuard = 329 * Megabyte / 10;

    private const long ArabicGuard = 433 * Megabyte / 10;

    private const long DevanagariGuard = 380 * Megabyte / 10;

    private readonly FontCollection _fonts = ScenarioDocuments.LoadFonts(TestPaths.FontsDirectory);

    public void Dispose() => _fonts.Dispose();

    /// <summary>The plan's target is 20 MB; net8.0 reads 20.5 MB and passes through the tolerance, so the name claims no figure.</summary>
    [Fact]
    public void Table_with_1000_rows_stays_within_its_budgets() => AssertWithin(fonts => ScenarioDocuments.Table(fonts), expectedPages: 21, TableTarget, TableGuard);

    /// <summary>The plan's target (20 pages under 10 MB) and a guard at the measured figure.</summary>
    [Fact]
    public void Report_of_20_pages_stays_within_its_budgets() => AssertWithin(ScenarioDocuments.Report20, expectedPages: 20, ReportTarget, ReportGuard);

    /// <summary>The plan's target (about 100 pages, 12 charts and 20 tables under 60 MB) and a guard at the measured figure.</summary>
    [Fact]
    public void Stress_document_stays_within_its_budgets() => AssertWithin(ScenarioDocuments.Stress, expectedPages: 101, StressTarget, StressGuard);

    /// <summary>A regression guard only, not a plan target: the Arabic table at its measured figure.</summary>
    [Fact]
    public void Arabic_table_stays_within_its_guard() => AssertWithin(fonts => ScenarioDocuments.TableArabic(fonts), expectedPages: 31, null, ArabicGuard);

    /// <summary>A regression guard only, not a plan target: the Devanagari table at its measured figure.</summary>
    [Fact]
    public void Devanagari_table_stays_within_its_guard() => AssertWithin(fonts => ScenarioDocuments.TableDevanagari(fonts), expectedPages: 20, null, DevanagariGuard);

    private void AssertWithin(Func<FontCollection, PdfDocument> build, int expectedPages, long? target, long guard)
    {
        using MemoryStream stream = new();
        build(_fonts).Save(stream);

        // The measured run: build and save, as the warm loop of the benchmark program measures it, with the stream already grown to its size.
        stream.SetLength(0);
        long before = GC.GetAllocatedBytesForCurrentThread();
        PdfDocument document = build(_fonts);
        document.Save(stream);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(expectedPages, document.Diagnostics.PageCount);
        string percent = ((Tolerance - 1) * 100).ToString("0", System.Globalization.CultureInfo.InvariantCulture);
        if (target is { } plan)
        {
            Assert.True(allocated <= plan * Tolerance, $"allocated {allocated / (double)Megabyte:F2} MB ({allocated} bytes) against the plan's target of {plan / (double)Megabyte:F2} MB plus {percent} percent ({(long)(plan * Tolerance)} bytes)");
        }

        Assert.True(allocated <= guard * Tolerance, $"allocated {allocated / (double)Megabyte:F2} MB ({allocated} bytes) against a guard of {guard / (double)Megabyte:F2} MB plus {percent} percent ({(long)(guard * Tolerance)} bytes)");
    }
}
