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
// The loop filter of VP8 (RFC 6386, chapter 15), after golang.org/x/image/vp8 (filter.go).

namespace Pdfwright.Images;

internal sealed partial class Vp8Decoder
{
    private static int Clamp15(int x) => x < -16 ? -16 : x > 15 ? 15 : x;

    private static int Clamp127(int x) => x < -128 ? -128 : x > 127 ? 127 : x;

    private static byte Clamp255(int x) => x < 0 ? (byte)0 : x > 255 ? (byte)255 : (byte)x;

    /// <summary>Modifies a band of 2 pixels along an edge: 16 positions starting at <paramref name="index"/>, <paramref name="iStep"/> apart along the edge, <paramref name="jStep"/> apart across it.</summary>
    private static void Filter2(byte[] pix, int level, int index, int iStep, int jStep)
    {
        for (int n = 16; n > 0; n--, index += iStep)
        {
            int p1 = pix[index - (2 * jStep)];
            int p0 = pix[index - (1 * jStep)];
            int q0 = pix[index];
            int q1 = pix[index + jStep];
            if ((Math.Abs(p0 - q0) << 1) + (Math.Abs(p1 - q1) >> 1) > level)
            {
                continue;
            }

            int a = (3 * (q0 - p0)) + Clamp127(p1 - q1);
            int a1 = Clamp15((a + 4) >> 3);
            int a2 = Clamp15((a + 3) >> 3);
            pix[index - jStep] = Clamp255(p0 + a2);
            pix[index] = Clamp255(q0 - a1);
        }
    }

    /// <summary>Modifies a band of 2, 4 or 6 pixels along an edge.</summary>
    private static void Filter246(byte[] pix, int n, int level, int ilevel, int hlevel, int index, int iStep, int jStep, bool fourNotSix)
    {
        for (; n > 0; n--, index += iStep)
        {
            int p3 = pix[index - (4 * jStep)];
            int p2 = pix[index - (3 * jStep)];
            int p1 = pix[index - (2 * jStep)];
            int p0 = pix[index - (1 * jStep)];
            int q0 = pix[index];
            int q1 = pix[index + jStep];
            int q2 = pix[index + (2 * jStep)];
            int q3 = pix[index + (3 * jStep)];
            if ((Math.Abs(p0 - q0) << 1) + (Math.Abs(p1 - q1) >> 1) > level)
            {
                continue;
            }

            if (Math.Abs(p3 - p2) > ilevel ||
                Math.Abs(p2 - p1) > ilevel ||
                Math.Abs(p1 - p0) > ilevel ||
                Math.Abs(q1 - q0) > ilevel ||
                Math.Abs(q2 - q1) > ilevel ||
                Math.Abs(q3 - q2) > ilevel)
            {
                continue;
            }

            if (Math.Abs(p1 - p0) > hlevel || Math.Abs(q1 - q0) > hlevel)
            {
                // Filter 2 pixels.
                int a = (3 * (q0 - p0)) + Clamp127(p1 - q1);
                int a1 = Clamp15((a + 4) >> 3);
                int a2 = Clamp15((a + 3) >> 3);
                pix[index - jStep] = Clamp255(p0 + a2);
                pix[index] = Clamp255(q0 - a1);
            }
            else if (fourNotSix)
            {
                // Filter 4 pixels.
                int a = 3 * (q0 - p0);
                int a1 = Clamp15((a + 4) >> 3);
                int a2 = Clamp15((a + 3) >> 3);
                int a3 = (a1 + 1) >> 1;
                pix[index - (2 * jStep)] = Clamp255(p1 + a3);
                pix[index - jStep] = Clamp255(p0 + a2);
                pix[index] = Clamp255(q0 - a1);
                pix[index + jStep] = Clamp255(q1 - a3);
            }
            else
            {
                // Filter 6 pixels.
                int a = Clamp127((3 * (q0 - p0)) + Clamp127(p1 - q1));
                int a1 = ((27 * a) + 63) >> 7;
                int a2 = ((18 * a) + 63) >> 7;
                int a3 = ((9 * a) + 63) >> 7;
                pix[index - (3 * jStep)] = Clamp255(p2 + a3);
                pix[index - (2 * jStep)] = Clamp255(p1 + a2);
                pix[index - jStep] = Clamp255(p0 + a1);
                pix[index] = Clamp255(q0 - a1);
                pix[index + jStep] = Clamp255(q1 - a2);
                pix[index + (2 * jStep)] = Clamp255(q2 - a3);
            }
        }
    }

