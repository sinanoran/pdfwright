using BenchmarkDotNet.Attributes;

namespace Pdfwright.Benchmarks.Scenarios;

/// <summary>Warm time and allocations of the scenarios (BenchmarkDotNet). Not run in CI.</summary>
[MemoryDiagnoser]
public class ScenarioBenchmarks
{
    private FontCollection fonts = null!;

    [GlobalSetup]
    public void Setup() => fonts = ScenarioDocuments.LoadFonts();

    [GlobalCleanup]
    public void Cleanup() => fonts.Dispose();

    [Benchmark]
    public long Table1000Rows()
    {
        using MemoryStream stream = new();
        ScenarioDocuments.Table(fonts).Save(stream);
        return stream.Length;
    }

    [Benchmark]
    public long TenPageReport()
    {
        using MemoryStream stream = new();
        ScenarioDocuments.Report(fonts).Save(stream);
        return stream.Length;
    }

    [Benchmark]
    public long Report20Pages()
    {
        using MemoryStream stream = new();
        ScenarioDocuments.Report20(fonts).Save(stream);
        return stream.Length;
    }

    [Benchmark]
    public long StressHundredPages()
    {
        using MemoryStream stream = new();
        ScenarioDocuments.Stress(fonts).Save(stream);
        return stream.Length;
    }

    [Benchmark]
    public long TableArabic1000Rows()
    {
        using MemoryStream stream = new();
        ScenarioDocuments.TableArabic(fonts).Save(stream);
        return stream.Length;
    }

    [Benchmark]
    public long TableDevanagari1000Rows()
    {
        using MemoryStream stream = new();
        ScenarioDocuments.TableDevanagari(fonts).Save(stream);
        return stream.Length;
    }

    [Benchmark]
    public long Table10000Rows()
    {
        using MemoryStream stream = new();
        ScenarioDocuments.Table(fonts, 10000).Save(stream);
        return stream.Length;
    }
}
