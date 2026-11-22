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
// The inverse transforms of the VP8L lossless format (section 3 of the WebP lossless bitstream specification), after golang.org/x/image/vp8l (transform.go).

namespace Pdfwright.Images;

/// <summary>An invertible transform of the pixels of a VP8L image, and its parameters.</summary>
internal sealed class Vp8lTransform
{
    /// <summary>The type: 0 predictor, 1 cross-colour, 2 subtract green, 3 colour indexing.</summary>
    internal int Type;

    /// <summary>
    /// The width of the image before the transform (equivalently, after its inverse). The colour indexing transform can make the width smaller: a 50 pixel wide image that
    /// needs only 4 bits for each colour index is transformed into one that is 25 pixels wide.
    /// </summary>
    internal int OldWidth;

    /// <summary>The log-2 of the size of the tiles for the predictor and cross-colour transforms; for the colour indexing transform, 8 shifted right by this is the number of bits of each index.</summary>
    internal int Bits;

    /// <summary>The values of the tiles for the predictor and cross-colour transforms, the palette (256 entries) for the colour indexing transform.</summary>
    internal byte[] Pix = [];

    private static int NTiles(int size, int bits) => (size + (1 << bits) - 1) >> bits;

    /// <summary>Applies the inverse of the transform to the pixels of an image of <paramref name="h"/> rows, which may return a new array.</summary>
    internal byte[] Inverse(byte[] pix, int h) => Type switch
    {
        0 => InversePredictor(pix, h),
        1 => InverseCrossColor(pix, h),
        2 => InverseSubtractGreen(pix),
        _ => InverseColorIndexing(pix, h),
    };

    private static byte Avg2(byte a, byte b) => (byte)((a + b) / 2);

    private static byte ClampAddSubtractFull(byte a, byte b, byte c)
    {
        int x = a + b - c;
        return x < 0 ? (byte)0 : x > 255 ? (byte)255 : (byte)x;
    }

    private static byte ClampAddSubtractHalf(byte a, byte b)
    {
        int x = a + ((a - b) / 2);
        return x < 0 ? (byte)0 : x > 255 ? (byte)255 : (byte)x;
    }

    private byte[] InversePredictor(byte[] pix, int h)
    {
        if (OldWidth == 0 || h == 0)
        {
            return pix;
        }

        // The first pixel's predictor is mode 0 (opaque black).
        pix[3] += 0xff;
        int p = 4;
        int mask = (1 << Bits) - 1;
        for (int x = 1; x < OldWidth; x++)
        {
            // The rest of the first row's predictor is mode 1 (L).
            pix[p] += pix[p - 4];
            pix[p + 1] += pix[p - 3];
            pix[p + 2] += pix[p - 2];
            pix[p + 3] += pix[p - 1];
            p += 4;
        }

        int top = 0;
        int tilesPerRow = NTiles(OldWidth, Bits);
        for (int y = 1; y < h; y++)
        {
            // The first column's predictor is mode 2 (T).
            pix[p] += pix[top];
            pix[p + 1] += pix[top + 1];
            pix[p + 2] += pix[top + 2];
            pix[p + 3] += pix[top + 3];
            p += 4;
            top += 4;

            int q = 4 * (y >> Bits) * tilesPerRow;
            int predictorMode = Pix[q + 1] & 0x0f;
            q += 4;
            for (int x = 1; x < OldWidth; x++)
            {
                if ((x & mask) == 0)
                {
                    predictorMode = Pix[q + 1] & 0x0f;
                    q += 4;
                }

                switch (predictorMode)
                {
                    case 1: // L.
                        for (int k = 0; k < 4; k++)
                        {
                            pix[p + k] += pix[p - 4 + k];
                        }

                        break;

                    case 2: // T.
                        for (int k = 0; k < 4; k++)
                        {
                            pix[p + k] += pix[top + k];
                        }

                        break;

                    case 3: // TR.
                        for (int k = 0; k < 4; k++)
                        {
                            pix[p + k] += pix[top + 4 + k];
                        }

                        break;

                    case 4: // TL.
                        for (int k = 0; k < 4; k++)
                        {
                            pix[p + k] += pix[top - 4 + k];
                        }

                        break;

                    case 5: // Average2(Average2(L, TR), T).
                        for (int k = 0; k < 4; k++)
                        {
                            pix[p + k] += Avg2(Avg2(pix[p - 4 + k], pix[top + 4 + k]), pix[top + k]);
                        }

                        break;

                    case 6: // Average2(L, TL).
                        for (int k = 0; k < 4; k++)
                        {
                            pix[p + k] += Avg2(pix[p - 4 + k], pix[top - 4 + k]);
                        }

                        break;

                    case 7: // Average2(L, T).
                        for (int k = 0; k < 4; k++)
                        {
                            pix[p + k] += Avg2(pix[p - 4 + k], pix[top + k]);
                        }

                        break;

                    case 8: // Average2(TL, T).
                        for (int k = 0; k < 4; k++)
                        {
                            pix[p + k] += Avg2(pix[top - 4 + k], pix[top + k]);
                        }

                        break;

                    case 9: // Average2(T, TR).
                        for (int k = 0; k < 4; k++)
                        {
                            pix[p + k] += Avg2(pix[top + k], pix[top + 4 + k]);
                        }

                        break;

                    case 10: // Average2(Average2(L, TL), Average2(T, TR)).
                        for (int k = 0; k < 4; k++)
                        {
                            pix[p + k] += Avg2(Avg2(pix[p - 4 + k], pix[top - 4 + k]), Avg2(pix[top + k], pix[top + 4 + k]));
                        }

                        break;

                    case 11: // Select(L, T, TL).
                    {
                        int l = 0;
                        int t = 0;
                        for (int k = 0; k < 4; k++)
                        {
                            l += Math.Abs(pix[top - 4 + k] - pix[top + k]);
                            t += Math.Abs(pix[top - 4 + k] - pix[p - 4 + k]);
                        }

                        int source = l < t ? p - 4 : top;
                        for (int k = 0; k < 4; k++)
                        {
                            pix[p + k] += pix[source + k];
                        }

                        break;
                    }

                    case 12: // ClampAddSubtractFull(L, T, TL).
                        for (int k = 0; k < 4; k++)
                        {
                            pix[p + k] += ClampAddSubtractFull(pix[p - 4 + k], pix[top + k], pix[top - 4 + k]);
                        }

                        break;

                    case 13: // ClampAddSubtractHalf(Average2(L, T), TL).
                        for (int k = 0; k < 4; k++)
                        {
                            pix[p + k] += ClampAddSubtractHalf(Avg2(pix[p - 4 + k], pix[top + k]), pix[top - 4 + k]);
                        }

                        break;

                    default: // Mode 0, and the two unused modes 14 and 15, which libwebp treats as mode 0: opaque black.
                        pix[p + 3] += 0xff;
                        break;
                }

                p += 4;
                top += 4;
            }
        }

        return pix;
    }

