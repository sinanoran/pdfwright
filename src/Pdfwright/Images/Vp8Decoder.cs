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
// A decoder of the VP8 lossy image format (RFC 6386), after golang.org/x/image/vp8 (decode.go, quant.go, token.go and the parts of reconstruct.go that parse), with the limits of this library added.

namespace Pdfwright.Images;

/// <summary>
/// Decodes the key frame of a VP8 bitstream (the payload of a <c>VP8 </c> chunk of a WebP file) into three planes of 8-bit samples, Y at full size and Cb and Cr at half size (4:2:0), the loop filter applied.
/// The size is checked against the pixel limit right after the frame header and before anything is allocated; every failure is an <see cref="ImageFormatException"/>.
/// </summary>
internal sealed partial class Vp8Decoder
{
    private const int NSegment = 4;
    private const int PlaneY1WithY2 = 0;
    private const int PlaneY2 = 1;
    private const int PlaneUV = 2;
    private const int PlaneY1SansY2 = 3;
    private const int ProbsPerPlane = 8 * 3 * 11;

    private readonly byte[] _data;
    private int _pos;
    private readonly int _end;

    private int _firstPartitionLen;
    private int _mbw;
    private int _mbh;

    // The segment header (section 9.3).
    private bool _useSegment;
    private bool _updateMap;
    private bool _relativeDelta;
    private readonly int[] _segQuantizer = new int[NSegment];
    private readonly int[] _segFilterStrength = new int[NSegment];
    private readonly int[] _segProb = [255, 255, 255];

    // The filter header (section 9.4).
    private bool _filterSimple;
    private int _filterLevel;
    private int _filterSharpness;
    private bool _useLfDelta;
    private readonly int[] _refLfDelta = new int[4];
    private readonly int[] _modeLfDelta = new int[4];

    private readonly Vp8Partition _fp = new();
    private readonly Vp8Partition[] _op = [new(), new(), new(), new(), new(), new(), new(), new()];
    private int _nOP;

    private readonly Quant[] _quant = new Quant[NSegment];
    private readonly byte[] _tokenProb = new byte[4 * ProbsPerPlane];
    private bool _useSkipProb;
    private int _skipProb;

    // Loop filter parameters, indexed [segment * 2 + (0 for 16x16 luma prediction, 1 otherwise)].
    private readonly FilterParam[] _filterParams = new FilterParam[NSegment * 2];
    private FilterParam[] _perMbFilterParams = [];

    /// <summary>The width of the frame in pixels.</summary>
    internal int Width { get; private set; }

    /// <summary>The height of the frame in pixels.</summary>
    internal int Height { get; private set; }

    /// <summary>The luma samples, <see cref="YStride"/> to a row, <see cref="Height"/> rows of <see cref="Width"/> samples of which are the picture.</summary>
    internal byte[] Y { get; private set; } = [];

    /// <summary>The blue-difference samples, half the width and height of <see cref="Y"/> (rounded up).</summary>
    internal byte[] Cb { get; private set; } = [];

    /// <summary>The red-difference samples.</summary>
    internal byte[] Cr { get; private set; } = [];

    /// <summary>The distance between two rows of <see cref="Y"/>.</summary>
    internal int YStride { get; private set; }

    /// <summary>The distance between two rows of <see cref="Cb"/> and <see cref="Cr"/>.</summary>
    internal int CStride { get; private set; }

    /// <summary>Starts reading the <paramref name="length"/> bytes of a <c>VP8 </c> chunk that begin at <paramref name="start"/> in <paramref name="data"/>.</summary>
    internal Vp8Decoder(byte[] data, int start, int length)
    {
        _data = data;
        _pos = start;
        _end = start + length;
    }

