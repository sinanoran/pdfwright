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
// Predictor modes (RFC 6386, chapters 11 and 12), residual coefficients (chapter 13), the inverse transforms (chapter 14) and macroblock reconstruction, after golang.org/x/image/vp8 (pred.go, predfunc.go, token.go, idct.go, reconstruct.go).
//
// There are 1*16*16 + 2*8*8 + 1*4*4 coefficients for each macroblock: 256 luma DCT coefficients, 2*64 chroma DCT coefficients and 16 luma WHT coefficients, in lots of 16.
// The pixels of a macroblock are rebuilt in a workspace of 26 rows of 32 bytes (Ybr below) that also holds the row above and the column left of each of the three planes:
//
//   row 0           : a b b b b b b b b b b b b b b b b c c c c   (the row above the luma block, its top right to the right, copied to rows 4, 8 and 12)
//   rows 1 to 16    : d Y Y Y Y Y Y Y Y Y Y Y Y Y Y Y Y           (the column left, then the 16 x 16 luma block, from column 8)
//   row 17          : e f f f f f f f f   g h h h h h h h h       (the row above the two chroma blocks)
//   rows 18 to 25   : i B B B B B B B B   j R R R R R R R R       (the column left, then the blue block at column 8 and the red block at column 24)
//
// For a macroblock in the top row, the row above is 0x7f (0x81 for the corner); for one in the leftmost column, the column left is 0x81.

namespace Pdfwright.Images;

internal sealed partial class Vp8Decoder
{
    private const int PredDC = 0;
    private const int PredTM = 1;
    private const int PredVE = 2;
    private const int PredHE = 3;
    private const int PredRD = 4;
    private const int PredVR = 5;
    private const int PredLD = 6;
    private const int PredVL = 7;
    private const int PredHD = 8;
    private const int PredHU = 9;
    private const int PredDCTop = 10;
    private const int PredDCLeft = 11;
    private const int PredDCTopLeft = 12;

    private const int BCoeffBase = 1 * 16 * 16;
    private const int RCoeffBase = (1 * 16 * 16) + (1 * 8 * 8);
    private const int WhtCoeffBase = (1 * 16 * 16) + (2 * 8 * 8);

    private const int YbrStride = 32;
    private const int YbrYX = 8;
    private const int YbrYY = 1;
    private const int YbrBX = 8;
    private const int YbrBY = 18;
    private const int YbrRX = 24;
    private const int YbrRY = 18;

    // The mapping from the position of a 4x4 region to a band is in section 13.3; the categories' probabilities are in section 13.2 (categories 1 and 2 are read inline).
    private static readonly byte[] Bands = [0, 1, 2, 3, 6, 4, 5, 6, 6, 6, 6, 6, 6, 6, 6, 7, 0];

    private static readonly byte[][] Cat3456 =
    [
        [173, 148, 140, 0, 0, 0, 0, 0, 0, 0, 0, 0],
        [176, 155, 140, 135, 0, 0, 0, 0, 0, 0, 0, 0],
        [180, 157, 141, 134, 130, 0, 0, 0, 0, 0, 0, 0],
        [254, 254, 243, 230, 196, 177, 153, 140, 133, 130, 129, 0],
    ];

    private static readonly byte[] Zigzag = [0, 1, 4, 8, 5, 2, 3, 6, 9, 12, 13, 10, 7, 11, 14, 15];

    // The per-macroblock state: the macroblock to the left, and (for each macroblock of the row above) the one above.
    private byte[] _upPred = [];
    private byte[] _upNzMask = [];
    private byte[] _upNzY16 = [];
    private readonly byte[] _leftPred = new byte[4];
    private int _leftNzMask;
    private int _leftNzY16;

    private int _segment;
    private uint _nzDCMask;
    private uint _nzACMask;
    private bool _usePredY16;
    private int _predY16;
    private int _predC8;
    private readonly byte[] _predY4 = new byte[16];

    private readonly short[] _coeff = new short[(1 * 16 * 16) + (2 * 8 * 8) + (1 * 4 * 4)];
    private readonly byte[] _ybr = new byte[(1 + 16 + 1 + 8) * YbrStride];

    private readonly int[] _lnz = new int[4];
    private readonly int[] _unz = new int[4];
    private readonly int[] _nzDC = new int[4];
    private readonly int[] _nzAC = new int[4];

