using System.Security.Cryptography;
using System.Text;
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
}