    /// <summary>Reads the frame header and refuses a frame that is not a key frame, is not shown, or has more pixels than <paramref name="maxPixels"/>. Nothing is allocated for the pixels yet.</summary>
    internal void ReadFrameHeader(long maxPixels)
    {
        // All frame headers are at least 3 bytes long, and the key frame's is 10.
        if (_end - _pos < 10)
        {
            throw new ImageFormatException("The WebP's VP8 data is cut short: it ends inside the frame header.");
        }

        int b0 = _data[_pos];
        bool keyFrame = (b0 & 1) == 0;
        int version = (b0 >> 1) & 7;
        bool showFrame = ((b0 >> 4) & 1) == 1;
        _firstPartitionLen = (b0 >> 5) | (_data[_pos + 1] << 3) | (_data[_pos + 2] << 11);
        if (!keyFrame)
        {
            throw new ImageFormatException("The WebP's VP8 frame is not a key frame, which a still image must be.");
        }

        if (_data[_pos + 3] != 0x9d || _data[_pos + 4] != 0x01 || _data[_pos + 5] != 0x2a)
        {
            throw new ImageFormatException("The WebP's VP8 frame has a wrong start code.");
        }

        if (version > 3 || !showFrame)
        {
            throw new ImageFormatException("The WebP's VP8 frame has an unknown version or is not meant to be shown.");
        }

        int width = ((_data[_pos + 7] & 0x3f) << 8) | _data[_pos + 6];
        int height = ((_data[_pos + 9] & 0x3f) << 8) | _data[_pos + 8];
        _pos += 10;
        if (width == 0 || height == 0)
        {
            throw new ImageFormatException("The WebP's VP8 frame has no width or no height.");
        }

        ImageSource.CheckPixels("WebP", width, height, maxPixels);
        Width = width;
        Height = height;
        _mbw = (width + 15) >> 4;
        _mbh = (height + 15) >> 4;
    }

    /// <summary>Decodes the frame whose header <see cref="ReadFrameHeader"/> read, and applies the loop filter.</summary>
    internal void DecodeFrame()
    {
        // The headers and the partition sizes are checked before anything is allocated for the pixels, so that a few bytes claiming a huge picture cost nothing.
        ParseOtherHeaders();
        YStride = 16 * _mbw;
        CStride = 8 * _mbw;
        Y = new byte[(long)YStride * 16 * _mbh];
        Cb = new byte[(long)CStride * 8 * _mbh];
        Cr = new byte[(long)CStride * 8 * _mbh];
        _perMbFilterParams = new FilterParam[_mbw * _mbh];
        _upPred = new byte[_mbw * 4];
        _upNzMask = new byte[_mbw];
        _upNzY16 = new byte[_mbw];
        Array.Clear(_upPred);
        Array.Clear(_upNzMask);
        Array.Clear(_upNzY16);
        for (int mby = 0; mby < _mbh; mby++)
        {
            Array.Clear(_leftPred);
            _leftNzMask = 0;
            _leftNzY16 = 0;
            for (int mbx = 0; mbx < _mbw; mbx++)
            {
                bool skip = Reconstruct(mbx, mby);
                FilterParam fs = _filterParams[(_segment * 2) + (_usePredY16 ? 0 : 1)];
                fs.Inner = fs.Inner || !skip;
                _perMbFilterParams[(_mbw * mby) + mbx] = fs;
            }

            // As libwebp does at the end of every row: a frame whose data ends early is refused here, not decoded to the end from zero bits.
            CheckEof();
        }

        // Even if there are per-segment levels, section 15 says that "loop filtering must be skipped entirely if loop_filter_level at either the frame header level or macroblock override level is 0".
        if (_filterLevel != 0)
        {
            if (_filterSimple)
            {
                SimpleFilter();
            }
            else
            {
                NormalFilter();
            }
        }
    }

    private void CheckEof()
    {
        if (_fp.UnexpectedEof)
        {
            throw Truncated();
        }

        for (int i = 0; i < _nOP; i++)
        {
            if (_op[i].UnexpectedEof)
            {
                throw Truncated();
            }
        }
    }

    private static ImageFormatException Truncated() => new("The WebP's VP8 data is cut short: it ends before the last macroblock.");

    private void ParseOtherHeaders()
    {
        // The first partition.
        if (_firstPartitionLen > _end - _pos)
        {
            throw Truncated();
        }

        _fp.Init(_data, _pos, _firstPartitionLen);
        _pos += _firstPartitionLen;

        // Read and ignore the colour space and pixel clamp values, which specified in section 9.2 but are not used.
        _fp.ReadBit(Vp8Partition.UniformProb);
        _fp.ReadBit(Vp8Partition.UniformProb);
        Vp8Tables.DefaultTokenProb.CopyTo(_tokenProb);
        ParseSegmentHeader();
        ParseFilterHeader();
        ParseOtherPartitions();
        ParseQuant();

        // Read and ignore the refreshLastFrameBuffer bit (section 9.8), which applies to video only.
        _fp.ReadBit(Vp8Partition.UniformProb);
        ParseTokenProb();
        _useSkipProb = _fp.ReadBit(Vp8Partition.UniformProb);
        if (_useSkipProb)
        {
            _skipProb = (int)_fp.ReadUint(Vp8Partition.UniformProb, 8);
        }

        if (_fp.UnexpectedEof)
        {
            throw Truncated();
        }
    }

