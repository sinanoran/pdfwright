using BenchmarkDotNet.Attributes;

namespace Pdfwright.Benchmarks.Scenarios;

/// <summary>Warm time and allocations of the scenarios (BenchmarkDotNet), tagged (the library's default) and untagged side by side. Not run in CI.</summary>
[MemoryDiagnoser]
public class ScenarioBenchmarks
{
    private FontCollection fonts = null!;

    /// <summary>Whether the scenarios are written tagged (<see cref="PdfDocumentOptions.Tagged"/>): both, so that the cost of tagging is measured.</summary>
    [Params(true, false)]
    public bool Tagged { get; set; }

    [GlobalSetup]
    public void Setup() => fonts = ScenarioDocuments.LoadFonts();

    [GlobalCleanup]
    public void Cleanup() => fonts.Dispose();

    [Benchmark]
    public long Table1000Rows()
    {
        using MemoryStream stream = new();
        ScenarioDocuments.Table(fonts, tagged: Tagged).Save(stream);
        return stream.Length;
    }

    [Benchmark]
    public long TenPageReport()
    {
        using MemoryStream stream = new();
        ScenarioDocuments.Report(fonts, tagged: Tagged).Save(stream);
        return stream.Length;
    }

    [Benchmark]
    public long Report20Pages()
    {
        using MemoryStream stream = new();
        ScenarioDocuments.Report20(fonts, tagged: Tagged).Save(stream);
        return stream.Length;
    }

    [Benchmark]
    public long StressHundredPages()
    {
        using MemoryStream stream = new();
        ScenarioDocuments.Stress(fonts, tagged: Tagged).Save(stream);
        return stream.Length;
    }

    [Benchmark]
    public long TableArabic1000Rows()
    {
        using MemoryStream stream = new();
        ScenarioDocuments.TableArabic(fonts, tagged: Tagged).Save(stream);
        return stream.Length;
    }

    [Benchmark]
    public long TableDevanagari1000Rows()
    {
        using MemoryStream stream = new();
        ScenarioDocuments.TableDevanagari(fonts, tagged: Tagged).Save(stream);
        return stream.Length;
    }

    [Benchmark]
    public long Table10000Rows()
    {
        using MemoryStream stream = new();
        ScenarioDocuments.Table(fonts, 10000, tagged: Tagged).Save(stream);
        return stream.Length;
    }
}
