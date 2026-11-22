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
// A decoder of the VP8L lossless image format (the WebP lossless bitstream specification), after golang.org/x/image/vp8l (decode.go), with the limits of this library added.

namespace Pdfwright.Images;

/// <summary>The bit stream of a VP8L image: bits are read from the least significant end of each byte.</summary>
internal sealed class Vp8lBits
{
    private readonly byte[] _data;
    private int _pos;
    private readonly int _end;

    internal Vp8lBits(byte[] data, int start, int length)
    {
        _data = data;
        _pos = start;
        _end = start + length;
    }

    /// <summary>The bits that were read from the data and not used yet, the next one in the least significant place.</summary>
    internal uint Bits;

    /// <summary>The number of bits in <see cref="Bits"/>.</summary>
    internal int NBits;

    internal static ImageFormatException Truncated() => new("The WebP's lossless data is cut short.");

    /// <summary>The next byte of the data, or -1 at the end.</summary>
    internal int TryReadByte() => _pos < _end ? _data[_pos++] : -1;

    /// <summary>Reads the next <paramref name="n"/> bits (at most 24).</summary>
    internal uint Read(int n)
    {
        while (NBits < n)
        {
            int c = TryReadByte();
            if (c < 0)
            {
                throw Truncated();
            }

            Bits |= (uint)c << NBits;
            NBits += 8;
        }

        uint u = Bits & ((1u << n) - 1);
        Bits >>= n;
        NBits -= n;
        return u;
    }
}

/// <summary>
/// Decodes a VP8L bitstream (the payload of a <c>VP8L</c> chunk, or the lossless-coded alpha plane of a lossy image) to 8-bit samples in red, green, blue, alpha order, 4 to a pixel. The size is checked against the pixel limit right after the header,
/// the number of Huffman tree groups and the memory of the Huffman trees are capped, and every failure is an <see cref="ImageFormatException"/>.
/// </summary>
internal sealed class Vp8lDecoder
{
    // The multiplier of the colour cache's hash function (section 4.2.3).
    private const uint ColorCacheMultiplier = 0x1e35a7bd;

    private const int TransformPredictor = 0;
    private const int TransformCrossColor = 1;
    private const int TransformSubtractGreen = 2;
    private const int TransformColorIndexing = 3;
    private const int NTransformTypes = 4;

    private const int NLiteralCodes = 256;
    private const int NLengthCodes = 24;
    private const int NDistanceCodes = 40;
    private const int NHuff = 5;

    /// <summary>
    /// The most Huffman tree groups an image may use: libwebp's encoder writes at most 2,600 (its MAX_HUFF_IMAGE_SIZE), and an image may name up to 65,535 of them, which with trees of the largest size is more than 3 GB.
    /// </summary>
    internal const int MaxHuffmanGroups = 2600;

    /// <summary>The most table entries (nodes and look-up entries, 8 bytes each at most) the trees of one image may hold in all: 64 MB.</summary>
    internal const long MaxHuffmanEntries = 1L << 23;

    private static readonly byte[] DistanceMapTable =
    [
        0x18, 0x07, 0x17, 0x19, 0x28, 0x06, 0x27, 0x29, 0x16, 0x1a,
        0x26, 0x2a, 0x38, 0x05, 0x37, 0x39, 0x15, 0x1b, 0x36, 0x3a,
        0x25, 0x2b, 0x48, 0x04, 0x47, 0x49, 0x14, 0x1c, 0x35, 0x3b,
        0x46, 0x4a, 0x24, 0x2c, 0x58, 0x45, 0x4b, 0x34, 0x3c, 0x03,
        0x57, 0x59, 0x13, 0x1d, 0x56, 0x5a, 0x23, 0x2d, 0x44, 0x4c,
        0x55, 0x5b, 0x33, 0x3d, 0x68, 0x02, 0x67, 0x69, 0x12, 0x1e,
        0x66, 0x6a, 0x22, 0x2e, 0x54, 0x5c, 0x43, 0x4d, 0x65, 0x6b,
        0x32, 0x3e, 0x78, 0x01, 0x77, 0x79, 0x53, 0x5d, 0x11, 0x1f,
        0x64, 0x6c, 0x42, 0x4e, 0x76, 0x7a, 0x21, 0x2f, 0x75, 0x7b,
        0x31, 0x3f, 0x63, 0x6d, 0x52, 0x5e, 0x00, 0x74, 0x7c, 0x41,
        0x4f, 0x10, 0x20, 0x62, 0x6e, 0x30, 0x73, 0x7d, 0x51, 0x5f,
        0x40, 0x72, 0x7e, 0x61, 0x6f, 0x50, 0x71, 0x7f, 0x60, 0x70,
    ];

