using Pdfwright.Benchmarks.Scenarios;

namespace Pdfwright.Tests.Performance;

/// <summary>
/// Allocation budgets (section 7 of the plan): the documents of the measuring scenarios are built and saved to memory, and the managed bytes
/// allocated on this thread by one build and one save may not exceed a limit by more than 25 percent. Allocation counts, unlike times, are stable from one machine to the next.
/// Each test has up to two limits: the plan's target, where the plan has one (the plan's figure stays as it is), and a regression guard at the highest figure measured on net8.0 to net11.0 when the guard was written.
/// The figures differ by runtime (net8.0 allocates about 7 percent more than net10.0), and the measured save runs after one warm-up, before the runtime has fully tiered up the code (about 4 percent more than at steady state), so the guards are not the figures of the benchmark program.
/// The documents are the ones of the benchmark project, whose source files are compiled into this project too, so the tests and the benchmarks cannot drift apart.
/// The plan's targets apply to the default output, which is tagged (plan of tagged PDF, section 1), and the same documents untagged (<c>Tagged = false</c>) keep the guards measured before tagging.
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

    /// <summary>Regression guards of untagged output (<see cref="PdfDocumentOptions.Tagged"/> set to <see langword="false"/>): the highest reading on net8.0 to net11.0 (Debug), rounded up to a tenth of a megabyte. Not plan targets.</summary>
    private const long UntaggedTableGuard = 207 * Megabyte / 10;

    /// <summary>
    /// The 1,000-row table tagged: 21.0 MB, the highest reading on net8.0 to net11.0, Debug and Release (net8.0 Release 20.95 MB, net8.0 Debug 20.4 to
    /// 20.7 MB, the others 18.1 to 19.6 MB), against 18.1 to 20.3 MB untagged in the same runs. It was 28.4 MB while each of its 7,007 structure
    /// elements was built as PDF objects (about 1.2 KB each); the elements are now written straight as bytes and a body row's are made again for the next.
    /// </summary>
    private const long TableGuard = 210 * Megabyte / 10;

    private const long UntaggedReportGuard = 63 * Megabyte / 10;

    private const long UntaggedStressGuard = 329 * Megabyte / 10;

    private const long UntaggedArabicGuard = 433 * Megabyte / 10;

    private const long UntaggedDevanagariGuard = 380 * Megabyte / 10;

    /// <summary>
    /// Regression guards of tagged output, the default: the highest reading on net8.0 to net11.0, Debug and Release, rounded up to a tenth of a megabyte. Not plan targets.
    /// When they were written (untagged in brackets, net8.0 and net10.0 Release): the 20-page report 5.60 to 5.79 MB (5.55 to 5.74 MB untagged), the stress document 29.3 to 30.5 MB (29.2 to 30.1 MB untagged), the
    /// Arabic table 41.9 to 42.1 MB (40.5 MB untagged), the Devanagari table 37.7 to 37.9 MB (36.3 MB untagged).
    /// </summary>
    private const long ReportGuard = 58 * Megabyte / 10;

    private const long StressGuard = 306 * Megabyte / 10;

    private const long ArabicGuard = 421 * Megabyte / 10;

    private const long DevanagariGuard = 380 * Megabyte / 10;

    private readonly FontCollection _fonts = ScenarioDocuments.LoadFonts(TestPaths.FontsDirectory);

    public void Dispose() => _fonts.Dispose();

    /// <summary>
    /// The table as the library writes it by default, tagged: the plan's target (plan of tagged PDF, section 1: the budgets stay those of untagged output)
    /// and a guard at its measured figure. net10.0 reads 18.1 to 19.5 MB; net8.0 reads 20.3 to 20.95 MB and passes through the tolerance, as the untagged
    /// table does, so the name claims no figure.
    /// </summary>
    [Fact]
    public void Table_with_1000_rows_stays_within_its_budgets() => AssertWithin(fonts => ScenarioDocuments.Table(fonts), expectedPages: 21, TableTarget, TableGuard);

    /// <summary>The same table untagged: the plan's target is 20 MB; net8.0 reads 20.5 MB and passes through the tolerance, so the name claims no figure.</summary>
    [Fact]
    public void Untagged_table_with_1000_rows_stays_within_its_budgets() => AssertWithin(fonts => ScenarioDocuments.Table(fonts, tagged: false), expectedPages: 21, TableTarget, UntaggedTableGuard);

    /// <summary>The plan's target (20 pages under 10 MB) and a guard at the measured figure, tagged (the default).</summary>
    [Fact]
    public void Report_of_20_pages_stays_within_its_budgets() => AssertWithin(fonts => ScenarioDocuments.Report20(fonts), expectedPages: 20, ReportTarget, ReportGuard);

    /// <summary>The plan's target and a guard at the measured figure, untagged.</summary>
    [Fact]
    public void Untagged_report_of_20_pages_stays_within_its_budgets() => AssertWithin(fonts => ScenarioDocuments.Report20(fonts, tagged: false), expectedPages: 20, ReportTarget, UntaggedReportGuard);

    /// <summary>The plan's target (about 100 pages, 12 charts and 20 tables under 60 MB) and a guard at the measured figure, tagged (the default).</summary>
    [Fact]
    public void Stress_document_stays_within_its_budgets() => AssertWithin(fonts => ScenarioDocuments.Stress(fonts), expectedPages: 101, StressTarget, StressGuard);

    /// <summary>The plan's target and a guard at the measured figure, untagged.</summary>
    [Fact]
    public void Untagged_stress_document_stays_within_its_budgets() => AssertWithin(fonts => ScenarioDocuments.Stress(fonts, tagged: false), expectedPages: 101, StressTarget, UntaggedStressGuard);

    /// <summary>A regression guard only, not a plan target: the Arabic table at its measured figure, tagged (the default).</summary>
    [Fact]
    public void Arabic_table_stays_within_its_guard() => AssertWithin(fonts => ScenarioDocuments.TableArabic(fonts), expectedPages: 31, null, ArabicGuard);

    /// <summary>A regression guard only: the Arabic table untagged.</summary>
    [Fact]
    public void Untagged_arabic_table_stays_within_its_guard() => AssertWithin(fonts => ScenarioDocuments.TableArabic(fonts, tagged: false), expectedPages: 31, null, UntaggedArabicGuard);

    /// <summary>A regression guard only, not a plan target: the Devanagari table at its measured figure, tagged (the default).</summary>
    [Fact]
    public void Devanagari_table_stays_within_its_guard() => AssertWithin(fonts => ScenarioDocuments.TableDevanagari(fonts), expectedPages: 20, null, DevanagariGuard);

    /// <summary>A regression guard only: the Devanagari table untagged.</summary>
    [Fact]
    public void Untagged_devanagari_table_stays_within_its_guard() => AssertWithin(fonts => ScenarioDocuments.TableDevanagari(fonts, tagged: false), expectedPages: 20, null, UntaggedDevanagariGuard);

    /// <summary>
    /// The budgets hold on a thread whose kept arrays are those of other, longer texts: what earlier work left must not keep the arrays of the next document from being kept. The threads of a test run
    /// (and of a server) lay out every kind of text in turn; before the arrays given back made room for themselves, such a thread allocated 10.4 MB for the 20-page report and 47.8 MB for the stress
    /// document (untagged), which is how the budgets failed on the build machines and not on a machine that skipped the tests that ran before them. Both documents are written tagged, the default.
    /// </summary>
    [Fact]
    public void Report_and_stress_document_stay_within_their_budgets_on_a_thread_that_keeps_the_arrays_of_longer_texts()
    {
        CrowdWithLargerArrays();
        AssertWithin(fonts => ScenarioDocuments.Report20(fonts), expectedPages: 20, ReportTarget, ReportGuard);
        CrowdWithLargerArrays();
        AssertWithin(fonts => ScenarioDocuments.Stress(fonts), expectedPages: 101, StressTarget, StressGuard);
    }

    /// <summary>Fills what the thread may keep of every element type of the layout with arrays of the two largest sizes, as long texts leave it.</summary>
    private static void CrowdWithLargerArrays()
    {
        static void Crowd<T>()
        {
            // What the thread keeps of the large sizes is taken first: what earlier tests left would otherwise fill the share, the arrays below would be dropped, and the thread would not be crowded.
            for (int size = ScratchArray.LargestSmall * 2; size <= ScratchArray.PoolLimit; size *= 2)
            {
                while (ScratchArray.KeptCount<T>(size) > 0)
                {
                    _ = ScratchArray.Rent<T>(size);
                }
            }

            List<T[]> held = [];
            for (int i = 0; i < 8; i++)
            {
                held.Add(ScratchArray.Rent<T>(ScratchArray.PoolLimit));
                held.Add(ScratchArray.Rent<T>(ScratchArray.PoolLimit / 2));
            }

            foreach (T[] array in held)
            {
                ScratchArray.Return(array);
            }
        }

        Crowd<int>();
        Crowd<double>();
        Crowd<bool>();
        Crowd<byte>();
        Crowd<Pdfwright.Unicode.BidiClass>();
        Crowd<Pdfwright.Unicode.CharacterInfo>();
        Crowd<Pdfwright.Unicode.LineBreakClass>();
        Crowd<Pdfwright.Unicode.LineBreakKind>();
        Crowd<Pdfwright.Text.ShapingFont.ShapedWord>();
    }

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