    // ---- Predictor modes (chapter 11)

    private void ParsePredModeY16(int mbx)
    {
        int p;
        if (!_fp.ReadBit(156))
        {
            p = !_fp.ReadBit(163) ? PredDC : PredVE;
        }
        else
        {
            p = !_fp.ReadBit(128) ? PredHE : PredTM;
        }

        for (int i = 0; i < 4; i++)
        {
            _upPred[(mbx * 4) + i] = (byte)p;
            _leftPred[i] = (byte)p;
        }

        _predY16 = p;
    }

    private void ParsePredModeC8()
    {
        if (!_fp.ReadBit(142))
        {
            _predC8 = PredDC;
        }
        else if (!_fp.ReadBit(114))
        {
            _predC8 = PredVE;
        }
        else if (!_fp.ReadBit(183))
        {
            _predC8 = PredHE;
        }
        else
        {
            _predC8 = PredTM;
        }
    }

    private void ParsePredModeY4(int mbx)
    {
        ReadOnlySpan<byte> predProb = Vp8Tables.PredProb;
        for (int j = 0; j < 4; j++)
        {
            int p = _leftPred[j];
            for (int i = 0; i < 4; i++)
            {
                ReadOnlySpan<byte> prob = predProb.Slice(((_upPred[(mbx * 4) + i] * 10) + p) * 9, 9);
                if (!_fp.ReadBit(prob[0]))
                {
                    p = PredDC;
                }
                else if (!_fp.ReadBit(prob[1]))
                {
                    p = PredTM;
                }
                else if (!_fp.ReadBit(prob[2]))
                {
                    p = PredVE;
                }
                else if (!_fp.ReadBit(prob[3]))
                {
                    if (!_fp.ReadBit(prob[4]))
                    {
                        p = PredHE;
                    }
                    else
                    {
                        p = !_fp.ReadBit(prob[5]) ? PredRD : PredVR;
                    }
                }
                else if (!_fp.ReadBit(prob[6]))
                {
                    p = PredLD;
                }
                else if (!_fp.ReadBit(prob[7]))
                {
                    p = PredVL;
                }
                else
                {
                    p = !_fp.ReadBit(prob[8]) ? PredHD : PredHU;
                }

                _predY4[(j * 4) + i] = (byte)p;
                _upPred[(mbx * 4) + i] = (byte)p;
            }

            _leftPred[j] = (byte)p;
        }
    }

    private static int CheckTopLeftPred(int mbx, int mby, int p)
    {
        if (p != PredDC)
        {
            return p;
        }

        if (mbx == 0)
        {
            return mby == 0 ? PredDCTopLeft : PredDCLeft;
        }

        return mby == 0 ? PredDCTop : PredDC;
    }

    // ---- Residuals (chapter 13)

    /// <summary>
    /// Parses a 4x4 region of residual coefficients (section 13.3) and returns 1 when it had at least one non-zero coefficient. <paramref name="context"/> is 0, 1 or 2: how many of the regions above and left had non-zero coefficients.
    /// </summary>
    private int ParseResiduals4(Vp8Partition r, int plane, int context, int dcQuant, int acQuant, bool skipFirstCoeff, int coeffBase)
    {
        byte[] prob = _tokenProb;
        int planeBase = plane * ProbsPerPlane;
        int n = skipFirstCoeff ? 1 : 0;
        int p = planeBase + (((Bands[n] * 3) + context) * 11);
        if (!r.ReadBit(prob[p]))
        {
            return 0;
        }

        while (n != 16)
        {
            n++;
            if (!r.ReadBit(prob[p + 1]))
            {
                p = planeBase + (Bands[n] * 3 * 11);
                continue;
            }

            uint v;
            if (!r.ReadBit(prob[p + 2]))
            {
                v = 1;
                p = planeBase + (((Bands[n] * 3) + 1) * 11);
            }
            else
            {
                if (!r.ReadBit(prob[p + 3]))
                {
                    if (!r.ReadBit(prob[p + 4]))
                    {
                        v = 2;
                    }
                    else
                    {
                        v = 3 + r.ReadUint(prob[p + 5], 1);
                    }
                }
                else if (!r.ReadBit(prob[p + 6]))
                {
                    if (!r.ReadBit(prob[p + 7]))
                    {
                        // Category 1.
                        v = 5 + r.ReadUint(159, 1);
                    }
                    else
                    {
                        // Category 2.
                        v = 7 + (2 * r.ReadUint(165, 1)) + r.ReadUint(145, 1);
                    }
                }
                else
                {
                    // Categories 3, 4, 5 and 6.
                    uint b1 = r.ReadUint(prob[p + 8], 1);
                    uint b0 = r.ReadUint(prob[p + 9 + (int)b1], 1);
                    int cat = (int)((2 * b1) + b0);
                    byte[] tab = Cat3456[cat];
                    v = 0;
                    for (int i = 0; tab[i] != 0; i++)
                    {
                        v *= 2;
                        v += r.ReadUint(tab[i], 1);
                    }

                    v += (uint)(3 + (8 << cat));
                }

                p = planeBase + (((Bands[n] * 3) + 2) * 11);
            }

            int z = Zigzag[n - 1];
            int c = (int)v * (z > 0 ? acQuant : dcQuant);
            if (r.ReadBit(Vp8Partition.UniformProb))
            {
                c = -c;
            }

            _coeff[coeffBase + z] = (short)c;
            if (n == 16 || !r.ReadBit(prob[p]))
            {
                return 1;
            }
        }

        return 1;
    }

