namespace Pdfwright.Images;

/// <summary>
/// Reads what a PDF needs to know of a JPEG file (ISO/IEC 10918-1) without decoding it: the size, the number of components and whether Adobe's
/// marker says the CMYK values are inverted. The file itself goes into the PDF as it is, with <c>DCTDecode</c>, so a reader decodes it; this
/// walks the markers only far enough to be sure that the file is whole and is one a PDF reader can decode (baseline, extended sequential or progressive Huffman, 8 bits).
/// </summary>
internal static class JpegReader
{
    /// <summary>Reads a JPEG file.</summary>
    /// <exception cref="ImageFormatException">The data is not a JPEG, is cut short, uses arithmetic coding or more than 8 bits, or is too large.</exception>
    internal static ImageSource Read(ReadOnlySpan<byte> file)
    {
        if (file.Length < 4 || file[0] != 0xFF || file[1] != 0xD8)
        {
            throw new ImageFormatException("The data is not a JPEG: it does not start with the start-of-image marker.");
        }

        int position = 2;
        int width = 0;
        int height = 0;
        int components = 0;
        bool frame = false;
        bool scan = false;
        bool inScan = false;
        bool adobe = false;
        int end = -1;

        while (end < 0)
        {
            int marker = NextMarker(file, ref position, inScan);
            if (marker < 0)
            {
                throw new ImageFormatException("The JPEG is cut short: it ends before the end-of-image marker.");
            }

            switch (marker)
            {
                case 0xD9:
                    if (!scan)
                    {
                        throw new ImageFormatException("The JPEG has no image data: the end-of-image marker comes before any scan.");
                    }

                    end = position + 2;
                    continue;
                case 0xD8:
                    throw new ImageFormatException("The JPEG has a second start-of-image marker.");
                case 0x01 or (>= 0xD0 and <= 0xD7):
                    position += 2;
                    continue;
            }

            // Every other marker has a segment: a 16-bit length that counts itself, then the payload.
            if (position + 4 > file.Length)
            {
                throw new ImageFormatException("The JPEG is cut short inside a marker.");
            }

            int length = (file[position + 2] << 8) | file[position + 3];
            if (length < 2 || position + 2 + length > file.Length)
            {
                throw new ImageFormatException(length < 2 ? "The JPEG has a marker with an impossible length." : "The JPEG is cut short: a marker segment runs past the end of the data.");
            }

            ReadOnlySpan<byte> payload = file.Slice(position + 4, length - 2);
            switch (marker)
            {
                case 0xC0 or 0xC1 or 0xC2:
                    if (frame)
                    {
                        throw new ImageFormatException("The JPEG has two frame headers.");
                    }

                    (width, height, components) = ReadFrame(payload);
                    frame = true;
                    break;
                case 0xC9 or 0xCA or 0xCB or 0xCD or 0xCE or 0xCF or 0xCC:
                    throw new ImageFormatException("The JPEG uses arithmetic coding, which PDF readers do not decode.");
                case 0xC3 or 0xC5 or 0xC6 or 0xC7:
                    throw new ImageFormatException("The JPEG is lossless or hierarchical, which PDF readers do not decode.");
                case 0xEE:
                    if (payload.Length >= 12 && payload[0] == 'A' && payload[1] == 'd' && payload[2] == 'o' && payload[3] == 'b' && payload[4] == 'e')
                    {
                        adobe = true;
                    }

                    break;
                case 0xDA:
                    if (!frame)
                    {
                        throw new ImageFormatException("The JPEG has a scan before its frame header.");
                    }

                    scan = true;
                    inScan = true;
                    position += 2 + length;
                    continue;
            }

            position += 2 + length;
            inScan = false;
        }

        if (!frame)
        {
            throw new ImageFormatException("The JPEG has no frame header.");
        }

        ImageColorSpace space = components switch { 1 => ImageColorSpace.Gray, 3 => ImageColorSpace.Rgb, _ => ImageColorSpace.Cmyk };

        // Adobe's CMYK files (Photoshop's) store the values inverted: 0 is full ink. The Decode array turns them round again.
        double[]? decode = components == 4 && adobe ? [1, 0, 1, 0, 1, 0, 1, 0] : null;
        return new ImageSource(width, height, space, file[..end].ToArray(), isJpeg: true, decode: decode);
    }

    private static (int Width, int Height, int Components) ReadFrame(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 6)
        {
            throw new ImageFormatException("The JPEG's frame header is cut short.");
        }

        int precision = payload[0];
        int height = (payload[1] << 8) | payload[2];
        int width = (payload[3] << 8) | payload[4];
        int components = payload[5];
        if (precision != 8)
        {
            throw new ImageFormatException($"The JPEG has {precision} bits for each sample: only 8 are supported.");
        }

        if (width == 0 || height == 0)
        {
            throw new ImageFormatException("The JPEG has no width or no height (a height set in a later segment is not supported).");
        }

        if ((long)width * height > ImageSource.MaxPixels)
        {
            throw new ImageFormatException($"The JPEG is {width} by {height} pixels, more than the {ImageSource.MaxPixels:N0} that are allowed.");
        }

        if (components is not (1 or 3 or 4))
        {
            throw new ImageFormatException($"The JPEG has {components} components: only gray (1), RGB (3) and CMYK (4) are supported.");
        }

        if (payload.Length < 6 + (3 * components))
        {
            throw new ImageFormatException("The JPEG's frame header is cut short: it does not list every component.");
        }

        return (width, height, components);
    }

    /// <summary>
    /// Moves to the next marker and returns its code, or -1 when the data ends first. Between segments only fill bytes may stand in front of a marker;
    /// inside the compressed data of a scan, stuffed zeros and restart markers belong to the data and are passed over.
    /// </summary>
    private static int NextMarker(ReadOnlySpan<byte> file, ref int position, bool inScan)
    {
        if (!inScan && position < file.Length && file[position] != 0xFF)
        {
            throw new ImageFormatException("The JPEG has data where a marker belongs: the file is damaged.");
        }

        while (position < file.Length)
        {
            if (file[position] != 0xFF)
            {
                position++;
                continue;
            }

            // Any number of 0xFF fill bytes may precede the marker's code.
            while (position + 1 < file.Length && file[position + 1] == 0xFF)
            {
                position++;
            }

            if (position + 1 >= file.Length)
            {
                return -1;
            }

            int code = file[position + 1];
            if (inScan && (code == 0x00 || code is >= 0xD0 and <= 0xD7))
            {
                position += 2;
                continue;
            }

            if (code == 0x00)
            {
                throw new ImageFormatException("The JPEG has a stuffed byte outside its compressed data: the file is damaged.");
            }

            return code;
        }

        return -1;
    }
}
