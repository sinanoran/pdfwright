using System.Security.Cryptography;
using Pdfwright.Tests.Document;
using Pdfwright.Tests.Pdf;

namespace Pdfwright.Tests.Conformance;

/// <summary>
/// The long pages of the corpus are not golden files, so these pin what they wrote before the content stream was written without allocating: the
/// SHA-256 of each uncompressed file, taken from the implementation that built the strings. The writing of the content may change how it makes the
/// bytes and never which bytes.
/// </summary>
public class LongPageBytesTests
{
    public static TheoryData<string, string> Pages() => new()
    {
        { "mixed", "a34e48c867eccdcf522e2f8424a192f4106bba13f040171b96eedc53cc58d9a6" },
        { "arabic", "2d9b08207a72dd36ba51daa9c9974335f3cec15a0c024fa301e69947e4bfa26c" },
        { "devanagari", "a7492fb60d15dfc3a32a899a27132cf561588ac458dbafeb8acb9eb311f1ab41" },
        { "thai", "69340b7351ec43c0373630c79a1103eb1b056abda079d3dee5ed61b065699551" },
        { "latin-extended", "ce162eb615471bbed6d04440f0caab19611706353ba9cb0030cb918fdd9b4388" },
    };

    [Theory]
    [MemberData(nameof(Pages))]
    public void The_long_page_of_a_script_is_the_bytes_the_string_writer_made(string group, string sha256)
    {
        PageCorpus corpus = PageCorpus.LoadAll().Single(c => c.Group == group);
        PageCorpus.Built built = corpus.Build(isLong: true, o => o.Compress = false);

        byte[] pdf = DocumentTestKit.Save(built.Document);

        Assert.Equal(sha256, Convert.ToHexString(SHA256.HashData(pdf)).ToLowerInvariant());
    }
}
