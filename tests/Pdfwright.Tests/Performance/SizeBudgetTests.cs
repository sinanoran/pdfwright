using Pdfwright.Benchmarks.Scenarios;

namespace Pdfwright.Tests.Performance;

/// <summary>
/// Output-size budgets (section 7 of the plan) of tagged output, which is compressed with object streams: the documents of the measuring scenarios,
/// built as the benchmark program builds them (the default options: compressed) and written tagged, may not exceed the plan's target where the plan
/// has one and the tagged file meets it, and a regression guard at the highest size measured on net8.0 to net11.0 (the bytes of a compressed stream
/// depend on the runtime's zlib) plus <see cref="Tolerance"/>. The untagged files are held to the plan's targets too.
/// </summary>
public sealed class SizeBudgetTests : IDisposable
{
    /// <summary>The margin over a guard that still passes; a plan target has none.</summary>
    private const double Tolerance = 1.05;

    private const long Kilobyte = 1024;

    /// <summary>Plan targets (section 7 of the plan).</summary>
    private const long TableTarget = 150 * Kilobyte;

    private const long ReportTarget = 120 * Kilobyte;

    private const long StressTarget = 600 * Kilobyte;

    /// <summary>
    /// Regression guards of tagged output: the highest size measured on net8.0 to net11.0, rounded up to a kilobyte of 1,024 bytes (net10.0 and
    /// net11.0 write the same bytes; net8.0 and net9.0 differ from them by up to 8 percent). Not plan targets.
    /// </summary>
    private const long TaggedTableGuard = 184 * Kilobyte;

    private const long TaggedReportGuard = 99 * Kilobyte;

    private const long TaggedStressGuard = 444 * Kilobyte;

    private const long TaggedArabicGuard = 472 * Kilobyte;

    private const long TaggedDevanagariGuard = 406 * Kilobyte;

    private readonly FontCollection _fonts = ScenarioDocuments.LoadFonts(TestPaths.FontsDirectory);

    public void Dispose() => _fonts.Dispose();

    /// <summary>
    /// The plan's target of 150 KB is <b>not met</b> by the tagged table (186,276 to 187,636 bytes; 793,991 before object streams), so it has a guard only. What is
    /// left over the untagged 75,661 bytes is the structure of 7,012 elements: about 21 KB of marked content in the content streams and about 87 KB of object
    /// streams (each element's number and offset in its stream, its kids and its parent's number, and its entry in the parent tree), numbers that
    /// Flate cannot predict.
    /// </summary>
    [Fact]
    public void Table_with_1000_rows_tagged_stays_within_its_guard() =>
        AssertWithin(ScenarioDocuments.Table(_fonts, tagged: true), expectedPages: 21, null, TaggedTableGuard);

    [Fact]
    public void Table_with_1000_rows_untagged_is_under_150_KB() => AssertWithin(ScenarioDocuments.Table(_fonts), expectedPages: 21, TableTarget, null);

    [Fact]
    public void Report_of_20_pages_tagged_is_under_120_KB() =>
        AssertWithin(ScenarioDocuments.Report20(_fonts).WithTagged(true), expectedPages: 20, ReportTarget, TaggedReportGuard);

    [Fact]
    public void Report_of_20_pages_untagged_is_under_120_KB() => AssertWithin(ScenarioDocuments.Report20(_fonts), expectedPages: 20, ReportTarget, null);

    [Fact]
    public void Stress_document_tagged_is_under_600_KB() =>
        AssertWithin(ScenarioDocuments.Stress(_fonts).WithTagged(true), expectedPages: 101, StressTarget, TaggedStressGuard);

    [Fact]
    public void Stress_document_untagged_is_under_600_KB() => AssertWithin(ScenarioDocuments.Stress(_fonts), expectedPages: 101, StressTarget, null);

    /// <summary>The plan asks for the "same" size as the Latin table, which the untagged Arabic table (372,479 bytes against 75,661: the fonts and the positioned glyphs) does not meet either; a guard only.</summary>
    [Fact]
    public void Arabic_table_tagged_stays_within_its_guard() =>
        AssertWithin(ScenarioDocuments.TableArabic(_fonts).WithTagged(true), expectedPages: 31, null, TaggedArabicGuard);

    /// <summary>As the Arabic table: a guard only (the untagged Devanagari table is 306,719 bytes).</summary>
    [Fact]
    public void Devanagari_table_tagged_stays_within_its_guard() =>
        AssertWithin(ScenarioDocuments.TableDevanagari(_fonts).WithTagged(true), expectedPages: 20, null, TaggedDevanagariGuard);

    private static void AssertWithin(PdfDocument document, int expectedPages, long? target, long? guard)
    {
        using MemoryStream stream = new();
        document.Save(stream);
        long size = stream.Length;

        Assert.Equal(expectedPages, document.Diagnostics.PageCount);
        if (target is { } plan)
        {
            Assert.True(size <= plan, $"{size} bytes ({size / (double)Kilobyte:F1} KB) against the plan's target of {plan / Kilobyte} KB");
        }

        if (guard is { } limit)
        {
            Assert.True(size <= limit * Tolerance, $"{size} bytes ({size / (double)Kilobyte:F1} KB) against a guard of {limit / Kilobyte} KB plus 5 percent ({(long)(limit * Tolerance)} bytes)");
        }
    }
}
