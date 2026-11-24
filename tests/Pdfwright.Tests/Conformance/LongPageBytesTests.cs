using System.Security.Cryptography;
using Pdfwright.Tests.Document;
using Pdfwright.Tests.Pdf;

namespace Pdfwright.Tests.Conformance;

/// <summary>
/// The long pages of the corpus are not golden files, so these pin what they wrote before the content stream was written without allocating: the
/// SHA-256 of each uncompressed file, taken from the implementation that built the strings. The writing of the content may change how it makes the
/// bytes and never which bytes. The files are untagged (<c>Tagged = false</c>), as they were written then; the tagged long pages are pinned by <c>TaggedOutputEquivalenceTests</c>.
/// </summary>
public class LongPageBytesTests
{
    public static TheoryData<string, string> Pages() => new()
    {
        { "mixed", "4c51f8e2cf1068c12820d2e6eadf13a7b4e83d04a208364acded8535612850e3" },
        { "arabic", "5a12f3274fe8ef0a6d920db259cb7825a32b7cbdd43579bc21dde7b675b99d7c" },
        { "devanagari", "bda25f95d6b18ad85f54a0d7af1686b85ee1e614d6ec944d7dcd037de150a1e7" },
        { "thai", "deb888c0b8fcfaefc2387562f09589ba6c85a98405b111772cc435a8c65541b4" },
        { "latin-extended", "df0bfeab802872074d1fc3594cbe708eedd76cec56f3d5dc76db6e0f07459f65" },
    };

    [Theory]
    [MemberData(nameof(Pages))]
    public void The_long_page_of_a_script_is_the_bytes_the_string_writer_made(string group, string sha256)
    {
        PageCorpus corpus = PageCorpus.LoadAll().Single(c => c.Group == group);
        PageCorpus.Built built = corpus.Build(isLong: true, o =>
        {
            o.Compress = false;
            o.Tagged = false;
        });

        byte[] pdf = DocumentTestKit.Save(built.Document);

        Assert.Equal(sha256, Convert.ToHexString(SHA256.HashData(pdf)).ToLowerInvariant());
    }
}