    // Section 9.3.
    private void ParseSegmentHeader()
    {
        _useSegment = _fp.ReadBit(Vp8Partition.UniformProb);
        if (!_useSegment)
        {
            _updateMap = false;
            return;
        }

        _updateMap = _fp.ReadBit(Vp8Partition.UniformProb);
        if (_fp.ReadBit(Vp8Partition.UniformProb))
        {
            _relativeDelta = !_fp.ReadBit(Vp8Partition.UniformProb);
            for (int i = 0; i < NSegment; i++)
            {
                _segQuantizer[i] = _fp.ReadOptionalInt(Vp8Partition.UniformProb, 7);
            }

            for (int i = 0; i < NSegment; i++)
            {
                _segFilterStrength[i] = _fp.ReadOptionalInt(Vp8Partition.UniformProb, 6);
            }
        }

        if (!_updateMap)
        {
            return;
        }

        for (int i = 0; i < 3; i++)
        {
            _segProb[i] = _fp.ReadBit(Vp8Partition.UniformProb) ? (int)_fp.ReadUint(Vp8Partition.UniformProb, 8) : 0xff;
        }
    }

    // Section 9.4.
    private void ParseFilterHeader()
    {
        _filterSimple = _fp.ReadBit(Vp8Partition.UniformProb);
        _filterLevel = (int)_fp.ReadUint(Vp8Partition.UniformProb, 6);
        _filterSharpness = (int)_fp.ReadUint(Vp8Partition.UniformProb, 3);
        _useLfDelta = _fp.ReadBit(Vp8Partition.UniformProb);
        if (_useLfDelta && _fp.ReadBit(Vp8Partition.UniformProb))
        {
            for (int i = 0; i < 4; i++)
            {
                _refLfDelta[i] = _fp.ReadOptionalInt(Vp8Partition.UniformProb, 6);
            }

            for (int i = 0; i < 4; i++)
            {
                _modeLfDelta[i] = _fp.ReadOptionalInt(Vp8Partition.UniformProb, 6);
            }
        }

        if (_filterLevel == 0)
        {
            return;
        }

        ComputeFilterParams();
    }

    // Section 9.5.
    private void ParseOtherPartitions()
    {
        const int maxNOP = 1 << 3;
        Span<int> partLens = stackalloc int[maxNOP];
        _nOP = 1 << (int)_fp.ReadUint(Vp8Partition.UniformProb, 2);

        // The final partition's length is implied by the remaining data and the lengths of the other partitions, which are 24-bit numbers.
        int n = 3 * (_nOP - 1);
        int remaining = _end - _pos;
        partLens[_nOP - 1] = remaining - n;
        if (partLens[_nOP - 1] < 0)
        {
            throw Truncated();
        }

        for (int i = 0; i < _nOP - 1; i++)
        {
            int at = _pos + (3 * i);
            int pl = _data[at] | (_data[at + 1] << 8) | (_data[at + 2] << 16);
            if (pl > partLens[_nOP - 1])
            {
                throw Truncated();
            }

            partLens[i] = pl;
            partLens[_nOP - 1] -= pl;
        }

        _pos += n;

        // Strictly speaking this is not in the specification: the final partition must fit a 24-bit length, as the others do.
        if (partLens[_nOP - 1] >= 1 << 24)
        {
            throw new ImageFormatException("The WebP's VP8 data has a partition that is too large.");
        }

        for (int i = 0; i < _nOP; i++)
        {
            _op[i].Init(_data, _pos, partLens[i]);
            _pos += partLens[i];
        }
    }

    // The dequantization tables are specified in section 14.1.
    private static readonly ushort[] DequantTableDC =
    [
        4, 5, 6, 7, 8, 9, 10, 10,
        11, 12, 13, 14, 15, 16, 17, 17,
        18, 19, 20, 20, 21, 21, 22, 22,
        23, 23, 24, 25, 25, 26, 27, 28,
        29, 30, 31, 32, 33, 34, 35, 36,
        37, 37, 38, 39, 40, 41, 42, 43,
        44, 45, 46, 46, 47, 48, 49, 50,
        51, 52, 53, 54, 55, 56, 57, 58,
        59, 60, 61, 62, 63, 64, 65, 66,
        67, 68, 69, 70, 71, 72, 73, 74,
        75, 76, 76, 77, 78, 79, 80, 81,
        82, 83, 84, 85, 86, 87, 88, 89,
        91, 93, 95, 96, 98, 100, 101, 102,
        104, 106, 108, 110, 112, 114, 116, 118,
        122, 124, 126, 128, 130, 132, 134, 136,
        138, 140, 143, 145, 148, 151, 154, 157,
    ];

