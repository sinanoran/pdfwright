using Pdfwright.Tests.Document;
using Pdfwright.Tests.Pdf;

namespace Pdfwright.Tests.Conformance;

/// <summary>
/// One deterministic, uncompressed document for each script group (its short page) and the README's report, compared by SHA-256 with the files in
/// <c>Golden/</c>. The documents are made with the public API, the fonts of the test project and <c>Compress = false</c>, so their bytes depend on
/// the library and the fonts only. A change of the shaping, the layout, the subsetter or the writer shows up here first.
/// </summary>
public class GoldenDocumentTests
{
    public static TheoryData<string> Groups()
    {
        TheoryData<string> data = [];
        foreach (PageCorpus corpus in PageCorpus.LoadAll())
        {
            data.Add(corpus.Group);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Groups))]
    public void The_short_page_of_a_script_is_its_golden_file(string group)
    {
        PageCorpus corpus = PageCorpus.LoadAll().Single(c => c.Group == group);
        PageCorpus.Built built = corpus.Build(isLong: false, o => o.Compress = false);

        Golden.Check($"{group}-short.pdf", DocumentTestKit.Save(built.Document));
    }

    [Theory]
    [MemberData(nameof(Groups))]
    public void The_short_page_of_a_script_looks_like_its_stored_render(string group)
    {
        PageCorpus corpus = PageCorpus.LoadAll().Single(c => c.Group == group);
        PageCorpus.Built built = corpus.Build(isLong: false, o => o.Compress = false);

        RenderGolden.Check($"{group}-short.pdf", DocumentTestKit.Save(built.Document));
    }

    [Fact]
    public void The_report_of_the_readme_looks_like_its_stored_render()
    {
        PdfDocument report = Document.SampleDocumentTests.ReadmeReport(o => o.Compress = false);

        RenderGolden.Check("readme-report.pdf", DocumentTestKit.Save(report));
    }

    [Fact]
    public void The_report_of_the_readme_is_a_golden_file()
    {
        PdfDocument report = Document.SampleDocumentTests.ReadmeReport(o => o.Compress = false);

        Golden.Check("readme-report.pdf", DocumentTestKit.Save(report));
    }

    [Fact]
    public void Two_runs_of_a_deterministic_document_are_the_same_bytes_compressed_or_not()
    {
        foreach (PageCorpus corpus in PageCorpus.LoadAll())
        {
            foreach (bool isLong in new[] { false, true })
            {
                foreach (bool compress in new[] { false, true })
                {
                    byte[] first = DocumentTestKit.Save(corpus.Build(isLong, o => o.Compress = compress).Document);
                    byte[] second = DocumentTestKit.Save(corpus.Build(isLong, o => o.Compress = compress).Document);

                    Assert.True(first.AsSpan().SequenceEqual(second), $"{corpus.Group} {(isLong ? "long" : "short")} compress={compress}: two runs differ");
                }
            }
        }
    }
}
