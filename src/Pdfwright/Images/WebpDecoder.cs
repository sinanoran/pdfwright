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
// The container of a WebP file (RIFF with the chunks VP8, VP8L, VP8X and ALPH) and the alpha plane of a lossy image, after golang.org/x/image/webp (decode.go), with the limits of this library and libwebp's leniency about a few chunks added.

using System.Buffers.Binary;

namespace Pdfwright.Images;

/// <summary>
/// Reads a WebP file (RIFF container, lossy VP8, lossless VP8L, the extended format VP8X with an alpha plane) into the form a PDF image wants: 8-bit RGB samples and, when the picture has transparency, a separate mask,
/// as <see cref="PngDecoder"/> gives them. An animated WebP is refused; the colour profile (ICCP), EXIF and XMP chunks are skipped. The size is checked against the pixel limit before anything is allocated for the
/// pixels, the size of the picture must be what its header (VP8X) says, and every failure is an <see cref="ImageFormatException"/>, never an exception of the runtime.
/// </summary>
internal static class WebpDecoder
{
    private const string AnimatedMessage = "The WebP is an animation, and this library reads still pictures only: it does not use the first frame of an animated WebP.";

    /// <summary>Whether the data starts like a WebP file: <c>RIFF</c>, four bytes of size, <c>WEBP</c>.</summary>
    internal static bool HasSignature(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8);

    /// <summary>Reads a WebP file.</summary>
    /// <exception cref="ImageFormatException">The data is not a WebP, is damaged or cut short, is an animation, or is larger than the limits allow.</exception>
    internal static ImageSource Decode(ReadOnlySpan<byte> file, long maxPixels = ImageSource.DefaultMaxPixels)
    {
        if (!HasSignature(file))
        {
            throw new ImageFormatException("The data is not a WebP: the RIFF header is wrong.");
        }

        byte[] data = file.ToArray();
        try
        {
            return Read(data, maxPixels);
        }
        catch (Exception exception) when (exception is IndexOutOfRangeException or ArgumentException or OverflowException or InvalidOperationException)
        {
            // A last line of defence: the decoders check what they read, and a damaged file that gets past a check must still not surface as an exception of the runtime.
            throw new ImageFormatException("The WebP is damaged: " + exception.Message, exception);
        }
    }

