using System.Security.Cryptography;
using System.Text;
using Pdfwright.Document;
using Pdfwright.Pdf;

namespace Pdfwright.Tests.Pdf;

/// <summary>
/// The library's own SHA-256 (<see cref="Sha256"/>), which the document's <c>/ID</c> and the tags of font subsets come from: the test vectors of FIPS 180-4 (NIST's examples for SHA-256), and the
/// digest of the framework's SHA-256 for random data appended in random pieces, so that a file's <c>/ID</c> and its fonts' names are what they were with the framework's.
/// </summary>
public class Sha256Tests
{
    [Theory]
    [InlineData("", "E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855")]
    [InlineData("abc", "BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD")]
    [InlineData("abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq", "248D6A61D20638B8E5C026930C3E6039A33CE45964FF2167F6ECEDD419DB06C1")]
    [InlineData("abcdefghbcdefghicdefghijdefghijkefghijklfghijklmghijklmnhijklmnoijklmnopjklmnopqklmnopqrlmnopqrsmnopqrstnopqrstu", "CF5B16A778AF8380036CE59E7B0492370B249B11E8F07A51AFAC45037AFEE9D1")]
    public void The_test_vectors_of_the_standard_hash_to_their_digests(string message, string digest)
    {
        Assert.Equal(digest, Convert.ToHexString(Sha256.HashData(Encoding.ASCII.GetBytes(message))));
    }

    [Fact]
    public void A_million_times_a_hashes_to_its_digest_whole_and_in_pieces()
    {
        byte[] million = new byte[1_000_000];
        Array.Fill(million, (byte)'a');
        const string Digest = "CDC76E5C9914FB9281A1C7E284D73E67F1809A48A497200E046D39CCC7112CD0";
        Assert.Equal(Digest, Convert.ToHexString(Sha256.HashData(million)));

        Sha256 pieces = new();
        for (int at = 0; at < million.Length; at += 997)
        {
            pieces.Append(million.AsSpan(at, Math.Min(997, million.Length - at)));
        }

        Assert.Equal(Digest, Convert.ToHexString(pieces.GetHashAndReset()));
    }

    [Fact]
    public void Random_data_appended_in_random_pieces_hashes_as_the_framework_hashes_it()
    {
        Random random = new(20261124);
        for (int round = 0; round < 400; round++)
        {
            // Lengths around the block of 64 bytes and the 56 bytes after which the padding takes another block, and longer ones.
            int length = round < 200 ? round : random.Next(100_000);
            byte[] data = new byte[length];
            random.NextBytes(data);
            Sha256 hash = new();
            int at = 0;
            while (at < length)
            {
                int piece = Math.Min(length - at, random.Next(4) == 0 ? random.Next(200) : random.Next(5));
                if (piece == 1 && random.Next(2) == 0)
                {
                    hash.Append(data[at]);
                }
                else
                {
                    hash.Append(data.AsSpan(at, piece));
                }

                at += piece;
            }

            Assert.Equal(SHA256.HashData(data), hash.GetHashAndReset());

            // After a digest the hash starts again with no data.
            hash.Append(data);
            Assert.Equal(SHA256.HashData(data), hash.GetHashAndReset());
        }
    }

    [Fact]
    public void A_copy_goes_on_from_where_the_hash_was_and_leaves_it_as_it_was()
    {
        byte[] prefix = new byte[1000];
        byte[] first = new byte[77];
        byte[] second = new byte[13];
        Random random = new(7);
        random.NextBytes(prefix);
        random.NextBytes(first);
        random.NextBytes(second);
        Sha256 hash = new();
        hash.Append(prefix);
        Sha256 copy = hash.Clone();
        copy.Append(first);
        hash.Append(second);
        hash.AppendBigEndian(0x01020304);

        Assert.Equal(SHA256.HashData([.. prefix, .. first]), copy.GetHashAndReset());
        Assert.Equal(SHA256.HashData([.. prefix, .. second, 1, 2, 3, 4]), hash.GetHashAndReset());
    }

    /// <summary>
    /// The file being saved is hashed for its <c>/ID</c> from the buffer it was written into, with the library's hash below <see cref="SaveBuffer.FrameworkHashThreshold"/> and the framework's from there
    /// on: both give the digest of the bytes, whatever the length and however they were written (lengths around the buffer's blocks of 64 KiB).
    /// </summary>
    [Fact]
    public void A_saved_file_is_hashed_alike_by_the_library_and_by_the_framework()
    {
        Random random = new(20261125);
        foreach (int length in new[] { 0, 1, 63, 64, 65, 65535, 65536, 65537, 200_000, 3 * 65536 })
        {
            byte[] data = new byte[length];
            random.NextBytes(data);
            using SaveBuffer buffer = new();
            for (int at = 0; at < length;)
            {
                int piece = Math.Min(length - at, random.Next(1, 30_000));
                buffer.Write(data.AsSpan(at, piece));
                at += piece;
            }

            byte[] expected = SHA256.HashData(data);
            Assert.Equal(expected, buffer.Sha256(length, frameworkThreshold: long.MaxValue));
            Assert.Equal(expected, buffer.Sha256(length, frameworkThreshold: 0));
            if (length > 1)
            {
                Assert.Equal(SHA256.HashData(data.AsSpan(0, length - 1)), buffer.Sha256(length - 1, frameworkThreshold: long.MaxValue));
                Assert.Equal(SHA256.HashData(data.AsSpan(0, length - 1)), buffer.Sha256(length - 1, frameworkThreshold: 0));
            }
        }
    }

    /// <summary>A document's <c>/ID</c> is the first half of the SHA-256 of the file before its cross-reference stream, from a file small enough for the library's hash and one large enough for the framework's.</summary>
    [Theory]
    [InlineData(10)]
    [InlineData(1000)]
    public void The_id_of_a_saved_document_is_the_hash_of_its_bytes_whichever_hash_took_it(int rows)
    {
        using FontCollection fonts = Pdfwright.Benchmarks.Scenarios.ScenarioDocuments.LoadFonts(TestPaths.FontsDirectory);
        using MemoryStream stream = new();
        Pdfwright.Benchmarks.Scenarios.ScenarioDocuments.Table(fonts, rows, pinned: true).Save(stream);
        byte[] pdf = stream.ToArray();
        string text = Encoding.Latin1.GetString(pdf);
        int xref = int.Parse(System.Text.RegularExpressions.Regex.Match(text, @"startxref\n(\d+)\n%%EOF\n$").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(rows > 100, xref >= SaveBuffer.FrameworkHashThreshold);
        string expected = Convert.ToHexString(SHA256.HashData(pdf.AsSpan(0, xref)))[..32];
        Assert.Contains($"/ID [<{expected}> <{expected}>]", text, StringComparison.Ordinal);
    }
}