    private static void Unpack(int mask, int[] into)
    {
        into[0] = mask & 1;
        into[1] = (mask >> 1) & 1;
        into[2] = (mask >> 2) & 1;
        into[3] = (mask >> 3) & 1;
    }

    private static uint Pack(int[] x, int shift) => (uint)(x[0] | (x[1] << 1) | (x[2] << 2) | (x[3] << 3)) << shift;

    /// <summary>Parses the residuals of a macroblock and returns whether the filter of the inside of it can be skipped.</summary>
    private bool ParseResiduals(int mbx, int mby)
    {
        Vp8Partition partition = _op[mby & (_nOP - 1)];
        int plane = PlaneY1SansY2;
        Quant quant = _quant[_segment];

        // The DC coefficient of each 4x4 luma region.
        if (_usePredY16)
        {
            int nz = ParseResiduals4(partition, PlaneY2, _leftNzY16 + _upNzY16[mbx], quant.Y2Dc, quant.Y2Ac, false, WhtCoeffBase);
            _leftNzY16 = nz;
            _upNzY16[mbx] = (byte)nz;
            InverseWht16();
            plane = PlaneY1WithY2;
        }

        uint nzDCMask = 0;
        uint nzACMask = 0;
        int coeffBase = 0;

        // The luma coefficients.
        int[] lnz = _lnz;
        int[] unz = _unz;
        int[] nzDC = _nzDC;
        int[] nzAC = _nzAC;
        Unpack(_leftNzMask & 0x0f, lnz);
        Unpack(_upNzMask[mbx] & 0x0f, unz);
        for (int y = 0; y < 4; y++)
        {
            int nz = lnz[y];
            for (int x = 0; x < 4; x++)
            {
                nz = ParseResiduals4(partition, plane, nz + unz[x], quant.Y1Dc, quant.Y1Ac, _usePredY16, coeffBase);
                unz[x] = nz;
                nzAC[x] = nz;
                nzDC[x] = _coeff[coeffBase] != 0 ? 1 : 0;
                coeffBase += 16;
            }

            lnz[y] = nz;
            nzDCMask |= Pack(nzDC, y * 4);
            nzACMask |= Pack(nzAC, y * 4);
        }

        uint lnzMask = Pack(lnz, 0);
        uint unzMask = Pack(unz, 0);

        // The chroma coefficients.
        Unpack(_leftNzMask >> 4, lnz);
        Unpack(_upNzMask[mbx] >> 4, unz);
        for (int c = 0; c < 4; c += 2)
        {
            for (int y = 0; y < 2; y++)
            {
                int nz = lnz[y + c];
                for (int x = 0; x < 2; x++)
                {
                    nz = ParseResiduals4(partition, PlaneUV, nz + unz[x + c], quant.UvDc, quant.UvAc, false, coeffBase);
                    unz[x + c] = nz;
                    nzAC[(y * 2) + x] = nz;
                    nzDC[(y * 2) + x] = _coeff[coeffBase] != 0 ? 1 : 0;
                    coeffBase += 16;
                }

                lnz[y + c] = nz;
            }

            nzDCMask |= Pack(nzDC, 16 + (c * 2));
            nzACMask |= Pack(nzAC, 16 + (c * 2));
        }

        lnzMask |= Pack(lnz, 4);
        unzMask |= Pack(unz, 4);

        // Save the state.
        _leftNzMask = (int)(lnzMask & 0xff);
        _upNzMask[mbx] = (byte)unzMask;
        _nzDCMask = nzDCMask;
        _nzACMask = nzACMask;

        // Section 15.1 says that steps 2 and 4 of the loop filter are skipped if there is no DCT coefficient coded for the whole macroblock.
        return nzDCMask == 0 && nzACMask == 0;
    }