    // The order of the code length code lengths, and the extra bits and offsets of the repeat codes (the end of section 5.2.2).
    private static readonly byte[] CodeLengthCodeOrder = [17, 18, 0, 1, 2, 3, 4, 5, 16, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15];
    private static readonly byte[] RepeatBits = [2, 3, 7];
    private static readonly byte[] RepeatOffsets = [3, 3, 11];

    private static readonly uint[] AlphabetSizes = [NLiteralCodes + NLengthCodes, NLiteralCodes, NLiteralCodes, NLiteralCodes, NDistanceCodes];

    private readonly Vp8lBits _bits;
    private long _huffmanEntries;

    internal Vp8lDecoder(byte[] data, int start, int length)
    {
        _bits = new Vp8lBits(data, start, length);
    }

    /// <summary>The width of the image in pixels.</summary>
    internal int Width { get; private set; }

    /// <summary>The height of the image in pixels.</summary>
    internal int Height { get; private set; }

    /// <summary>The "alpha is used" hint of the header (the pixels' alpha values are what counts).</summary>
    internal bool AlphaHint { get; private set; }

    /// <summary>Reads the header, and refuses an image of more pixels than <paramref name="maxPixels"/> before anything is allocated for it.</summary>
    internal void ReadHeader(long maxPixels)
    {
        if (_bits.Read(8) != 0x2f)
        {
            throw new ImageFormatException("The WebP's lossless data has a wrong signature.");
        }

        int width = (int)_bits.Read(14) + 1;
        int height = (int)_bits.Read(14) + 1;
        AlphaHint = _bits.Read(1) != 0;
        if (_bits.Read(3) != 0)
        {
            throw new ImageFormatException("The WebP's lossless data has a version that is not known.");
        }

        ImageSource.CheckPixels("WebP", width, height, maxPixels);
        Width = width;
        Height = height;
    }

    /// <summary>Sets the size of an image that has no header (the lossless alpha plane of a lossy image, whose size is that of the picture).</summary>
    internal void SetSize(int width, int height)
    {
        Width = width;
        Height = height;
    }

    private static int NTiles(int size, int bits) => (size + (1 << bits) - 1) >> bits;

    private static ImageFormatException Invalid(string what) => new("The WebP's lossless data is damaged: " + what + ".");

    /// <summary>Decodes the image: the transforms, then the pixels, then the inverse transforms.</summary>
    internal byte[] Decode()
    {
        int w = Width;
        int h = Height;
        Vp8lTransform?[] transforms = new Vp8lTransform?[NTransformTypes];
        Vp8lTransform[] ordered = new Vp8lTransform[NTransformTypes];
        int nTransforms = 0;
        while (_bits.Read(1) != 0)
        {
            Vp8lTransform t = DecodeTransform(w, h, out w);
            if (transforms[t.Type] is not null)
            {
                throw Invalid("a transform is repeated");
            }

            transforms[t.Type] = t;
            ordered[nTransforms++] = t;
        }

        byte[] pix = DecodePix(w, h, topLevel: true);
        for (int i = nTransforms - 1; i >= 0; i--)
        {
            pix = ordered[i].Inverse(pix, h);
        }

        return pix;
    }