    // The simple filter (section 15.2).
    private void SimpleFilter()
    {
        for (int mby = 0; mby < _mbh; mby++)
        {
            for (int mbx = 0; mbx < _mbw; mbx++)
            {
                FilterParam f = _perMbFilterParams[(_mbw * mby) + mbx];
                if (f.Level == 0)
                {
                    continue;
                }

                int l = f.Level;
                int yIndex = ((mby * YStride) + mbx) * 16;
                if (mbx > 0)
                {
                    Filter2(Y, l + 4, yIndex, YStride, 1);
                }

                if (f.Inner)
                {
                    Filter2(Y, l, yIndex + 0x4, YStride, 1);
                    Filter2(Y, l, yIndex + 0x8, YStride, 1);
                    Filter2(Y, l, yIndex + 0xc, YStride, 1);
                }

                if (mby > 0)
                {
                    Filter2(Y, l + 4, yIndex, 1, YStride);
                }

                if (f.Inner)
                {
                    Filter2(Y, l, yIndex + (YStride * 0x4), 1, YStride);
                    Filter2(Y, l, yIndex + (YStride * 0x8), 1, YStride);
                    Filter2(Y, l, yIndex + (YStride * 0xc), 1, YStride);
                }
            }
        }
    }

    // The normal filter (section 15.3).
    private void NormalFilter()
    {
        for (int mby = 0; mby < _mbh; mby++)
        {
            for (int mbx = 0; mbx < _mbw; mbx++)
            {
                FilterParam f = _perMbFilterParams[(_mbw * mby) + mbx];
                if (f.Level == 0)
                {
                    continue;
                }

                int l = f.Level;
                int il = f.ILevel;
                int hl = f.HLevel;
                int yIndex = ((mby * YStride) + mbx) * 16;
                int cIndex = ((mby * CStride) + mbx) * 8;
                if (mbx > 0)
                {
                    Filter246(Y, 16, l + 4, il, hl, yIndex, YStride, 1, false);
                    Filter246(Cb, 8, l + 4, il, hl, cIndex, CStride, 1, false);
                    Filter246(Cr, 8, l + 4, il, hl, cIndex, CStride, 1, false);
                }

                if (f.Inner)
                {
                    Filter246(Y, 16, l, il, hl, yIndex + 0x4, YStride, 1, true);
                    Filter246(Y, 16, l, il, hl, yIndex + 0x8, YStride, 1, true);
                    Filter246(Y, 16, l, il, hl, yIndex + 0xc, YStride, 1, true);
                    Filter246(Cb, 8, l, il, hl, cIndex + 0x4, CStride, 1, true);
                    Filter246(Cr, 8, l, il, hl, cIndex + 0x4, CStride, 1, true);
                }

                if (mby > 0)
                {
                    Filter246(Y, 16, l + 4, il, hl, yIndex, 1, YStride, false);
                    Filter246(Cb, 8, l + 4, il, hl, cIndex, 1, CStride, false);
                    Filter246(Cr, 8, l + 4, il, hl, cIndex, 1, CStride, false);
                }

                if (f.Inner)
                {
                    Filter246(Y, 16, l, il, hl, yIndex + (YStride * 0x4), 1, YStride, true);
                    Filter246(Y, 16, l, il, hl, yIndex + (YStride * 0x8), 1, YStride, true);
                    Filter246(Y, 16, l, il, hl, yIndex + (YStride * 0xc), 1, YStride, true);
                    Filter246(Cb, 8, l, il, hl, cIndex + (CStride * 0x4), 1, CStride, true);
                    Filter246(Cr, 8, l, il, hl, cIndex + (CStride * 0x4), 1, CStride, true);
                }
            }
        }
    }

    // Section 15.4: computes the loop filter parameters of each segment, for 16x16 luma prediction (index 0) and for the rest (index 1).
    private void ComputeFilterParams()
    {
        for (int i = 0; i < NSegment; i++)
        {
            int baseLevel = _filterLevel;
            if (_useSegment)
            {
                baseLevel = _segFilterStrength[i];
                if (_relativeDelta)
                {
                    baseLevel += _filterLevel;
                }
            }

            for (int j = 0; j < 2; j++)
            {
                FilterParam p = default;
                p.Inner = j != 0;
                int level = baseLevel;
                if (_useLfDelta)
                {
                    // libwebp's decoder has a "TODO: only CURRENT is handled for now."
                    level += _refLfDelta[0];
                    if (j != 0)
                    {
                        level += _modeLfDelta[0];
                    }
                }

                if (level <= 0)
                {
                    p.Level = 0;
                    _filterParams[(i * 2) + j] = p;
                    continue;
                }

                if (level > 63)
                {
                    level = 63;
                }

                int ilevel = level;
                if (_filterSharpness > 0)
                {
                    ilevel >>= _filterSharpness > 4 ? 2 : 1;
                    int x = 9 - _filterSharpness;
                    if (ilevel > x)
                    {
                        ilevel = x;
                    }
                }

                if (ilevel < 1)
                {
                    ilevel = 1;
                }

                p.ILevel = ilevel;
                p.Level = (2 * level) + ilevel;

                // This is a key frame (a still image has no other kind).
                p.HLevel = level < 15 ? 0 : level < 40 ? 1 : 2;
                _filterParams[(i * 2) + j] = p;
            }
        }
    }
}