    // ---- The workspace

    /// <summary>Prepares the row above and the column left of the three blocks (the cells a to j of the drawing at the top of this file).</summary>
    private void PrepareYbr(int mbx, int mby)
    {
        byte[] ybr = _ybr;
        if (mbx == 0)
        {
            for (int y = 0; y < 17; y++)
            {
                ybr[(y * YbrStride) + 7] = 0x81;
            }

            for (int y = 17; y < 26; y++)
            {
                ybr[(y * YbrStride) + 7] = 0x81;
                ybr[(y * YbrStride) + 23] = 0x81;
            }
        }
        else
        {
            for (int y = 0; y < 17; y++)
            {
                ybr[(y * YbrStride) + 7] = ybr[(y * YbrStride) + 7 + 16];
            }

            for (int y = 17; y < 26; y++)
            {
                ybr[(y * YbrStride) + 7] = ybr[(y * YbrStride) + 15];
                ybr[(y * YbrStride) + 23] = ybr[(y * YbrStride) + 31];
            }
        }

        if (mby == 0)
        {
            for (int x = 7; x < 28; x++)
            {
                ybr[x] = 0x7f;
            }

            for (int x = 7; x < 16; x++)
            {
                ybr[(17 * YbrStride) + x] = 0x7f;
            }

            for (int x = 23; x < 32; x++)
            {
                ybr[(17 * YbrStride) + x] = 0x7f;
            }
        }
        else
        {
            int yRow = ((16 * mby) - 1) * YStride;
            int cRow = ((8 * mby) - 1) * CStride;
            for (int i = 0; i < 16; i++)
            {
                ybr[8 + i] = Y[yRow + (16 * mbx) + i];
            }

            for (int i = 0; i < 8; i++)
            {
                ybr[(17 * YbrStride) + 8 + i] = Cb[cRow + (8 * mbx) + i];
            }

            for (int i = 0; i < 8; i++)
            {
                ybr[(17 * YbrStride) + 24 + i] = Cr[cRow + (8 * mbx) + i];
            }

            // The top right of the last macroblock of a row is the last sample above it, repeated.
            int last = mbx == _mbw - 1 ? 15 : -1;
            for (int i = 16; i < 20; i++)
            {
                ybr[8 + i] = Y[yRow + (16 * mbx) + (last >= 0 ? last : i)];
            }
        }

        for (int y = 4; y < 16; y += 4)
        {
            ybr[(y * YbrStride) + 24] = ybr[24];
            ybr[(y * YbrStride) + 25] = ybr[25];
            ybr[(y * YbrStride) + 26] = ybr[26];
            ybr[(y * YbrStride) + 27] = ybr[27];
        }
    }

    /// <summary>Reconstructs one macroblock into the planes and returns whether the filter of the inside of it can be skipped.</summary>
    private bool Reconstruct(int mbx, int mby)
    {
        if (_updateMap)
        {
            if (!_fp.ReadBit(_segProb[0]))
            {
                _segment = (int)_fp.ReadUint(_segProb[1], 1);
            }
            else
            {
                _segment = (int)_fp.ReadUint(_segProb[2], 1) + 2;
            }
        }

        bool skip = false;
        if (_useSkipProb)
        {
            skip = _fp.ReadBit(_skipProb);
        }

        // Prepare the workspace.
        Array.Clear(_coeff);
        PrepareYbr(mbx, mby);

        // Parse the predictor modes.
        _usePredY16 = _fp.ReadBit(145);
        if (_usePredY16)
        {
            ParsePredModeY16(mbx);
        }
        else
        {
            ParsePredModeY4(mbx);
        }

        ParsePredModeC8();

        // Parse the residuals.
        if (!skip)
        {
            skip = ParseResiduals(mbx, mby);
        }
        else
        {
            if (_usePredY16)
            {
                _leftNzY16 = 0;
                _upNzY16[mbx] = 0;
            }

            _leftNzMask = 0;
            _upNzMask[mbx] = 0;
            _nzDCMask = 0;
            _nzACMask = 0;
        }

        // Rebuild the pixels and copy them to the planes.
        ReconstructMacroblock(mbx, mby);
        for (int y = 0, at = ((mby * YStride) + mbx) * 16; y < 16; y++, at += YStride)
        {
            Buffer.BlockCopy(_ybr, ((YbrYY + y) * YbrStride) + YbrYX, Y, at, 16);
        }

        for (int y = 0, at = ((mby * CStride) + mbx) * 8; y < 8; y++, at += CStride)
        {
            Buffer.BlockCopy(_ybr, ((YbrBY + y) * YbrStride) + YbrBX, Cb, at, 8);
            Buffer.BlockCopy(_ybr, ((YbrRY + y) * YbrStride) + YbrRX, Cr, at, 8);
        }

        return skip;
    }