    // Decodes the next transform and the width of the image after it (before its inverse), section 3.
    private Vp8lTransform DecodeTransform(int w, int h, out int newWidth)
    {
        Vp8lTransform t = new() { OldWidth = w, Type = (int)_bits.Read(2) };
        newWidth = w;
        switch (t.Type)
        {
            case TransformPredictor:
            case TransformCrossColor:
                t.Bits = (int)_bits.Read(3) + 2;
                t.Pix = DecodePix(NTiles(w, t.Bits), NTiles(h, t.Bits), topLevel: false);
                break;

            case TransformSubtractGreen:
                break;

            case TransformColorIndexing:
            {
                int nColors = (int)_bits.Read(8) + 1;
                t.Bits = nColors <= 2 ? 3 : nColors <= 4 ? 2 : nColors <= 16 ? 1 : 0;
                newWidth = NTiles(w, t.Bits);
                byte[] pix = DecodePix(nColors, 1, topLevel: false);
                for (int p = 4; p < pix.Length; p += 4)
                {
                    pix[p] += pix[p - 4];
                    pix[p + 1] += pix[p - 3];
                    pix[p + 2] += pix[p - 2];
                    pix[p + 3] += pix[p - 1];
                }

                // The specification says that "if the index is equal or larger than color_table_size, the argb color value should be set to 0x00000000 (transparent black)": the table has 256 entries.
                t.Pix = new byte[4 * 256];
                Array.Copy(pix, t.Pix, pix.Length);
                break;
            }
        }

        return t;
    }

    // Decodes the code lengths of a Huffman tree, which are themselves Huffman coded (section 5.2.2).
    private void DecodeCodeLengths(uint[] dst, uint[] codeLengthCodeLengths)
    {
        Vp8lHuffmanTree h = new();
        h.Build(codeLengthCodeLengths);

        int maxSymbol = dst.Length;
        if (_bits.Read(1) != 0)
        {
            int n = 2 + (2 * (int)_bits.Read(3));
            maxSymbol = (int)_bits.Read(n) + 2;
            if (maxSymbol > dst.Length)
            {
                throw Invalid("invalid code lengths");
            }
        }

        // The specification says that "if code 16 [meaning repeat] is used before a non-zero value has been emitted, a value of 8 is repeated."
        uint prevCodeLength = 8;
        for (int symbol = 0; symbol < dst.Length;)
        {
            if (maxSymbol == 0)
            {
                break;
            }

            maxSymbol--;
            uint codeLength = h.Next(_bits);
            if (codeLength < 16)
            {
                dst[symbol++] = codeLength;
                if (codeLength != 0)
                {
                    prevCodeLength = codeLength;
                }

                continue;
            }

            uint repeat = _bits.Read(RepeatBits[codeLength - 16]) + RepeatOffsets[codeLength - 16];
            if (symbol + repeat > dst.Length)
            {
                throw Invalid("invalid code lengths");
            }

            // A code length of 16 repeats the previous non-zero code, one of 17 or 18 repeats zeros.
            uint cl = codeLength == 16 ? prevCodeLength : 0;
            for (; repeat > 0; repeat--)
            {
                dst[symbol++] = cl;
            }
        }
    }

    // Decodes a Huffman tree into h, or decodes and discards it when h is null.
    private void DecodeHuffmanTree(Vp8lHuffmanTree? h, uint alphabetSize)
    {
        if (_bits.Read(1) != 0)
        {
            // A simple code: one or two symbols.
            uint nSymbols = _bits.Read(1) + 1;
            int firstSymbolLengthCode = (int)((7 * _bits.Read(1)) + 1);
            uint symbol0 = _bits.Read(firstSymbolLengthCode);
            uint symbol1 = nSymbols == 2 ? _bits.Read(8) : 0;
            if (h is null)
            {
                return;
            }

            Spend(Vp8lHuffmanTree.Cost((int)nSymbols));
            h.BuildSimple(nSymbols, symbol0, symbol1, alphabetSize);
            return;
        }

        int nCodes = (int)_bits.Read(4) + 4;
        if (nCodes > CodeLengthCodeOrder.Length)
        {
            throw Invalid("invalid Huffman tree");
        }

        uint[] codeLengthCodeLengths = new uint[CodeLengthCodeOrder.Length];
        for (int i = 0; i < nCodes; i++)
        {
            codeLengthCodeLengths[CodeLengthCodeOrder[i]] = _bits.Read(3);
        }

        uint[] codeLengths = new uint[alphabetSize];
        DecodeCodeLengths(codeLengths, codeLengthCodeLengths);
        if (h is null)
        {
            return;
        }

        int used = 0;
        foreach (uint cl in codeLengths)
        {
            if (cl != 0)
            {
                used++;
            }
        }

        Spend(Vp8lHuffmanTree.Cost(used));
        h.Build(codeLengths);
    }

