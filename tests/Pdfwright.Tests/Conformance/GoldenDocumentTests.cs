using Pdfwright.Tests.Document;
using Pdfwright.Tests.Pdf;

namespace Pdfwright.Tests.Conformance;

/// <summary>
/// One deterministic, uncompressed document for each script group (its short page) and the README's report, compared by SHA-256 with the files in
/// <c>Golden/</c>. The documents are made with the public API, the fonts of the test project and <c>Compress = false</c>, so their bytes depend on
/// the library and the fonts only. A change of the shaping, the layout, the subsetter or the writer shows up here first.
/// Each document has two golden files: <c>&lt;name&gt;.pdf</c>, written with <c>Tagged = false</c>, is the untagged file of the versions before tagged PDF,
/// byte for byte (the files committed before tagging, unchanged); <c>&lt;name&gt;-tagged.pdf</c> is the library's default output, tagged. The renders are made
/// from the tagged files and compared with the PNGs made from the untagged ones: tagging changes nothing that is drawn.
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

    private static byte[] ShortPage(string group, bool tagged)
    {
        PageCorpus corpus = PageCorpus.LoadAll().Single(c => c.Group == group);
        return DocumentTestKit.Save(corpus.Build(isLong: false, o =>
        {
            o.Compress = false;
            o.Tagged = tagged;
        }).Document);
    }

    private static byte[] ReadmeReport(bool tagged) => DocumentTestKit.Save(Document.SampleDocumentTests.ReadmeReport(o =>
    {
        o.Compress = false;
        o.Tagged = tagged;
    }));

    [Theory]
    [MemberData(nameof(Groups))]
    public void The_short_page_of_a_script_untagged_is_its_golden_file(string group) => Golden.Check($"{group}-short.pdf", ShortPage(group, tagged: false));

    [Theory]
    [MemberData(nameof(Groups))]
    public void The_short_page_of_a_script_tagged_is_its_golden_file(string group) => Golden.Check($"{group}-short-tagged.pdf", ShortPage(group, tagged: true));

    /// <summary>The tagged page against the render of the untagged one (<c>Renders/&lt;group&gt;-short-p1.png</c>): tagging must not move a pixel.</summary>
    [Theory]
    [MemberData(nameof(Groups))]
    public void The_short_page_of_a_script_looks_like_its_stored_render(string group) => RenderGolden.Check($"{group}-short.pdf", ShortPage(group, tagged: true));

    /// <summary>The tagged report against the renders of the untagged one (<c>Renders/readme-report-p*.png</c>).</summary>
    [Fact]
    public void The_report_of_the_readme_looks_like_its_stored_render() => RenderGolden.Check("readme-report.pdf", ReadmeReport(tagged: true));

    [Fact]
    public void The_report_of_the_readme_untagged_is_a_golden_file() => Golden.Check("readme-report.pdf", ReadmeReport(tagged: false));

    [Fact]
    public void The_report_of_the_readme_tagged_is_a_golden_file() => Golden.Check("readme-report-tagged.pdf", ReadmeReport(tagged: true));

    [Fact]
    public void Two_runs_of_a_deterministic_document_are_the_same_bytes_compressed_or_not_tagged_or_not()
    {
        foreach (PageCorpus corpus in PageCorpus.LoadAll())
        {
            foreach (bool isLong in new[] { false, true })
            {
                foreach (bool compress in new[] { false, true })
                {
                    foreach (bool tagged in new[] { false, true })
                    {
                        void Change(DocumentTestKit.OptionsBuilder o)
                        {
                            o.Compress = compress;
                            o.Tagged = tagged;
                        }

                        byte[] first = DocumentTestKit.Save(corpus.Build(isLong, Change).Document);
                        byte[] second = DocumentTestKit.Save(corpus.Build(isLong, Change).Document);

                        Assert.True(first.AsSpan().SequenceEqual(second), $"{corpus.Group} {(isLong ? "long" : "short")} compress={compress} tagged={tagged}: two runs differ");
                    }
                }
            }
        }
    }
}