    private static readonly ushort[] DequantTableAC =
    [
        4, 5, 6, 7, 8, 9, 10, 11,
        12, 13, 14, 15, 16, 17, 18, 19,
        20, 21, 22, 23, 24, 25, 26, 27,
        28, 29, 30, 31, 32, 33, 34, 35,
        36, 37, 38, 39, 40, 41, 42, 43,
        44, 45, 46, 47, 48, 49, 50, 51,
        52, 53, 54, 55, 56, 57, 58, 60,
        62, 64, 66, 68, 70, 72, 74, 76,
        78, 80, 82, 84, 86, 88, 90, 92,
        94, 96, 98, 100, 102, 104, 106, 108,
        110, 112, 114, 116, 119, 122, 125, 128,
        131, 134, 137, 140, 143, 146, 149, 152,
        155, 158, 161, 164, 167, 170, 173, 177,
        181, 185, 189, 193, 197, 201, 205, 209,
        213, 217, 221, 225, 229, 234, 239, 245,
        249, 254, 259, 264, 269, 274, 279, 284,
    ];

    private static int Clip(int x, int min, int max) => x < min ? min : x > max ? max : x;

    // Section 9.6.
    private void ParseQuant()
    {
        int baseQ0 = (int)_fp.ReadUint(Vp8Partition.UniformProb, 7);
        int dqy1DC = _fp.ReadOptionalInt(Vp8Partition.UniformProb, 4);
        const int dqy1AC = 0;
        int dqy2DC = _fp.ReadOptionalInt(Vp8Partition.UniformProb, 4);
        int dqy2AC = _fp.ReadOptionalInt(Vp8Partition.UniformProb, 4);
        int dquvDC = _fp.ReadOptionalInt(Vp8Partition.UniformProb, 4);
        int dquvAC = _fp.ReadOptionalInt(Vp8Partition.UniformProb, 4);
        for (int i = 0; i < NSegment; i++)
        {
            int q = baseQ0;
            if (_useSegment)
            {
                q = _relativeDelta ? q + _segQuantizer[i] : _segQuantizer[i];
            }

            Quant quant = default;
            quant.Y1Dc = DequantTableDC[Clip(q + dqy1DC, 0, 127)];
            quant.Y1Ac = DequantTableAC[Clip(q + dqy1AC, 0, 127)];
            quant.Y2Dc = DequantTableDC[Clip(q + dqy2DC, 0, 127)] * 2;
            quant.Y2Ac = DequantTableAC[Clip(q + dqy2AC, 0, 127)] * 155 / 100;
            if (quant.Y2Ac < 8)
            {
                quant.Y2Ac = 8;
            }

            // The 117 is not a typo: the specification's reference decoder clamps this value at 132, which is DequantTableDC[117].
            quant.UvDc = DequantTableDC[Clip(q + dquvDC, 0, 117)];
            quant.UvAc = DequantTableAC[Clip(q + dquvAC, 0, 127)];
            _quant[i] = quant;
        }
    }

    // Section 13.4.
    private void ParseTokenProb()
    {
        ReadOnlySpan<byte> update = Vp8Tables.TokenProbUpdateProb;
        for (int i = 0; i < _tokenProb.Length; i++)
        {
            if (_fp.ReadBit(update[i]))
            {
                _tokenProb[i] = (byte)_fp.ReadUint(Vp8Partition.UniformProb, 8);
            }
        }
    }

    private struct Quant
    {
        internal int Y1Dc;
        internal int Y1Ac;
        internal int Y2Dc;
        internal int Y2Ac;
        internal int UvDc;
        internal int UvAc;
    }

    private struct FilterParam
    {
        // The first three are thresholds the loop filter uses to smooth over the edges and the inside of a macroblock; Level is used by both filters, the other two by the normal filter only.
        internal int Level;
        internal int ILevel;
        internal int HLevel;

        // Whether the filter of the inside of the macroblock cannot be left out.
        internal bool Inner;
    }
}