    private void Spend(long entries)
    {
        _huffmanEntries += entries;
        if (_huffmanEntries > MaxHuffmanEntries)
        {
            throw Invalid("its Huffman trees are too large");
        }
    }

    // Decodes the one or more groups of 5 Huffman trees that decode the pixels. With one group for the whole image, hPix is null and hBits is 0; with more, hPix maps tiles to groups and hBits is the log-2 of the tile size.
    private Vp8lHuffmanTree[][] DecodeHuffmanGroups(int w, int h, bool topLevel, int ccBits, out byte[]? hPix, out int hBits)
    {
        hPix = null;
        hBits = 0;

        // hPix maps tiles to group indices. The number of groups is the greatest index plus one: unused groups may be in the data, and are decoded and dropped. When the greatest index is large the groups that are
        // named are numbered again from 0, so that nothing is allocated for the ones that are not (mapping[i] is -1 for a group that is not named).
        short[]? mapping = null;
        int maxHGroupIndex = 0;
        int numHGroups = 1;

        if (topLevel && _bits.Read(1) != 0)
        {
            hBits = (int)_bits.Read(3) + 2;
            int tileW = NTiles(w, hBits);
            int tileH = NTiles(h, hBits);
            long numTiles = (long)tileH * tileW;
            hPix = DecodePix(tileW, tileH, topLevel: false);
            for (int p = 0; p < hPix.Length; p += 4)
            {
                int i = (hPix[p] << 8) | hPix[p + 1];
                if (maxHGroupIndex < i)
                {
                    maxHGroupIndex = i;
                }
            }

            // The format allows 65,535 groups, but libwebp writes at most 2,600 and so does the limit here.
            if (maxHGroupIndex >= MaxHuffmanGroups)
            {
                throw Invalid("too many Huffman trees");
            }

            if (maxHGroupIndex >= 1000 || maxHGroupIndex >= numTiles)
            {
                mapping = new short[maxHGroupIndex + 1];
                Array.Fill(mapping, (short)-1);
                numHGroups = 0;
                for (int p = 0; p < hPix.Length; p += 4)
                {
                    int i = (hPix[p] << 8) | hPix[p + 1];
                    if (mapping[i] == -1)
                    {
                        mapping[i] = (short)numHGroups;
                        numHGroups++;
                    }

                    hPix[p] = (byte)(mapping[i] >> 8);
                    hPix[p + 1] = (byte)mapping[i];
                }
            }
            else
            {
                numHGroups = maxHGroupIndex + 1;
            }
        }

        Vp8lHuffmanTree[][] hGroups = new Vp8lHuffmanTree[numHGroups][];
        for (int i = 0; i <= maxHGroupIndex; i++)
        {
            Vp8lHuffmanTree[]? hg = null;
            if (mapping is null)
            {
                hg = hGroups[i] = NewGroup();
            }
            else if (mapping[i] != -1)
            {
                hg = hGroups[mapping[i]] = NewGroup();
            }

            for (int j = 0; j < NHuff; j++)
            {
                uint alphabetSize = AlphabetSizes[j];
                if (j == 0 && ccBits > 0)
                {
                    alphabetSize += 1u << ccBits;
                }

                DecodeHuffmanTree(hg?[j], alphabetSize);
            }
        }

        return hGroups;
    }

    private static Vp8lHuffmanTree[] NewGroup()
    {
        Vp8lHuffmanTree[] group = new Vp8lHuffmanTree[NHuff];
        for (int i = 0; i < NHuff; i++)
        {
            group[i] = new Vp8lHuffmanTree();
        }

        return group;
    }

    // Maps a distance of the LZ77 backward references to a two-dimensional pixel offset (section 4.2.2).
    private static int DistanceMap(int w, uint code)
    {
        if (code > DistanceMapTable.Length)
        {
            return (int)code - DistanceMapTable.Length;
        }

        int distCode = DistanceMapTable[code - 1];
        int yOffset = distCode >> 4;
        int xOffset = 8 - (distCode & 0xf);
        int d = (yOffset * w) + xOffset;
        return d >= 1 ? d : 1;
    }

    // The next LZ77 parameter, a length or a distance (section 4.2.2).
    private uint Lz77Param(uint symbol)
    {
        if (symbol < 4)
        {
            return symbol + 1;
        }

        int extraBits = (int)((symbol - 2) >> 1);
        uint offset = (2 + (symbol & 1)) << extraBits;
        uint n = _bits.Read(extraBits);
        return offset + n + 1;
    }