    /// <summary>Applies the predictor functions and adds the inverse-transformed residuals.</summary>
    private void ReconstructMacroblock(int mbx, int mby)
    {
        if (_usePredY16)
        {
            Predict(16, CheckTopLeftPred(mbx, mby, _predY16), 1, 8);
            for (int j = 0; j < 4; j++)
            {
                for (int i = 0; i < 4; i++)
                {
                    int n = (4 * j) + i;
                    uint mask = 1u << n;
                    if ((_nzACMask & mask) != 0)
                    {
                        InverseDct4((4 * j) + 1, (4 * i) + 8, 16 * n);
                    }
                    else if ((_nzDCMask & mask) != 0)
                    {
                        InverseDct4DcOnly((4 * j) + 1, (4 * i) + 8, 16 * n);
                    }
                }
            }
        }
        else
        {
            for (int j = 0; j < 4; j++)
            {
                for (int i = 0; i < 4; i++)
                {
                    int n = (4 * j) + i;
                    int y = (4 * j) + 1;
                    int x = (4 * i) + 8;
                    Predict4(_predY4[n], y, x);
                    uint mask = 1u << n;
                    if ((_nzACMask & mask) != 0)
                    {
                        InverseDct4(y, x, 16 * n);
                    }
                    else if ((_nzDCMask & mask) != 0)
                    {
                        InverseDct4DcOnly(y, x, 16 * n);
                    }
                }
            }
        }

        int p = CheckTopLeftPred(mbx, mby, _predC8);
        Predict(8, p, YbrBY, YbrBX);
        if ((_nzACMask & 0x0f0000) != 0)
        {
            InverseDct8(YbrBY, YbrBX, BCoeffBase);
        }
        else if ((_nzDCMask & 0x0f0000) != 0)
        {
            InverseDct8DcOnly(YbrBY, YbrBX, BCoeffBase);
        }

        Predict(8, p, YbrRY, YbrRX);
        if ((_nzACMask & 0xf00000) != 0)
        {
            InverseDct8(YbrRY, YbrRX, RCoeffBase);
        }
        else if ((_nzDCMask & 0xf00000) != 0)
        {
            InverseDct8DcOnly(YbrRY, YbrRX, RCoeffBase);
        }
    }

    // ---- Prediction (chapter 12)

    private static byte Avg2(int a, int b) => (byte)((a + b + 1) >> 1);

    private static byte Avg3(int a, int b, int c) => (byte)((a + (2 * b) + c + 2) >> 2);

    private void Put4(int y, int x, byte a, byte b, byte c, byte d)
    {
        int at = (y * YbrStride) + x;
        _ybr[at] = a;
        _ybr[at + 1] = b;
        _ybr[at + 2] = c;
        _ybr[at + 3] = d;
    }

