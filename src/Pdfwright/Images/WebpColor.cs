// Copyright (c) 2010, Google Inc. All rights reserved.
//
// Ported to C# from libwebp v1.6.0 (https://github.com/webmproject/libwebp), whose licence (BSD-3-Clause) and patent grant are in the NOTICE file of this package. Its text, as the licence requires:
//
// Redistribution and use in source and binary forms, with or without modification, are permitted provided that the following conditions are met:
//
//   * Redistributions of source code must retain the above copyright notice, this list of conditions and the following disclaimer.
//   * Redistributions in binary form must reproduce the above copyright notice, this list of conditions and the following disclaimer in the documentation and/or other materials provided with the distribution.
//   * Neither the name of Google nor the names of its contributors may be used to endorse or promote products derived from this software without specific prior written permission.
//
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE
// ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
// SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
// OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
//
// The conversion to C# and the additions are part of Pdfwright (MIT licence). The name of Google is not used to endorse this software.
//
// The conversion of the YUV samples of a lossy WebP to RGB, after libwebp v1.6.0 (src/dsp/yuv.h and src/dsp/upsampling.c, the C code without SIMD): the fixed-point BT.601 formulas and the "fancy" (bilinear) upsampling of the chroma
// planes that libwebp uses by default, so that the colours are those libwebp gives to within one level.

namespace Pdfwright.Images;

/// <summary>Turns the three planes of a VP8 frame into RGB samples the way libwebp does.</summary>
internal static class WebpColor
{
    // The fixed-point formulas are:
    //   R = (19077 . y             + 26149 . v - 14234) >> 6
    //   G = (19077 . y -  6419 . u - 13320 . v +  8708) >> 6
    //   B = (19077 . y + 33050 . u             - 17685) >> 6
    // where '.' is: a . b = ((a << 8) * b) >> 16, which keeps 8 bits of fractional precision before the final shift.
    private const int YuvFix2 = 6;
    private const int YuvMask2 = (256 << YuvFix2) - 1;

    private static int MultHi(int v, int coeff) => (v * coeff) >> 8;

    private static byte Clip8(int v) => (v & ~YuvMask2) == 0 ? (byte)(v >> YuvFix2) : v < 0 ? (byte)0 : (byte)255;

    private static void YuvToRgb(int y, int u, int v, byte[] rgb, int at)
    {
        int yy = MultHi(y, 19077);
        rgb[at] = Clip8(yy + MultHi(v, 26149) - 14234);
        rgb[at + 1] = Clip8(yy - MultHi(u, 6419) - MultHi(v, 13320) + 8708);
        rgb[at + 2] = Clip8(yy + MultHi(u, 33050) - 17685);
    }

    /// <summary>The picture of a decoded frame as red, green, blue bytes (three for each pixel, rows one after another).</summary>
    internal static byte[] ToRgb(Vp8Decoder frame)
    {
        int width = frame.Width;
        int height = frame.Height;
        byte[] y = frame.Y;
        byte[] u = frame.Cb;
        byte[] v = frame.Cr;
        int yStride = frame.YStride;
        int cStride = frame.CStride;
        byte[] rgb = new byte[3L * width * height];
        int rowBytes = 3 * width;

        // The first row is special: the chroma samples are mirrored at the border.
        UpsampleLinePair(y, 0, -1, u, v, 0, 0, rgb, 0, -1, width);

        // The rest in pairs of rows (1 and 2, 3 and 4, ...), each pair between two rows of chroma.
        int row = 1;
        for (; row + 1 < height; row += 2)
        {
            int topUv = ((row - 1) >> 1) * cStride;
            int curUv = ((row + 1) >> 1) * cStride;
            UpsampleLinePair(y, row * yStride, (row + 1) * yStride, u, v, topUv, curUv, rgb, row * rowBytes, (row + 1) * rowBytes, width);
        }

        // The last row of a picture that has an even number of rows is on its own, and its chroma is mirrored as well.
        if (row < height)
        {
            int uv = (row >> 1) * cStride;
            UpsampleLinePair(y, row * yStride, -1, u, v, uv, uv, rgb, row * rowBytes, -1, width);
        }

        return rgb;
    }

    // Given the samples of chroma laid out in a square as
    //   [a b]
    //   [c d]
    // the four luma pixels between them get
    //   ([9a + 3b + 3c +  d   3a + 9b +  c + 3d] + [8 8]) / 16   (the upper pair)
    //   ([3a +  b + 9c + 3d    a + 3b + 3c + 9d] + [8 8]) / 16   (the lower pair)
    // computed as libwebp does it, in steps that round the same way.
    private static void UpsampleLinePair(byte[] y, int topY, int botY, byte[] u, byte[] v, int topUv, int curUv, byte[] dst, int topDst, int botDst, int len)
    {
        int lastPixelPair = (len - 1) >> 1;
        int tlU = u[topUv];
        int tlV = v[topUv];
        int lU = u[curUv];
        int lV = v[curUv];

        YuvToRgb(y[topY], ((3 * tlU) + lU + 2) >> 2, ((3 * tlV) + lV + 2) >> 2, dst, topDst);
        if (botY >= 0)
        {
            YuvToRgb(y[botY], ((3 * lU) + tlU + 2) >> 2, ((3 * lV) + tlV + 2) >> 2, dst, botDst);
        }

        for (int x = 1; x <= lastPixelPair; x++)
        {
            int tU = u[topUv + x];
            int tV = v[topUv + x];
            int cU = u[curUv + x];
            int cV = v[curUv + x];

            // The values that go with the first and the second diagonal.
            int avgU = tlU + tU + lU + cU + 8;
            int avgV = tlV + tV + lV + cV + 8;
            int diag12U = (avgU + (2 * (tU + lU))) >> 3;
            int diag12V = (avgV + (2 * (tV + lV))) >> 3;
            int diag03U = (avgU + (2 * (tlU + cU))) >> 3;
            int diag03V = (avgV + (2 * (tlV + cV))) >> 3;

            YuvToRgb(y[topY + (2 * x) - 1], (diag12U + tlU) >> 1, (diag12V + tlV) >> 1, dst, topDst + (((2 * x) - 1) * 3));
            YuvToRgb(y[topY + (2 * x)], (diag03U + tU) >> 1, (diag03V + tV) >> 1, dst, topDst + (2 * x * 3));
            if (botY >= 0)
            {
                YuvToRgb(y[botY + (2 * x) - 1], (diag03U + lU) >> 1, (diag03V + lV) >> 1, dst, botDst + (((2 * x) - 1) * 3));
                YuvToRgb(y[botY + (2 * x)], (diag12U + cU) >> 1, (diag12V + cV) >> 1, dst, botDst + (2 * x * 3));
            }

            tlU = tU;
            tlV = tV;
            lU = cU;
            lV = cV;
        }

        if ((len & 1) == 0)
        {
            YuvToRgb(y[topY + len - 1], ((3 * tlU) + lU + 2) >> 2, ((3 * tlV) + lV + 2) >> 2, dst, topDst + ((len - 1) * 3));
            if (botY >= 0)
            {
                YuvToRgb(y[botY + len - 1], ((3 * lU) + tlU + 2) >> 2, ((3 * lV) + tlV + 2) >> 2, dst, botDst + ((len - 1) * 3));
            }
        }
    }
}
