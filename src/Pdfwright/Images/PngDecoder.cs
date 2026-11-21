using System.Buffers.Binary;
using System.IO.Compression;

namespace Pdfwright.Images;

/// <summary>
/// Reads a PNG (ISO/IEC 15948) into the form a PDF image wants: 8-bit samples in a device colour space, a palette, and the transparency as a
/// separate mask. Every chunk's CRC is checked; only IHDR, PLTE, tRNS, IDAT and IEND are read, and the other ancillary chunks (gAMA, iCCP, sRGB, cHRM and the rest) are
/// skipped, while an unknown chunk whose type begins with a capital letter (a critical one, which a decoder may not skip) is refused, so the colours are those of the raw samples (see the known limitations). Nothing is allocated for a size above the limits, and
/// data that is damaged or too short for its header is an <see cref="ImageFormatException"/>, never an exception of the runtime.
/// </summary>
internal static class PngDecoder
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    // The Adam7 passes: where the first pixel of each pass is, and the distance between its pixels.
    private static readonly (int X, int Y, int StepX, int StepY)[] Passes =
    [
        (0, 0, 8, 8), (4, 0, 8, 8), (0, 4, 4, 8), (2, 0, 4, 4), (0, 2, 2, 4), (1, 0, 2, 2), (0, 1, 1, 2),
    ];

    private static readonly uint[] CrcTable = MakeCrcTable();

    internal static bool HasSignature(ReadOnlySpan<byte> bytes) => bytes.Length >= Signature.Length && bytes[..Signature.Length].SequenceEqual(Signature);

    /// <summary>Reads a PNG file.</summary>
    /// <exception cref="ImageFormatException">The data is not a PNG, is damaged or cut short, or is larger than the limits allow.</exception>
    internal static ImageSource Decode(ReadOnlySpan<byte> file)
    {
        if (!HasSignature(file))
        {
            throw new ImageFormatException("The data is not a PNG: the signature is wrong.");
        }

        Header? header = null;
        byte[]? palette = null;
        byte[]? transparency = null;
        List<(int Start, int Length)> slices = [];
        long total = 0;
        bool end = false;
        int position = Signature.Length;
        while (!end)
        {
            if (position + 12 > file.Length)
            {
                throw new ImageFormatException("The PNG is cut short: it ends before the IEND chunk.");
            }

            uint length = BinaryPrimitives.ReadUInt32BigEndian(file[position..]);
            if (length > int.MaxValue || position + 12L + length > file.Length)
            {
                throw new ImageFormatException("The PNG is cut short or damaged: a chunk is longer than the data that is left.");
            }

            ReadOnlySpan<byte> type = file.Slice(position + 4, 4);
            ReadOnlySpan<byte> data = file.Slice(position + 8, (int)length);
            uint stored = BinaryPrimitives.ReadUInt32BigEndian(file[(position + 8 + (int)length)..]);
            if (Crc(type, data) != stored)
            {
                throw new ImageFormatException($"The PNG is damaged: the CRC of the {Name(type)} chunk does not match.");
            }

            if (header is null && !type.SequenceEqual("IHDR"u8))
            {
                throw new ImageFormatException("The PNG is damaged: the first chunk is not IHDR.");
            }

            if (type.SequenceEqual("IHDR"u8))
            {
                if (header is not null)
                {
                    throw new ImageFormatException("The PNG has two IHDR chunks.");
                }

                header = ReadHeader(data);
            }
            else if (type.SequenceEqual("PLTE"u8))
            {
                if (palette is not null)
                {
                    throw new ImageFormatException("The PNG has two PLTE chunks.");
                }

                if (data.Length == 0 || data.Length % 3 != 0 || data.Length > 768)
                {
                    throw new ImageFormatException("The PNG's palette has an impossible length.");
                }

                palette = data.ToArray();
            }
            else if (type.SequenceEqual("tRNS"u8))
            {
                transparency = data.ToArray();
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                slices.Add((position + 8, (int)length));
                total += length;
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                end = true;
            }
            else if ((type[0] & 0x20) == 0)
            {
                // The first letter's case says whether a decoder may skip a chunk: an unknown critical one means the file needs more than this reader knows.
                throw new ImageFormatException($"The PNG has a critical chunk ({Name(type)}) that this reader does not know, so it cannot show the image right.");
            }

            position += 12 + (int)length;
        }

        if (total == 0)
        {
            throw new ImageFormatException("The PNG has no image data.");
        }

        // One buffer of exactly the compressed size (the input is a span, which a stream cannot keep), inflated from as it is.
        if (total > int.MaxValue)
        {
            throw new ImageFormatException("The PNG's image data is too large.");
        }

        byte[] compressed = new byte[total];
        int filled = 0;
        foreach ((int start, int length) in slices)
        {
            file.Slice(start, length).CopyTo(compressed.AsSpan(filled));
            filled += length;
        }

        return Build(header!, palette, transparency, compressed);
    }

    private static Header ReadHeader(ReadOnlySpan<byte> data)
    {
        if (data.Length != 13)
        {
            throw new ImageFormatException("The PNG's IHDR chunk has the wrong length.");
        }

        uint width = BinaryPrimitives.ReadUInt32BigEndian(data);
        uint height = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
        int depth = data[8];
        int colorType = data[9];
        if (width == 0 || height == 0)
        {
            throw new ImageFormatException("The PNG has no width or no height.");
        }

        if (width > ImageSource.MaxSide || height > ImageSource.MaxSide)
        {
            throw new ImageFormatException($"The PNG is {width} by {height} pixels: a side may have at most {ImageSource.MaxSide:N0}.");
        }

        if ((long)width * height > ImageSource.MaxPixels)
        {
            throw new ImageFormatException($"The PNG is {width} by {height} pixels, more than the {ImageSource.MaxPixels:N0} that are allowed.");
        }

        bool allowed = colorType switch
        {
            0 => depth is 1 or 2 or 4 or 8 or 16,
            2 or 4 or 6 => depth is 8 or 16,
            3 => depth is 1 or 2 or 4 or 8,
            _ => false,
        };
        if (!allowed)
        {
            throw new ImageFormatException($"The PNG has a colour type ({colorType}) and a bit depth ({depth}) that the format does not allow together.");
        }

        if (data[10] != 0 || data[11] != 0)
        {
            throw new ImageFormatException("The PNG uses a compression or filter method that the format does not define.");
        }

        if (data[12] > 1)
        {
            throw new ImageFormatException("The PNG uses an interlace method that the format does not define.");
        }

        return new Header((int)width, (int)height, depth, colorType, data[12] == 1);
    }

    private static ImageSource Build(Header header, byte[]? palette, byte[]? transparency, byte[] compressed)
    {
        int width = header.Width;
        int height = header.Height;
        int depth = header.Depth;
        int colorType = header.ColorType;
        int channels = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, _ => 4 };
        int bitsPerPixel = channels * depth;
        int bytesPerPixel = Math.Max(1, bitsPerPixel / 8);
        int maxValue = (1 << Math.Min(depth, 8)) - 1;

        if (colorType == 3)
        {
            if (palette is null)
            {
                throw new ImageFormatException("The PNG is an indexed image and has no palette.");
            }
        }
        else
        {
            palette = null;
        }

        // What the transparency chunk means depends on the colour type; a chunk of the wrong size is damage.
        int paletteEntries = palette is null ? 0 : palette.Length / 3;
        int[]? colorKey = null;
        byte[]? paletteAlpha = null;
        switch (colorType)
        {
            case 0 when transparency is not null:
                RequireLength(transparency, 2);
                colorKey = [BinaryPrimitives.ReadUInt16BigEndian(transparency) & ((1 << depth) - 1)];
                break;
            case 2 when transparency is not null:
                RequireLength(transparency, 6);
                colorKey =
                [
                    BinaryPrimitives.ReadUInt16BigEndian(transparency) & ((1 << depth) - 1),
                    BinaryPrimitives.ReadUInt16BigEndian(transparency.AsSpan(2)) & ((1 << depth) - 1),
                    BinaryPrimitives.ReadUInt16BigEndian(transparency.AsSpan(4)) & ((1 << depth) - 1),
                ];
                break;
            case 3 when transparency is not null:
                if (transparency.Length > paletteEntries)
                {
                    throw new ImageFormatException("The PNG has more transparency values than palette entries.");
                }

                if (transparency.Any(a => a != 255))
                {
                    paletteAlpha = transparency;
                }

                break;
        }

        bool expandPalette = paletteAlpha is not null;
        bool hasAlpha = colorType is 4 or 6 || colorKey is not null || expandPalette;
        int colorBytes = colorType switch { 0 or 3 when !expandPalette => 1, 4 => 1, _ => 3 };

        // The size of what the compressed data must hold, before anything of that size is allocated: a header that promises far more than
        // the data can hold (deflate cannot do better than about 1032 to 1) is a damaged file.
        (int X, int Y, int StepX, int StepY)[] passes = header.Interlaced ? Passes : [(0, 0, 1, 1)];
        long expected = 0;
        foreach ((int x0, int y0, int dx, int dy) in passes)
        {
            int pw = (width - x0 + dx - 1) / dx;
            int ph = (height - y0 + dy - 1) / dy;
            if (pw > 0 && ph > 0)
            {
                expected += ph * (1L + (((long)pw * bitsPerPixel) + 7) / 8);
            }
        }

        if (expected > ((long)compressed.Length + 16) * 1100)
        {
            throw new ImageFormatException("The PNG is cut short or damaged: its image data is too small for its size.");
        }

        byte[] samples = new byte[(long)width * height * colorBytes];
        byte[]? alpha = hasAlpha ? new byte[(long)width * height] : null;

        try
        {
            using MemoryStream source = new(compressed, writable: false);
            using ZLibStream inflater = new(source, CompressionMode.Decompress);
            foreach ((int x0, int y0, int dx, int dy) in passes)
            {
                int pw = (width - x0 + dx - 1) / dx;
                int ph = (height - y0 + dy - 1) / dy;
                if (pw <= 0 || ph <= 0)
                {
                    continue;
                }

                int stride = (int)((((long)pw * bitsPerPixel) + 7) / 8);
                byte[] line = new byte[stride + 1];
                byte[] current = new byte[stride];
                byte[] previous = new byte[stride];
                for (int row = 0; row < ph; row++)
                {
                    ReadLine(inflater, line);
                    Array.Copy(line, 1, current, 0, stride);
                    Unfilter(line[0], current, previous, bytesPerPixel);
                    int y = y0 + (row * dy);
                    for (int column = 0; column < pw; column++)
                    {
                        int pixel = (y * width) + x0 + (column * dx);
                        Pixel(current, column, depth, maxValue, colorType, palette, paletteAlpha, colorKey, samples, alpha, pixel, colorBytes);
                    }

                    (current, previous) = (previous, current);
                }
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException)
        {
            // The inflater reports damage as InvalidDataException, or, from the native zlib, as a ZLibException (an IOException).
            throw new ImageFormatException("The PNG's image data is damaged: " + exception.Message, exception);
        }

        ImageColorSpace space = colorType switch
        {
            3 when !expandPalette => ImageColorSpace.Indexed,
            0 or 4 => ImageColorSpace.Gray,
            _ => ImageColorSpace.Rgb,
        };
        return new ImageSource(width, height, space, samples, isJpeg: false, palette: space == ImageColorSpace.Indexed ? palette : null, mask: alpha);
    }

    private static void Pixel(
        byte[] row,
        int column,
        int depth,
        int maxValue,
        int colorType,
        byte[]? palette,
        byte[]? paletteAlpha,
        int[]? colorKey,
        byte[] samples,
        byte[]? alpha,
        int pixel,
        int colorBytes)
    {
        switch (colorType)
        {
            case 0:
            {
                int gray = Sample(row, column, depth);
                samples[pixel] = To8(gray, depth, maxValue);
                if (alpha is not null)
                {
                    alpha[pixel] = gray == colorKey![0] ? (byte)0 : (byte)255;
                }

                break;
            }

            case 2:
            {
                int r = Sample(row, column * 3, depth);
                int g = Sample(row, (column * 3) + 1, depth);
                int b = Sample(row, (column * 3) + 2, depth);
                int at = pixel * 3;
                samples[at] = To8(r, depth, maxValue);
                samples[at + 1] = To8(g, depth, maxValue);
                samples[at + 2] = To8(b, depth, maxValue);
                if (alpha is not null)
                {
                    alpha[pixel] = r == colorKey![0] && g == colorKey[1] && b == colorKey[2] ? (byte)0 : (byte)255;
                }

                break;
            }

            case 3:
            {
                int index = Sample(row, column, depth);
                if (index >= palette!.Length / 3)
                {
                    throw new ImageFormatException("The PNG has a pixel whose palette index is beyond the palette.");
                }

                if (paletteAlpha is null)
                {
                    samples[pixel] = (byte)index;
                }
                else
                {
                    int at = pixel * 3;
                    samples[at] = palette[index * 3];
                    samples[at + 1] = palette[(index * 3) + 1];
                    samples[at + 2] = palette[(index * 3) + 2];
                    alpha![pixel] = index < paletteAlpha.Length ? paletteAlpha[index] : (byte)255;
                }

                break;
            }

            case 4:
                samples[pixel] = To8(Sample(row, column * 2, depth), depth, maxValue);
                alpha![pixel] = To8(Sample(row, (column * 2) + 1, depth), depth, maxValue);
                break;

            default:
            {
                int at = pixel * 3;
                samples[at] = To8(Sample(row, column * 4, depth), depth, maxValue);
                samples[at + 1] = To8(Sample(row, (column * 4) + 1, depth), depth, maxValue);
                samples[at + 2] = To8(Sample(row, (column * 4) + 2, depth), depth, maxValue);
                alpha![pixel] = To8(Sample(row, (column * 4) + 3, depth), depth, maxValue);
                break;
            }
        }
    }

    /// <summary>The sample with this index in a row (samples are packed from the high bits of each byte, and 16-bit samples are big endian).</summary>
    private static int Sample(byte[] row, int index, int depth)
    {
        switch (depth)
        {
            case 8:
                return row[index];
            case 16:
                return (row[index * 2] << 8) | row[(index * 2) + 1];
            default:
                int bit = index * depth;
                int shift = 8 - depth - (bit & 7);
                return (row[bit >> 3] >> shift) & ((1 << depth) - 1);
        }
    }

    /// <summary>A sample as a byte: a sample of fewer bits is stretched to the full range, a 16-bit one is rounded to the nearest byte.</summary>
    private static byte To8(int value, int depth, int maxValue) => depth switch
    {
        8 => (byte)value,
        16 => (byte)(((value * 255) + 32767) / 65535),
        _ => (byte)(value * (255 / maxValue)),
    };

    private static void ReadLine(Stream inflater, byte[] line)
    {
        int read = 0;
        while (read < line.Length)
        {
            int n = inflater.Read(line, read, line.Length - read);
            if (n == 0)
            {
                throw new ImageFormatException("The PNG is cut short: its image data ends before the last row.");
            }

            read += n;
        }
    }

    /// <summary>Undoes a row's filter in place; <paramref name="previous"/> is the row above, already unfiltered (zeros above the first row).</summary>
    private static void Unfilter(byte filter, byte[] row, byte[] previous, int bytesPerPixel)
    {
        switch (filter)
        {
            case 0:
                break;
            case 1:
                for (int i = bytesPerPixel; i < row.Length; i++)
                {
                    row[i] = (byte)(row[i] + row[i - bytesPerPixel]);
                }

                break;
            case 2:
                for (int i = 0; i < row.Length; i++)
                {
                    row[i] = (byte)(row[i] + previous[i]);
                }

                break;
            case 3:
                for (int i = 0; i < row.Length; i++)
                {
                    int left = i >= bytesPerPixel ? row[i - bytesPerPixel] : 0;
                    row[i] = (byte)(row[i] + ((left + previous[i]) / 2));
                }

                break;
            case 4:
                for (int i = 0; i < row.Length; i++)
                {
                    int a = i >= bytesPerPixel ? row[i - bytesPerPixel] : 0;
                    int b = previous[i];
                    int c = i >= bytesPerPixel ? previous[i - bytesPerPixel] : 0;
                    int p = a + b - c;
                    int pa = Math.Abs(p - a);
                    int pb = Math.Abs(p - b);
                    int pc = Math.Abs(p - c);
                    int predictor = pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
                    row[i] = (byte)(row[i] + predictor);
                }

                break;
            default:
                throw new ImageFormatException($"The PNG has a row with the filter type {filter}, which the format does not define.");
        }
    }

    private static void RequireLength(byte[] chunk, int length)
    {
        if (chunk.Length != length)
        {
            throw new ImageFormatException("The PNG's tRNS chunk has the wrong length for its colour type.");
        }
    }

    private static string Name(ReadOnlySpan<byte> type)
    {
        char[] letters = new char[type.Length];
        for (int i = 0; i < type.Length; i++)
        {
            letters[i] = type[i] is >= (byte)' ' and <= (byte)'~' ? (char)type[i] : '?';
        }

        return new string(letters);
    }

    private static uint Crc(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in type)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        foreach (byte b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFF;
    }

    private static uint[] MakeCrcTable()
    {
        uint[] table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }

    private sealed record Header(int Width, int Height, int Depth, int ColorType, bool Interlaced);
}
