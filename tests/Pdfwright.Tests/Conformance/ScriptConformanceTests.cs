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

        string[] pages = PageTexts(pdf);
        Extraction.Result result = Extraction.Match(string.Join(Extraction.Line, pages), built.Expected, built.Header);
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

    /// <summary>The text of each page with the line and gap markers of <see cref="Extraction.Read"/>.</summary>
    internal static string[] PageTexts(byte[] pdf)
    {
        using UglyToad.PdfPig.PdfDocument read = UglyToad.PdfPig.PdfDocument.Open(pdf);
        return [.. read.GetPages().Select(Extraction.Read)];
    }

    [Theory]
    [MemberData(nameof(Pages))]
    public void Letters_of_right_to_left_runs_fall_and_of_left_to_right_runs_rise_in_the_order_of_the_stream(string group, bool isLong)
    {
        byte[] pdf = Make(group, isLong, out _);

        int rtl = 0;
        int ltr = 0;
        List<string> violations = [];
        using (UglyToad.PdfPig.PdfDocument read = UglyToad.PdfPig.PdfDocument.Open(pdf))
        {
            foreach (UglyToad.PdfPig.Content.Page page in read.GetPages())
            {
                Directions.Report report = Directions.Check(page);
                rtl += report.RightToLeftPairs;
                ltr += report.LeftToRightPairs;
                violations.AddRange(report.Violations.Select(v => $"page {page.Number}: {v}"));
            }
        }

        Assert.True(violations.Count == 0, $"{group} {(isLong ? "long" : "short")}: {violations.Count} of {rtl + ltr} pairs of letters run the wrong way\n" + string.Join('\n', violations.Take(8)));

        // The check must have looked at something: a script that is right to left has right-to-left pairs, the Latin, Greek and Cyrillic groups have left-to-right ones.
        if (group is "arabic" or "hebrew" or "mixed")
        {
            Assert.True(rtl >= 100, $"{group}: only {rtl} right-to-left pairs were checked");
        }

        if (group is "latin-extended" or "greek" or "cyrillic" or "mixed")
        {
            Assert.True(ltr >= 100, $"{group}: only {ltr} left-to-right pairs were checked");
        }
    }

    [Fact]
    public void The_comparison_notices_a_moved_space_a_lost_space_and_a_character_moved_into_the_next_string()
    {
        string L = Extraction.Line.ToString();
        string G = Extraction.Gap.ToString();

        // A moved space, a lost space.
        Assert.NotEmpty(Extraction.Match("a bcd", ["ab cd"], []).Failures);
        Assert.NotEmpty(Extraction.Match("abcd", ["ab cd"], []).Failures);
        Assert.NotEmpty(Extraction.Match("ab  c d", ["ab cd"], []).Failures);
        Assert.NotEmpty(Extraction.Match("ab cd", ["abcd"], []).Failures);

        // A character moved into the next string, with or without the gap of the next table cell between them.
        Assert.NotEmpty(Extraction.Match("abc d", ["ab", "cd"], []).Failures);
        Assert.NotEmpty(Extraction.Match("abc" + G + "d", ["ab", "cd"], []).Failures);
        Assert.NotEmpty(Extraction.Match("abc" + G + "de", ["ab", "cde"], []).Failures);
        Assert.Empty(Extraction.Match("ab" + G + "cd", ["ab", "cd"], []).Failures);
        Assert.Empty(Extraction.Match("ab" + L + "cd", ["ab", "cd"], []).Failures);

        // Equal after the normalisation: any run of white space is one space, a grapheme joiner is nothing.
        Assert.Empty(Extraction.Match("ab \n\t cd" + (char)0x034F, ["ab cd"], []).Failures);

        // A line break stands for a space, and may fall inside a string only between grapheme clusters; a gap only inside a string (a jump between runs).
        Assert.Empty(Extraction.Match("ab" + L + "cd", ["ab cd"], []).Failures);
        Assert.Empty(Extraction.Match("ab " + L + "cd", ["ab cd"], []).Failures);
        Assert.Empty(Extraction.Match("ab" + L + "cd", ["abcd"], []).Failures);
        Assert.NotEmpty(Extraction.Match("ab " + L + "cd", ["abcd"], []).Failures);
        Assert.NotEmpty(Extraction.Match("a" + L + "́b", ["áb"], []).Failures);
        Assert.Empty(Extraction.Match("á" + L + "b", ["áb"], []).Failures);
        Assert.Empty(Extraction.Match("ab" + G + "cd", ["abcd"], []).Failures);
        Assert.NotEmpty(Extraction.Match("ab" + G, ["abcd"], []).Failures);
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
