// Copyright 2014 The Go Authors. All rights reserved.
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
// A canonical Huffman tree with a look-up table for the codes of up to 7 bits, as the VP8L format uses (section 5 of the WebP lossless specification), after golang.org/x/image/vp8l (huffman.go).
// Unlike the original, a tree must be complete (as libwebp insists): an incomplete or over-subscribed set of code lengths is refused, so that reading a symbol can never end in a node that has no children.

namespace Pdfwright.Images;

/// <summary>A Huffman tree of at most 2 * (the number of symbols) - 1 nodes.</summary>
internal sealed class Vp8lHuffmanTree
{
    /// <summary>The log-2 size of the look-up table.</summary>
    private const int LutSize = 7;
    private const int LutMask = (1 << LutSize) - 1;
    private const int LeafNode = -1;

    /// <summary>The longest code VP8L allows.</summary>
    private const int MaxAllowedCodeLength = 15;

    private static readonly byte[] ReverseBits = MakeReverseBits();

    // The nodes: for each, the symbol it holds, and Children, which is the index of the first of its two children when positive, 0 for a node that is not set yet and -1 for a leaf.
    private HNode[] _nodes = [];
    private int _count;

    // The look-up table: x is the next LutSize bits of the stream. The low 8 bits of Lut[x] are 1 plus the number of bits of the next code, or 0 if that code needs more than LutSize bits.
    // The high 24 bits are the symbol, if the code needs LutSize bits or fewer, or the index of the node to start from.
    private readonly uint[] _lut = new uint[1 << LutSize];

    /// <summary>The number of table entries (nodes and look-up entries) of a tree with this many symbols, which the decoder counts against its budget before the tree is built.</summary>
    internal static long Cost(int nSymbols) => (2L * nSymbols) + (1 << LutSize);

    private static byte[] MakeReverseBits()
    {
        byte[] table = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            int v = 0;
            for (int b = 0; b < 8; b++)
            {
                if ((i & (1 << b)) != 0)
                {
                    v |= 0x80 >> b;
                }
            }

            table[i] = (byte)v;
        }