    private byte[] InverseCrossColor(byte[] pix, int h)
    {
        int greenToRed = 0;
        int greenToBlue = 0;
        int redToBlue = 0;
        int p = 0;
        int mask = (1 << Bits) - 1;
        int tilesPerRow = NTiles(OldWidth, Bits);
        for (int y = 0; y < h; y++)
        {
            int q = 4 * (y >> Bits) * tilesPerRow;
            for (int x = 0; x < OldWidth; x++)
            {
                if ((x & mask) == 0)
                {
                    redToBlue = (sbyte)Pix[q];
                    greenToBlue = (sbyte)Pix[q + 1];
                    greenToRed = (sbyte)Pix[q + 2];
                    q += 4;
                }

                byte red = pix[p];
                byte green = pix[p + 1];
                byte blue = pix[p + 2];
                red += (byte)((uint)(greenToRed * (sbyte)green) >> 5);
                blue += (byte)((uint)(greenToBlue * (sbyte)green) >> 5);
                blue += (byte)((uint)(redToBlue * (sbyte)red) >> 5);
                pix[p] = red;
                pix[p + 2] = blue;
                p += 4;
            }
        }

        return pix;
    }

    private static byte[] InverseSubtractGreen(byte[] pix)
    {
        for (int p = 0; p < pix.Length; p += 4)
        {
            byte green = pix[p + 1];
            pix[p] += green;
            pix[p + 2] += green;
        }

        return pix;
    }

    private byte[] InverseColorIndexing(byte[] pix, int h)
    {
        if (Bits == 0)
        {
            for (int p = 0; p < pix.Length; p += 4)
            {
                int i = 4 * pix[p + 1];
                pix[p] = Pix[i];
                pix[p + 1] = Pix[i + 1];
                pix[p + 2] = Pix[i + 2];
                pix[p + 3] = Pix[i + 3];
            }

            return pix;
        }

        uint vMask = 0;
        int xMask = 0;
        int bitsPerPixel = 8 >> Bits;
        switch (Bits)
        {
            case 1:
                vMask = 0x0f;
                xMask = 0x01;
                break;
            case 2:
                vMask = 0x03;
                xMask = 0x03;
                break;
            case 3:
                vMask = 0x01;
                xMask = 0x07;
                break;
        }

        byte[] dst = new byte[4L * OldWidth * h];
        int d = 0;
        int src = 0;
        uint v = 0;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < OldWidth; x++)
            {
                if ((x & xMask) == 0)
                {
                    v = pix[src + 1];
                    src += 4;
                }

                int i = 4 * (int)(v & vMask);
                dst[d] = Pix[i];
                dst[d + 1] = Pix[i + 1];
                dst[d + 2] = Pix[i + 2];
                dst[d + 3] = Pix[i + 3];
                d += 4;
                v >>= bitsPerPixel;
            }
        }

        return dst;
    }
}