    /// <summary>Predicts a 4x4 luma region from the row above (with its top right) and the column left.</summary>
    private void Predict4(int mode, int y, int x)
    {
        byte[] b = _ybr;
        int top = ((y - 1) * YbrStride) + x;
        int c = b[top - 1];
        int t0 = b[top];
        int t1 = b[top + 1];
        int t2 = b[top + 2];
        int t3 = b[top + 3];
        int t4 = b[top + 4];
        int t5 = b[top + 5];
        int t6 = b[top + 6];
        int t7 = b[top + 7];
        int l0 = b[(y * YbrStride) + x - 1];
        int l1 = b[((y + 1) * YbrStride) + x - 1];
        int l2 = b[((y + 2) * YbrStride) + x - 1];
        int l3 = b[((y + 3) * YbrStride) + x - 1];
        switch (mode)
        {
            case PredDC:
            {
                byte avg = (byte)((4 + t0 + t1 + t2 + t3 + l0 + l1 + l2 + l3) >> 3);
                for (int j = 0; j < 4; j++)
                {
                    Put4(y + j, x, avg, avg, avg, avg);
                }

                break;
            }

            case PredTM:
            {
                for (int j = 0; j < 4; j++)
                {
                    int l = b[((y + j) * YbrStride) + x - 1];
                    for (int i = 0; i < 4; i++)
                    {
                        b[((y + j) * YbrStride) + x + i] = (byte)Clip(l + b[top + i] - c, 0, 255);
                    }
                }

                break;
            }

            case PredVE:
            {
                byte abc = Avg3(c, t0, t1);
                byte bcd = Avg3(t0, t1, t2);
                byte cde = Avg3(t1, t2, t3);
                byte def = Avg3(t2, t3, t4);
                for (int j = 0; j < 4; j++)
                {
                    Put4(y + j, x, abc, bcd, cde, def);
                }

                break;
            }

            case PredHE:
            {
                byte apq = Avg3(c, l0, l1);
                byte rqp = Avg3(l2, l1, l0);
                byte srq = Avg3(l3, l2, l1);
                byte ssr = Avg3(l3, l3, l2);
                Put4(y, x, apq, apq, apq, apq);
                Put4(y + 1, x, rqp, rqp, rqp, rqp);
                Put4(y + 2, x, srq, srq, srq, srq);
                Put4(y + 3, x, ssr, ssr, ssr, ssr);
                break;
            }

            case PredRD:
            {
                byte srq = Avg3(l3, l2, l1);
                byte rqp = Avg3(l2, l1, l0);
                byte qpa = Avg3(l1, l0, c);
                byte pab = Avg3(l0, c, t0);
                byte abc = Avg3(c, t0, t1);
                byte bcd = Avg3(t0, t1, t2);
                byte cde = Avg3(t1, t2, t3);
                Put4(y, x, pab, abc, bcd, cde);
                Put4(y + 1, x, qpa, pab, abc, bcd);
                Put4(y + 2, x, rqp, qpa, pab, abc);
                Put4(y + 3, x, srq, rqp, qpa, pab);
                break;
            }

            case PredVR:
            {
                byte ab = Avg2(c, t0);
                byte bc = Avg2(t0, t1);
                byte cd = Avg2(t1, t2);
                byte de = Avg2(t2, t3);
                byte rqp = Avg3(l2, l1, l0);
                byte qpa = Avg3(l1, l0, c);
                byte pab = Avg3(l0, c, t0);
                byte abc = Avg3(c, t0, t1);
                byte bcd = Avg3(t0, t1, t2);
                byte cde = Avg3(t1, t2, t3);
                Put4(y, x, ab, bc, cd, de);
                Put4(y + 1, x, pab, abc, bcd, cde);
                Put4(y + 2, x, qpa, ab, bc, cd);
                Put4(y + 3, x, rqp, pab, abc, bcd);
                break;
            }

            case PredLD:
            {
                byte abc = Avg3(t0, t1, t2);
                byte bcd = Avg3(t1, t2, t3);
                byte cde = Avg3(t2, t3, t4);
                byte def = Avg3(t3, t4, t5);
                byte efg = Avg3(t4, t5, t6);
                byte fgh = Avg3(t5, t6, t7);
                byte ghh = Avg3(t6, t7, t7);
                Put4(y, x, abc, bcd, cde, def);
                Put4(y + 1, x, bcd, cde, def, efg);
                Put4(y + 2, x, cde, def, efg, fgh);
                Put4(y + 3, x, def, efg, fgh, ghh);
                break;
            }

            case PredVL:
            {
                byte ab = Avg2(t0, t1);
                byte bc = Avg2(t1, t2);
                byte cd = Avg2(t2, t3);
                byte de = Avg2(t3, t4);
                byte abc = Avg3(t0, t1, t2);
                byte bcd = Avg3(t1, t2, t3);
                byte cde = Avg3(t2, t3, t4);
                byte def = Avg3(t3, t4, t5);
                byte efg = Avg3(t4, t5, t6);
                byte fgh = Avg3(t5, t6, t7);
                Put4(y, x, ab, bc, cd, de);
                Put4(y + 1, x, abc, bcd, cde, def);
                Put4(y + 2, x, bc, cd, de, efg);
                Put4(y + 3, x, bcd, cde, def, fgh);
                break;
            }

            case PredHD:
            {
                byte sr = Avg2(l3, l2);
                byte rq = Avg2(l2, l1);
                byte qp = Avg2(l1, l0);
                byte pa = Avg2(l0, c);
                byte srq = Avg3(l3, l2, l1);
                byte rqp = Avg3(l2, l1, l0);
                byte qpa = Avg3(l1, l0, c);
                byte pab = Avg3(l0, c, t0);
                byte abc = Avg3(c, t0, t1);
                byte bcd = Avg3(t0, t1, t2);
                Put4(y, x, pa, pab, abc, bcd);
                Put4(y + 1, x, qp, qpa, pa, pab);
                Put4(y + 2, x, rq, rqp, qp, qpa);
                Put4(y + 3, x, sr, srq, rq, rqp);
                break;
            }

            case PredHU:
            {
                byte pq = Avg2(l0, l1);
                byte qr = Avg2(l1, l2);
                byte rs = Avg2(l2, l3);
                byte pqr = Avg3(l0, l1, l2);
                byte qrs = Avg3(l1, l2, l3);
                byte rss = Avg3(l2, l3, l3);
                byte sss = (byte)l3;
                Put4(y, x, pq, pqr, qr, qrs);
                Put4(y + 1, x, qr, qrs, rs, rss);
                Put4(y + 2, x, rs, rss, sss, sss);
                Put4(y + 3, x, sss, sss, sss, sss);
                break;
            }
        }
    }

