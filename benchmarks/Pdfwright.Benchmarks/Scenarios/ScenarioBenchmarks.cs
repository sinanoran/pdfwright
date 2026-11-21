using BenchmarkDotNet.Attributes;

namespace Pdfwright.Benchmarks.Scenarios;

/// <summary>Warm time and allocations of the two scenarios (BenchmarkDotNet). Not run in CI.</summary>
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
}
