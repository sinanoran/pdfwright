using System.Buffers.Binary;
using System.Numerics;

namespace Pdfwright.Pdf;

/// <summary>
/// SHA-256 (FIPS 180-4), computed incrementally in managed code: the document's <c>/ID</c> and the tag of a font subset are taken from it. The framework's own
/// (<c>System.Security.Cryptography</c>) gives the same digest, which the tests hold this one to, but loading it brings the cryptography assembly and the operating system's
/// cryptography library into the process (about 1 MB of working set) for two hashes that are identifiers, not security. Not thread-safe; a copy (<see cref="Clone"/>) continues
/// on its own.
/// </summary>
internal sealed class Sha256
{
    /// <summary>The length of a digest in bytes.</summary>
    internal const int HashSize = 32;

    private const int BlockSize = 64;

    private static ReadOnlySpan<uint> RoundConstants =>
    [
        0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
        0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
        0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
        0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
        0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
        0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
        0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
        0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2,
    ];

    private readonly uint[] _state = new uint[8];
    private readonly byte[] _buffer = new byte[BlockSize];
    private int _buffered;
    private long _length;

    /// <summary>A hash of no data yet.</summary>
    internal Sha256() => Reset();

    /// <summary>The SHA-256 of <paramref name="data"/>.</summary>
    internal static byte[] HashData(ReadOnlySpan<byte> data)
    {
        Sha256 hash = new();
        hash.Append(data);
        return hash.GetHashAndReset();
    }

    /// <summary>Adds <paramref name="data"/> to the data hashed.</summary>
    internal void Append(ReadOnlySpan<byte> data)
    {
        _length += data.Length;
        if (_buffered > 0)
        {
            int take = Math.Min(BlockSize - _buffered, data.Length);
            data[..take].CopyTo(_buffer.AsSpan(_buffered));
            _buffered += take;
            data = data[take..];
            if (_buffered < BlockSize)
            {
                return;
            }

            Compress(_state, _buffer);
            _buffered = 0;
        }

        while (data.Length >= BlockSize)
        {
            Compress(_state, data[..BlockSize]);
            data = data[BlockSize..];
        }

        data.CopyTo(_buffer);
        _buffered = data.Length;
    }

    /// <summary>Adds one byte to the data hashed.</summary>
    internal void Append(byte value) => Append([value]);

    /// <summary>Adds a 32-bit number, big-endian, to the data hashed.</summary>
    internal void AppendBigEndian(int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        Append(bytes);
    }

    /// <summary>A hash that continues from where this one is, on its own: what is appended to either does not reach the other.</summary>
    internal Sha256 Clone()
    {
        Sha256 copy = new();
        _state.AsSpan().CopyTo(copy._state);
        _buffer.AsSpan().CopyTo(copy._buffer);
        copy._buffered = _buffered;
        copy._length = _length;
        return copy;
    }

    /// <summary>The digest of the data appended so far; the hash starts again with no data.</summary>
    internal byte[] GetHashAndReset()
    {
        // The padding: a 1 bit, zeros up to 56 bytes into a block, and the length in bits as a big-endian 64-bit number.
        long bits = _length * 8;
        _buffer[_buffered++] = 0x80;
        if (_buffered > BlockSize - 8)
        {
            _buffer.AsSpan(_buffered).Clear();
            Compress(_state, _buffer);
            _buffered = 0;
        }

        _buffer.AsSpan(_buffered, BlockSize - 8 - _buffered).Clear();
        BinaryPrimitives.WriteInt64BigEndian(_buffer.AsSpan(BlockSize - 8), bits);
        Compress(_state, _buffer);

        byte[] digest = new byte[HashSize];
        for (int i = 0; i < 8; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(digest.AsSpan(i * 4), _state[i]);
        }

        Reset();
        return digest;
    }

    private void Reset()
    {
        _state[0] = 0x6a09e667;
        _state[1] = 0xbb67ae85;
        _state[2] = 0x3c6ef372;
        _state[3] = 0xa54ff53a;
        _state[4] = 0x510e527f;
        _state[5] = 0x9b05688c;
        _state[6] = 0x1f83d9ab;
        _state[7] = 0x5be0cd19;
        _buffer.AsSpan().Clear();
        _buffered = 0;
        _length = 0;
    }

    /// <summary>The compression function over one block of 64 bytes.</summary>
    private static void Compress(uint[] state, ReadOnlySpan<byte> block)
    {
        Span<uint> w = stackalloc uint[64];
        for (int i = 0; i < 16; i++)
        {
            w[i] = BinaryPrimitives.ReadUInt32BigEndian(block[(i * 4)..]);
        }

        for (int i = 16; i < 64; i++)
        {
            uint s0 = BitOperations.RotateRight(w[i - 15], 7) ^ BitOperations.RotateRight(w[i - 15], 18) ^ (w[i - 15] >> 3);
            uint s1 = BitOperations.RotateRight(w[i - 2], 17) ^ BitOperations.RotateRight(w[i - 2], 19) ^ (w[i - 2] >> 10);
            w[i] = w[i - 16] + s0 + w[i - 7] + s1;
        }

        uint a = state[0];
        uint b = state[1];
        uint c = state[2];
        uint d = state[3];
        uint e = state[4];
        uint f = state[5];
        uint g = state[6];
        uint h = state[7];
        ReadOnlySpan<uint> k = RoundConstants;
        for (int i = 0; i < 64; i++)
        {
            uint sum1 = BitOperations.RotateRight(e, 6) ^ BitOperations.RotateRight(e, 11) ^ BitOperations.RotateRight(e, 25);
            uint choose = (e & f) ^ (~e & g);
            uint t1 = h + sum1 + choose + k[i] + w[i];
            uint sum0 = BitOperations.RotateRight(a, 2) ^ BitOperations.RotateRight(a, 13) ^ BitOperations.RotateRight(a, 22);
            uint majority = (a & b) ^ (a & c) ^ (b & c);
            uint t2 = sum0 + majority;
            h = g;
            g = f;
            f = e;
            e = d + t1;
            d = c;
            c = b;
            b = a;
            a = t1 + t2;
        }

        state[0] += a;
        state[1] += b;
        state[2] += c;
        state[3] += d;
        state[4] += e;
        state[5] += f;
        state[6] += g;
        state[7] += h;
    }
}