    /// <summary>Predicts a block of <paramref name="size"/> (8 or 16) samples square: the whole 16x16 luma block, or one 8x8 chroma block.</summary>
    private void Predict(int size, int mode, int y, int x)
    {
        byte[] b = _ybr;
        int top = ((y - 1) * YbrStride) + x;
        switch (mode)
        {
            case PredDC:
            case PredDCTop:
            case PredDCLeft:
            {
                // PredDCTop is for a macroblock in the top row, which has no row above: the average of the column left alone. PredDCLeft is the reverse.
                int sum = mode == PredDC ? size : size / 2;
                if (mode != PredDCLeft)
                {
                    for (int j = 0; j < size; j++)
                    {
                        sum += b[((y + j) * YbrStride) + x - 1];
                    }
                }

                if (mode != PredDCTop)
                {
                    for (int i = 0; i < size; i++)
                    {
                        sum += b[top + i];
                    }
                }

                byte avg = (byte)(mode == PredDC ? sum / (2 * size) : sum / size);
                Fill(size, y, x, avg);
                break;
            }

            case PredDCTopLeft:
                Fill(size, y, x, 0x80);
                break;

            case PredTM:
            {
                int corner = b[top - 1];
                for (int j = 0; j < size; j++)
                {
                    int delta1 = b[((y + j) * YbrStride) + x - 1] - corner;
                    for (int i = 0; i < size; i++)
                    {
                        b[((y + j) * YbrStride) + x + i] = (byte)Clip(delta1 + b[top + i], 0, 255);
                    }
                }

                break;
            }

            case PredVE:
                for (int j = 0; j < size; j++)
                {
                    Buffer.BlockCopy(b, top, b, ((y + j) * YbrStride) + x, size);
                }

                break;

            case PredHE:
                for (int j = 0; j < size; j++)
                {
                    byte left = b[((y + j) * YbrStride) + x - 1];
                    int at = ((y + j) * YbrStride) + x;
                    for (int i = 0; i < size; i++)
                    {
                        b[at + i] = left;
                    }
                }

                break;
        }
    }

    private void Fill(int size, int y, int x, byte value)
    {
        for (int j = 0; j < size; j++)
        {
            int at = ((y + j) * YbrStride) + x;
            for (int i = 0; i < size; i++)
            {
                _ybr[at + i] = value;
            }
        }
    }

    // ---- The inverse transforms (chapter 14)

    private static byte Clip8(int i) => i < 0 ? (byte)0 : i > 255 ? (byte)255 : (byte)i;

