using Pdfwright.Tests.Document;
using System.Globalization;
using System.Text;

namespace Pdfwright.Tests.Conformance;

/// <summary>
/// Acceptance by extraction at the level of the public document (plan 8.2 and 8.3): for each script group, a short page and a long page with a table
/// are made with <c>PdfDocument</c>, read back with PdfPig, and every heading, paragraph and table cell must come back equal to its input in logical order
/// after the normalisation of <see cref="Extraction"/> (white space and U+034F removed). Environment: <c>PDFWRIGHT_DUMP</c> keeps the files and the expected strings
/// for <c>tools/pdfjs-check</c>; <c>PDFWRIGHT_CONFORMANCE_REPORT</c> appends one line per page to a file.
/// </summary>
public class ScriptConformanceTests
{
    public static TheoryData<string, bool> Pages()
    {
        TheoryData<string, bool> data = [];
        foreach (PageCorpus corpus in PageCorpus.LoadAll())
        {
            data.Add(corpus.Group, false);
            data.Add(corpus.Group, true);
        }

        return data;
    }

    internal static byte[] Make(string group, bool isLong, out PageCorpus.Built built)
    {
        PageCorpus corpus = PageCorpus.LoadAll().Single(c => c.Group == group);
        built = corpus.Build(isLong);
        return DocumentTestKit.Save(built.Document);
    }

    private static void Record(string group, bool isLong, byte[] pdf, PageCorpus.Built built, Extraction.Result result, int pages)
    {
        string name = $"{group}-{(isLong ? "long" : "short")}";
        string? dump = Environment.GetEnvironmentVariable("PDFWRIGHT_DUMP");
        if (!string.IsNullOrEmpty(dump))
        {
            _ = Directory.CreateDirectory(dump);
            File.WriteAllBytes(Path.Combine(dump, name + ".pdf"), pdf);
            string expected = string.Join('\n', built.Expected) + (built.HasTable ? "\n!" + string.Concat(built.Header) : string.Empty) + "\n";
            File.WriteAllText(Path.Combine(dump, name + ".expected.txt"), expected, new UTF8Encoding(false));
        }

        string? report = Environment.GetEnvironmentVariable("PDFWRIGHT_CONFORMANCE_REPORT");
        if (!string.IsNullOrEmpty(report))
        {
            lock (typeof(ScriptConformanceTests))
            {
                File.AppendAllText(report, string.Create(CultureInfo.InvariantCulture, $"{group}|{(isLong ? "long" : "short")}|{result.Passed}|{result.Total}|{pages}|{result.HeaderRepeats}\n"));
            }
        }
    }

    [Theory]
    [MemberData(nameof(Pages))]
    public void Every_string_of_a_page_reads_back_equal_in_logical_order(string group, bool isLong)
    {
        byte[] pdf = Make(group, isLong, out PageCorpus.Built built);

        string[] pages = DocumentTestKit.PageTexts(pdf);
        Extraction.Result result = Extraction.Match(string.Concat(pages), built.Expected, built.Header);
        Record(group, isLong, pdf, built, result, pages.Length);

        Assert.Empty(built.Document.Diagnostics.MissingGlyphs);
        Assert.Empty(built.Document.Diagnostics.MissingFamilies);
        Assert.True(
            result.Failures.Count == 0 && result.Trailing.Length == 0,
            $"{group} {(isLong ? "long" : "short")}: {result.Passed} of {result.Total} strings read back; trailing '{result.Trailing}'\n" + string.Join('\n', result.Failures.Take(8)));
        if (isLong)
        {
            Assert.True(pages.Length >= 2, $"the long page of {group} has {pages.Length} page");
            Assert.Equal(pages.Length - 1, result.HeaderRepeats);
        }
        else
        {
            Assert.Single(pages);
        }
    }

    [Theory]
    [MemberData(nameof(Pages))]
    public void Qpdf_accepts_every_page_of_the_corpus(string group, bool isLong)
    {
        byte[] pdf = Make(group, isLong, out _);

        DocumentTestKit.AssertValid(pdf, $"corpus-{group}-{(isLong ? "long" : "short")}");
    }

    [Fact]
    public void The_comparison_notices_a_changed_string_a_swapped_pair_and_extra_text()
    {
        string[] strings = ["مرحبا بالعالم", "שלום", "abc"];
        string text = string.Join(' ', strings);

        Extraction.Result same = Extraction.Match(text + (char)0x034F, strings, []);
        Extraction.Result changed = Extraction.Match("مرحبا بالعالم שלם abc", strings, []);
        Extraction.Result swapped = Extraction.Match("שלום مرحبا بالعالم abc", strings, []);
        Extraction.Result extra = Extraction.Match(text + " more", strings, []);
        Extraction.Result header = Extraction.Match("H1 H2 a b H1 H2 c", ["H1", "H2", "a", "b", "c"], ["H1", "H2"]);

        Assert.Equal(3, same.Passed);
        Assert.Empty(same.Failures);
        Assert.Equal(2, changed.Passed);
        Assert.Single(changed.Failures);
        Assert.NotEmpty(swapped.Failures);
        Assert.Equal("more", extra.Trailing);
        Assert.Equal(5, header.Passed);
        Assert.Equal(1, header.HeaderRepeats);
    }
}