        return table;
    }

    private static ImageFormatException Invalid() => new("The WebP's lossless data has an invalid Huffman tree.");

    // Inserts a symbol whose code is the least significant codeLength bits of code.
    private void Insert(uint symbol, uint code, uint codeLength)
    {
        if (symbol > 0xffff || codeLength > 0xfe)
        {
            throw Invalid();
        }

        uint baseCode;
        if (codeLength > LutSize)
        {
            baseCode = (uint)ReverseBits[(code >> (int)(codeLength - LutSize)) & 0xff] >> (8 - LutSize);
        }
        else
        {
            baseCode = (uint)ReverseBits[code & 0xff] >> (int)(8 - codeLength);
            for (uint i = 0; i < 1u << (LutSize - (int)codeLength); i++)
            {
                _lut[baseCode | (i << (int)codeLength)] = (symbol << 8) | (codeLength + 1);
            }
        }

        uint n = 0;
        for (int jump = LutSize; codeLength > 0;)
        {
            codeLength--;
            if (n >= _count)
            {
                throw Invalid();
            }

            switch (_nodes[n].Children)
            {
                case LeafNode:
                    throw Invalid();
                case 0:
                    if (_count == _nodes.Length)
                    {
                        throw Invalid();
                    }

                    // Create two empty child nodes.
                    _nodes[n].Children = _count;
                    _count += 2;
                    break;
            }

            n = (uint)_nodes[n].Children + (1 & (code >> (int)codeLength));
            jump--;
            if (jump == 0 && _lut[baseCode] == 0)
            {
                _lut[baseCode] = n << 8;
            }
        }

        if (n >= _count)
        {
            throw Invalid();
        }

        switch (_nodes[n].Children)
        {
            case LeafNode:
                break;
            case 0:
                // Turn the uninitialised node into a leaf.
                _nodes[n].Children = LeafNode;
                break;
            default:
                throw Invalid();
        }

        _nodes[n].Symbol = symbol;
    }

    /// <summary>Builds the tree of the canonical code the code lengths describe (a length of 0 is a symbol that is not used).</summary>
    internal void Build(uint[] codeLengths)
    {
        uint nSymbols = 0;
        uint lastSymbol = 0;
        long kraft = 0;
        for (int symbol = 0; symbol < codeLengths.Length; symbol++)
        {
            uint cl = codeLengths[symbol];
            if (cl != 0)
            {
                if (cl > MaxAllowedCodeLength)
                {
                    throw Invalid();
                }

                nSymbols++;
                lastSymbol = (uint)symbol;
                kraft += 1L << (MaxAllowedCodeLength - (int)cl);
            }
        }

        if (nSymbols == 0)
        {
            throw Invalid();
        }

        _nodes = new HNode[(2 * nSymbols) - 1];
        _count = 1;
        if (nSymbols == 1)
        {
            // The trivial case: a symbol that needs no bits.
            Insert(lastSymbol, 0, 0);
            return;
        }

        // The code must be complete: the lengths fill the code space exactly.
        if (kraft != 1L << MaxAllowedCodeLength)
        {
            throw Invalid();
        }

        uint[] histogram = new uint[MaxAllowedCodeLength + 1];
        foreach (uint cl in codeLengths)
        {
            histogram[cl]++;
        }

        histogram[0] = 0;
        uint[] nextCodes = new uint[MaxAllowedCodeLength + 1];
        uint currCode = 0;
        for (int cl = 1; cl < nextCodes.Length; cl++)
        {
            currCode = (currCode + histogram[cl - 1]) << 1;
            nextCodes[cl] = currCode;
        }

        for (int symbol = 0; symbol < codeLengths.Length; symbol++)
        {
            uint cl = codeLengths[symbol];
            if (cl > 0)
            {
                Insert((uint)symbol, nextCodes[cl], cl);
                nextCodes[cl]++;
            }
        }
    }

    /// <summary>Builds a tree of one or two symbols (the "simple" code lengths of section 5.2.2).</summary>
    internal void BuildSimple(uint nSymbols, uint symbol0, uint symbol1, uint alphabetSize)
    {
        _nodes = new HNode[(2 * nSymbols) - 1];
        _count = 1;
        for (uint i = 0; i < nSymbols; i++)
        {
            uint symbol = i == 0 ? symbol0 : symbol1;
            if (symbol >= alphabetSize)
            {
                throw Invalid();
            }

            Insert(symbol, i, nSymbols - 1);
        }
    }

    /// <summary>Reads the next symbol from the bit stream.</summary>
    internal uint Next(Vp8lBits d)
    {
        uint n = 0;

        // Read enough bits to use the look-up table. When there are no more bytes the symbol may still be in the bits that are left: the tree is walked instead.
        bool useLut = true;
        if (d.NBits < LutSize)
        {
            int c = d.TryReadByte();
            if (c < 0)
            {
                useLut = false;
            }
            else
            {
                d.Bits |= (uint)c << d.NBits;
                d.NBits += 8;
            }
        }

        if (useLut)
        {
            n = _lut[d.Bits & LutMask];
            uint b = n & 0xff;
            if (b != 0)
            {
                b--;
                d.Bits >>= (int)b;
                d.NBits -= (int)b;
                return n >> 8;
            }

            n >>= 8;
            d.Bits >>= LutSize;
            d.NBits -= LutSize;
        }

        while (_nodes[n].Children != LeafNode)
        {
            if (d.NBits == 0)
            {
                int c = d.TryReadByte();
                if (c < 0)
                {
                    throw Vp8lBits.Truncated();
                }

                d.Bits = (uint)c;
                d.NBits = 8;
            }

            n = (uint)_nodes[n].Children + (d.Bits & 1);
            d.Bits >>= 1;
            d.NBits--;
        }

        return _nodes[n].Symbol;
    }

    private struct HNode
    {
        internal uint Symbol;
        internal int Children;
    }
}
