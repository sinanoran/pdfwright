// Copyright 2011 The Go Authors. All rights reserved.
//
// Ported to C# from golang.org/x/image v0.48.0 (https://github.com/golang/image), whose licence (BSD-3-Clause) and patent grant are in the NOTICE file of this package. Its text, as the licence requires:
//
// Copyright 2009 The Go Authors.
//
// Redistribution and use in source and binary forms, with or without modification, are permitted provided that the following conditions are met:
//
//    * Redistributions of source code must retain the above copyright notice, this list of conditions and the following disclaimer.
//    * Redistributions in binary form must reproduce the above copyright notice, this list of conditions and the following disclaimer in the documentation and/or other materials provided with the distribution.
//    * Neither the name of Google LLC nor the names of its contributors may be used to endorse or promote products derived from this software without specific prior written permission.
//
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE
// ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
// SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
// OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
//
// The conversion to C# and the additions are part of Pdfwright (MIT licence). The name of Google is not used to endorse this software.
//
// An arithmetic-coded VP8 partition (RFC 6386, chapter 7), after golang.org/x/image/vp8 (partition.go), which follows libwebp's approach: a look-up table recalibrates the range.

namespace Pdfwright.Images;

/// <summary>One of the independent, byte-aligned partitions of a VP8 frame, read a bit at a time. Reading past the end sets <see cref="UnexpectedEof"/> and returns zero bits, so the caller finds out at the end of the frame.</summary>
internal sealed class Vp8Partition
{
    /// <summary>The probability of a bit that is as likely 0 as 1.</summary>
    internal const int UniformProb = 128;

    private static readonly byte[] LutShift =
    [
        7, 6, 6, 5, 5, 5, 5, 4, 4, 4, 4, 4, 4, 4, 4,
        3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3,
        2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2,
        2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2,
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
    ];

    private static readonly byte[] LutRangeM1 =
    [
        127,
        127, 191,
        127, 159, 191, 223,
        127, 143, 159, 175, 191, 207, 223, 239,
        127, 135, 143, 151, 159, 167, 175, 183, 191, 199, 207, 215, 223, 231, 239, 247,
        127, 131, 135, 139, 143, 147, 151, 155, 159, 163, 167, 171, 175, 179, 183, 187,
        191, 195, 199, 203, 207, 211, 215, 219, 223, 227, 231, 235, 239, 243, 247, 251,
        127, 129, 131, 133, 135, 137, 139, 141, 143, 145, 147, 149, 151, 153, 155, 157,
        159, 161, 163, 165, 167, 169, 171, 173, 175, 177, 179, 181, 183, 185, 187, 189,
        191, 193, 195, 197, 199, 201, 203, 205, 207, 209, 211, 213, 215, 217, 219, 221,
        223, 225, 227, 229, 231, 233, 235, 237, 239, 241, 243, 245, 247, 249, 251, 253,
    ];

    private byte[] _buf = [];
    private int _pos;
    private int _end;
    private uint _rangeM1;
    private uint _bits;
    private int _nBits;

    /// <summary>Whether a bit was asked for after the last byte.</summary>
    internal bool UnexpectedEof { get; private set; }

    /// <summary>Starts reading the <paramref name="length"/> bytes of <paramref name="buffer"/> from <paramref name="start"/>.</summary>
    internal void Init(byte[] buffer, int start, int length)
    {
        _buf = buffer;
        _pos = start;
        _end = start + length;
        _rangeM1 = 254;
        _bits = 0;
        _nBits = 0;
        UnexpectedEof = false;
    }

    /// <summary>The next bit, given the probability (0 to 255, out of 256) that it is 0.</summary>
    internal bool ReadBit(int prob)
    {
        if (_nBits < 8)
        {
            if (_pos >= _end)
            {
                UnexpectedEof = true;
                return false;
            }

            uint x = _buf[_pos];
            _bits |= x << (8 - _nBits);
            _pos++;
            _nBits += 8;
        }

        uint split = (((_rangeM1 * (uint)prob) >> 8) + 1);
        bool bit = _bits >= split << 8;
        if (bit)
        {
            _rangeM1 -= split;
            _bits -= split << 8;
        }
        else
        {
            _rangeM1 = split - 1;
        }

        if (_rangeM1 < 127)
        {
            int shift = LutShift[_rangeM1];
            _rangeM1 = LutRangeM1[_rangeM1];
            _bits <<= shift;
            _nBits -= shift;
        }

        return bit;
    }

    /// <summary>The next <paramref name="n"/>-bit unsigned integer.</summary>
    internal uint ReadUint(int prob, int n)
    {
        uint u = 0;
        while (n > 0)
        {
            n--;
            if (ReadBit(prob))
            {
                u |= 1u << n;
            }
        }

        return u;
    }

    /// <summary>The next <paramref name="n"/>-bit signed integer (magnitude, then sign).</summary>
    internal int ReadInt(int prob, int n)
    {
        int u = (int)ReadUint(prob, n);
        return ReadBit(prob) ? -u : u;
    }

    /// <summary>The next signed integer in an encoding where zero is the likely result.</summary>
    internal int ReadOptionalInt(int prob, int n) => ReadBit(prob) ? ReadInt(prob, n) : 0;
}