    private void InverseDct4(int y, int x, int coeffBase)
    {
        const int c1 = 85627; // 65536 * cos(pi/8) * sqrt(2).
        const int c2 = 35468; // 65536 * sin(pi/8) * sqrt(2).
        Span<int> m = stackalloc int[16];
        short[] coeff = _coeff;
        for (int i = 0; i < 4; i++)
        {
            int a = coeff[coeffBase] + coeff[coeffBase + 8];
            int b = coeff[coeffBase] - coeff[coeffBase + 8];
            int c = ((coeff[coeffBase + 4] * c2) >> 16) - ((coeff[coeffBase + 12] * c1) >> 16);
            int d = ((coeff[coeffBase + 4] * c1) >> 16) + ((coeff[coeffBase + 12] * c2) >> 16);
            m[(i * 4) + 0] = a + d;
            m[(i * 4) + 1] = b + c;
            m[(i * 4) + 2] = b - c;
            m[(i * 4) + 3] = a - d;
            coeffBase++;
        }

        for (int j = 0; j < 4; j++)
        {
            int dc = m[j] + 4;
            int a = dc + m[8 + j];
            int b = dc - m[8 + j];
            int c = ((m[4 + j] * c2) >> 16) - ((m[12 + j] * c1) >> 16);
            int d = ((m[4 + j] * c1) >> 16) + ((m[12 + j] * c2) >> 16);
            int at = ((y + j) * YbrStride) + x;
            _ybr[at] = Clip8(_ybr[at] + ((a + d) >> 3));
            _ybr[at + 1] = Clip8(_ybr[at + 1] + ((b + c) >> 3));
            _ybr[at + 2] = Clip8(_ybr[at + 2] + ((b - c) >> 3));
            _ybr[at + 3] = Clip8(_ybr[at + 3] + ((a - d) >> 3));
        }
    }

    private void InverseDct4DcOnly(int y, int x, int coeffBase)
    {
        int dc = (_coeff[coeffBase] + 4) >> 3;
        for (int j = 0; j < 4; j++)
        {
            int at = ((y + j) * YbrStride) + x;
            for (int i = 0; i < 4; i++)
            {
                _ybr[at + i] = Clip8(_ybr[at + i] + dc);
            }
        }
    }

    private void InverseDct8(int y, int x, int coeffBase)
    {
        InverseDct4(y, x, coeffBase);
        InverseDct4(y, x + 4, coeffBase + 16);
        InverseDct4(y + 4, x, coeffBase + 32);
        InverseDct4(y + 4, x + 4, coeffBase + 48);
    }

    private void InverseDct8DcOnly(int y, int x, int coeffBase)
    {
        InverseDct4DcOnly(y, x, coeffBase);
        InverseDct4DcOnly(y, x + 4, coeffBase + 16);
        InverseDct4DcOnly(y + 4, x, coeffBase + 32);
        InverseDct4DcOnly(y + 4, x + 4, coeffBase + 48);
    }

    private void InverseWht16()
    {
        Span<int> m = stackalloc int[16];
        short[] coeff = _coeff;
        for (int i = 0; i < 4; i++)
        {
            int a0 = coeff[384 + 0 + i] + coeff[384 + 12 + i];
            int a1 = coeff[384 + 4 + i] + coeff[384 + 8 + i];
            int a2 = coeff[384 + 4 + i] - coeff[384 + 8 + i];
            int a3 = coeff[384 + 0 + i] - coeff[384 + 12 + i];
            m[0 + i] = a0 + a1;
            m[8 + i] = a0 - a1;
            m[4 + i] = a3 + a2;
            m[12 + i] = a3 - a2;
        }

        int output = 0;
        for (int i = 0; i < 4; i++)
        {
            int dc = m[0 + (i * 4)] + 3;
            int a0 = dc + m[3 + (i * 4)];
            int a1 = m[1 + (i * 4)] + m[2 + (i * 4)];
            int a2 = m[1 + (i * 4)] - m[2 + (i * 4)];
            int a3 = dc - m[3 + (i * 4)];
            coeff[output + 0] = (short)((a0 + a1) >> 3);
            coeff[output + 16] = (short)((a3 + a2) >> 3);
            coeff[output + 32] = (short)((a0 - a1) >> 3);
            coeff[output + 48] = (short)((a3 - a2) >> 3);
            output += 64;
        }
    }
}