    // Decodes pixel data of w by h, in red, green, blue, alpha order (section 5.2.2).
    private byte[] DecodePix(int w, int h, bool topLevel)
    {
        // The colour cache's parameters.
        int ccBits = 0;
        int ccShift = 0;
        uint[] ccEntries = [];
        if (_bits.Read(1) != 0)
        {
            ccBits = (int)_bits.Read(4);
            if (ccBits < 1 || ccBits > 11)
            {
                throw Invalid("invalid colour cache parameters");
            }

            ccShift = 32 - ccBits;
            ccEntries = new uint[1 << ccBits];
        }

        // The Huffman groups.
        Vp8lHuffmanTree[][] hGroups = DecodeHuffmanGroups(w, h, topLevel, ccBits, out byte[]? hPix, out int hBits);
        int hMask = 0;
        int tilesPerRow = 0;
        if (hBits != 0)
        {
            hMask = (1 << hBits) - 1;
            tilesPerRow = NTiles(w, hBits);
        }

        // The pixels.
        long size = 4L * w * h;
        if (size > int.MaxValue)
        {
            throw Invalid("the image is too large");
        }

        byte[] pix = new byte[size];
        int p = 0;
        int cachedP = 0;
        int x = 0;
        int y = 0;
        Vp8lHuffmanTree[] hg = hGroups[0];
        bool lookupHG = hMask != 0;
        while (p < pix.Length)
        {
            if (lookupHG)
            {
                int i = 4 * ((tilesPerRow * (y >> hBits)) + (x >> hBits));
                hg = hGroups[(hPix![i] << 8) | hPix[i + 1]];
            }

            uint green = hg[0].Next(_bits);
            if (green < NLiteralCodes)
            {
                // A literal pixel.
                uint red = hg[1].Next(_bits);
                uint blue = hg[2].Next(_bits);
                uint alpha = hg[3].Next(_bits);
                pix[p] = (byte)red;
                pix[p + 1] = (byte)green;
                pix[p + 2] = (byte)blue;
                pix[p + 3] = (byte)alpha;
                p += 4;

                x++;
                if (x == w)
                {
                    x = 0;
                    y++;
                }

                lookupHG = hMask != 0 && (x & hMask) == 0;
            }
            else if (green < NLiteralCodes + NLengthCodes)
            {
                // An LZ77 backward reference.
                uint length = Lz77Param(green - NLiteralCodes);
                uint distSym = hg[4].Next(_bits);
                uint distCode = Lz77Param(distSym);
                int dist = DistanceMap(w, distCode);
                int pEnd = p + (4 * (int)length);
                int q = p - (4 * dist);
                int qEnd = pEnd - (4 * dist);
                if (pix.Length < pEnd || q < 0 || pix.Length < qEnd)
                {
                    throw Invalid("invalid LZ77 parameters");
                }

                for (; p < pEnd; p++, q++)
                {
                    pix[p] = pix[q];
                }

                x += (int)length;
                while (x >= w)
                {
                    x -= w;
                    y++;
                }

                lookupHG = hMask != 0;
            }
            else
            {
                // A colour cache lookup. First the previous pixels are put in the cache (ARGB order).
                for (; cachedP < p; cachedP += 4)
                {
                    uint argb = ((uint)pix[cachedP] << 16) | ((uint)pix[cachedP + 1] << 8) | pix[cachedP + 2] | ((uint)pix[cachedP + 3] << 24);
                    ccEntries[(argb * ColorCacheMultiplier) >> ccShift] = argb;
                }

                green -= NLiteralCodes + NLengthCodes;
                if (green >= ccEntries.Length)
                {
                    throw Invalid("invalid colour cache index");
                }

                uint cached = ccEntries[green];
                pix[p] = (byte)(cached >> 16);
                pix[p + 1] = (byte)(cached >> 8);
                pix[p + 2] = (byte)cached;
                pix[p + 3] = (byte)(cached >> 24);
                p += 4;

                x++;
                if (x == w)
                {
                    x = 0;
                    y++;
                }

                lookupHG = hMask != 0 && (x & hMask) == 0;
            }
        }

        return pix;
    }
}