    private static ImageSource Read(byte[] data, long maxPixels)
    {
        uint riffSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4));
        if (riffSize < 12 || riffSize > int.MaxValue - 8)
        {
            throw new ImageFormatException("The WebP's RIFF size is wrong.");
        }

        if ((long)riffSize + 8 > data.Length)
        {
            throw new ImageFormatException("The WebP is cut short: the RIFF header says it is longer than the data.");
        }

        int end = (int)riffSize + 8;
        bool seenVp8x = false;
        int canvasWidth = 0;
        int canvasHeight = 0;
        int alphaStart = -1;
        int alphaLength = 0;
        int position = 12;
        while (true)
        {
            if (position + 8 > end)
            {
                throw new ImageFormatException("The WebP has no image data.");
            }

            ReadOnlySpan<byte> id = data.AsSpan(position, 4);
            uint declared = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(position + 4));
            int start = position + 8;
            if (declared > end - start)
            {
                throw new ImageFormatException("The WebP is cut short: a chunk is longer than the data that is left.");
            }

            int length = (int)declared;
            position = start + length + (length & 1);

            if (id.SequenceEqual("VP8X"u8))
            {
                if (seenVp8x)
                {
                    throw new ImageFormatException("The WebP has two VP8X chunks.");
                }

                if (length != 10)
                {
                    throw new ImageFormatException("The WebP's VP8X chunk has the wrong length.");
                }

                seenVp8x = true;
                if ((data[start] & 0x02) != 0)
                {
                    throw new ImageFormatException(AnimatedMessage);
                }

                canvasWidth = (data[start + 4] | (data[start + 5] << 8) | (data[start + 6] << 16)) + 1;
                canvasHeight = (data[start + 7] | (data[start + 8] << 8) | (data[start + 9] << 16)) + 1;
                if (canvasWidth > ImageSource.MaxSide || canvasHeight > ImageSource.MaxSide)
                {
                    throw new ImageFormatException(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"The WebP is {canvasWidth} by {canvasHeight} pixels: a side may have at most {ImageSource.MaxSide:N0}."));
                }

                ImageSource.CheckPixels("WebP", canvasWidth, canvasHeight, maxPixels);
            }
            else if (id.SequenceEqual("ANIM"u8) || id.SequenceEqual("ANMF"u8))
            {
                throw new ImageFormatException(AnimatedMessage);
            }
            else if (id.SequenceEqual("ALPH"u8))
            {
                if (!seenVp8x)
                {
                    throw new ImageFormatException("The WebP has an ALPH chunk without a VP8X chunk before it.");
                }

                // Only the first one counts; the plane is decoded once the picture's size is known to be right.
                if (alphaStart < 0)
                {
                    alphaStart = start;
                    alphaLength = length;
                }
            }
            else if (id.SequenceEqual("VP8 "u8))
            {
                return ReadLossy(data, start, length, seenVp8x, canvasWidth, canvasHeight, alphaStart, alphaLength, maxPixels);
            }
            else if (id.SequenceEqual("VP8L"u8))
            {
                return ReadLossless(data, start, length, seenVp8x, canvasWidth, canvasHeight, maxPixels);
            }
        }
    }

    private static ImageSource ReadLossy(byte[] data, int start, int length, bool seenVp8x, int canvasWidth, int canvasHeight, int alphaStart, int alphaLength, long maxPixels)
    {
        Vp8Decoder vp8 = new(data, start, length);
        vp8.ReadFrameHeader(maxPixels);
        if (seenVp8x && (vp8.Width != canvasWidth || vp8.Height != canvasHeight))
        {
            throw new ImageFormatException("The size of the WebP's picture is not the size its VP8X chunk gives.");
        }

        byte[]? alpha = alphaStart >= 0 ? ReadAlpha(data, alphaStart, alphaLength, vp8.Width, vp8.Height) : null;
        vp8.DecodeFrame();
        byte[] rgb = WebpColor.ToRgb(vp8);
        return new ImageSource(vp8.Width, vp8.Height, ImageColorSpace.Rgb, rgb, isJpeg: false, mask: OpaqueToNull(alpha));
    }

    private static ImageSource ReadLossless(byte[] data, int start, int length, bool seenVp8x, int canvasWidth, int canvasHeight, long maxPixels)
    {
        Vp8lDecoder vp8l = new(data, start, length);
        vp8l.ReadHeader(maxPixels);
        if (seenVp8x && (vp8l.Width != canvasWidth || vp8l.Height != canvasHeight))
        {
            throw new ImageFormatException("The size of the WebP's picture is not the size its VP8X chunk gives.");
        }

        byte[] pix = vp8l.Decode();
        int pixels = vp8l.Width * vp8l.Height;
        byte[] rgb = new byte[3 * pixels];
        byte[] alpha = new byte[pixels];
        byte opaque = 255;
        for (int i = 0, p = 0; i < pixels; i++, p += 4)
        {
            rgb[3 * i] = pix[p];
            rgb[(3 * i) + 1] = pix[p + 1];
            rgb[(3 * i) + 2] = pix[p + 2];
            alpha[i] = pix[p + 3];
            opaque &= pix[p + 3];
        }

        return new ImageSource(vp8l.Width, vp8l.Height, ImageColorSpace.Rgb, rgb, isJpeg: false, mask: opaque == 255 ? null : alpha);
    }

    /// <summary>A mask in which every pixel is opaque says nothing, and is left out.</summary>
    private static byte[]? OpaqueToNull(byte[]? alpha)
    {
        if (alpha is null)
        {
            return null;
        }

        foreach (byte a in alpha)
        {
            if (a != 255)
            {
                return alpha;
            }
        }

        return null;
    }

    /// <summary>Decodes the data of an ALPH chunk to one byte of opacity for each pixel.</summary>
    private static byte[] ReadAlpha(byte[] data, int start, int length, int width, int height)
    {
        if (length < 1)
        {
            throw new ImageFormatException("The WebP's ALPH chunk is empty.");
        }

        // The Pre-processing, Filter and Compression bits (the pre-processing is a hint to a viewer and changes nothing here).
        int header = data[start];
        int compression = header & 0x03;
        int filter = (header >> 2) & 0x03;
        int pixels = width * height;
        byte[] alpha;
        switch (compression)
        {
            case 0:
                if (length - 1 < pixels)
                {
                    throw new ImageFormatException("The WebP's alpha data is cut short.");
                }

                alpha = new byte[pixels];
                Array.Copy(data, start + 1, alpha, 0, pixels);
                break;

            case 1:
            {
                // The lossless coded alpha has no header: the size is that of the picture, and the values are the green channel.
                Vp8lDecoder vp8l = new(data, start + 1, length - 1);
                vp8l.SetSize(width, height);
                byte[] pix = vp8l.Decode();
                alpha = new byte[pixels];
                for (int i = 0; i < pixels; i++)
                {
                    alpha[i] = pix[(4 * i) + 1];
                }

                break;
            }

            default:
                throw new ImageFormatException("The WebP's alpha data uses a compression method that the format does not define.");
        }

        UnfilterAlpha(alpha, width, filter);
        return alpha;
    }

    // Undoes the prediction filter of the alpha plane (0 none, 1 horizontal, 2 vertical, 3 gradient).
    private static void UnfilterAlpha(byte[] alpha, int stride, int filter)
    {
        if (alpha.Length == 0 || stride == 0 || filter == 0)
        {
            return;
        }

        switch (filter)
        {
            case 1: // Horizontal.
                for (int i = 1; i < stride; i++)
                {
                    alpha[i] += alpha[i - 1];
                }

                for (int i = stride; i < alpha.Length; i += stride)
                {
                    // The first column is equivalent to the vertical filter.
                    alpha[i] += alpha[i - stride];
                    for (int j = 1; j < stride; j++)
                    {
                        alpha[i + j] += alpha[i + j - 1];
                    }
                }

                break;

            case 2: // Vertical.
                // The first row is equivalent to the horizontal filter.
                for (int i = 1; i < stride; i++)
                {
                    alpha[i] += alpha[i - 1];
                }

                for (int i = stride; i < alpha.Length; i++)
                {
                    alpha[i] += alpha[i - stride];
                }

                break;

            default: // Gradient.
                // The first row is equivalent to the horizontal filter.
                for (int i = 1; i < stride; i++)
                {
                    alpha[i] += alpha[i - 1];
                }

                for (int i = stride; i < alpha.Length; i += stride)
                {
                    // The first column is equivalent to the vertical filter.
                    alpha[i] += alpha[i - stride];

                    // The interior is predicted from the three pixels above and to the left.
                    for (int j = 1; j < stride; j++)
                    {
                        int c = alpha[i + j - stride - 1];
                        int b = alpha[i + j - stride];
                        int a = alpha[i + j - 1];
                        int x = a + b - c;
                        x = x < 0 ? 0 : x > 255 ? 255 : x;
                        alpha[i + j] += (byte)x;
                    }
                }

                break;
        }
    }
}
