using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Pdfwright.Tests.Images;

/// <summary>Makes PNG and JPEG files from code, so that the tests need no encoder and no image from anywhere.</summary>
internal static class ImageTestKit
{
    private static readonly (int X, int Y, int StepX, int StepY)[] Adam7 =
    [
        (0, 0, 8, 8), (4, 0, 8, 8), (0, 4, 4, 8), (2, 0, 4, 4), (0, 2, 2, 4), (1, 0, 2, 2), (0, 1, 1, 2),
    ];

    internal static int Channels(int colorType) => colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, _ => 4 };

    internal static int MaxSample(int depth) => (1 << depth) - 1;

    /// <summary>
    /// The sample of channel <paramref name="channel"/> of pixel (x, y) in the test pictures: a pattern that differs between neighbours and between
    /// channels, and fills the range of the bit depth. A 16-bit sample has a low byte that differs from its high byte.
    /// </summary>
    internal static int Sample(int x, int y, int channel, int depth, int colorType)
    {
        int seed = (x * 37) + (y * 91) + (channel * 53) + 11;
        if (colorType == 3)
        {
            return seed % (MaxSample(depth) + 1);
        }

        return depth switch
        {
            16 => ((seed & 0xFF) << 8) | ((seed * 3 + 5) & 0xFF),
            8 => seed & 0xFF,
            _ => (seed >> 1) & MaxSample(depth),
        };
    }

    /// <summary>A sample as the byte a PDF image holds (what the reader is meant to produce).</summary>
    internal static byte To8(int value, int depth) => depth switch
    {
        8 => (byte)value,
        16 => (byte)Math.Round(value * 255.0 / 65535.0, MidpointRounding.AwayFromZero),
        _ => (byte)(value * 255 / MaxSample(depth)),
    };

    internal static byte[] Png(
        int width,
        int height,
        int depth,
        int colorType,
        bool interlaced = false,
        byte[]? palette = null,
        byte[]? transparency = null,
        Func<int, int, int, int>? sample = null,
        int? filter = null,
        int idatSize = 61,
        Action<List<(string Type, byte[] Data)>>? changeChunks = null)
    {
        sample ??= (x, y, c) => Sample(x, y, c, depth, colorType);
        int channels = Channels(colorType);
        int bitsPerPixel = channels * depth;
        int bytesPerPixel = Math.Max(1, bitsPerPixel / 8);

        using MemoryStream raw = new();
        (int X, int Y, int StepX, int StepY)[] passes = interlaced ? Adam7 : [(0, 0, 1, 1)];
        int passNumber = 0;
        foreach ((int x0, int y0, int dx, int dy) in passes)
        {
            int pw = (width - x0 + dx - 1) / dx;
            int ph = (height - y0 + dy - 1) / dy;
            if (pw > 0 && ph > 0)
            {
                int stride = ((pw * bitsPerPixel) + 7) / 8;
                byte[] previous = new byte[stride];
                for (int row = 0; row < ph; row++)
                {
                    byte[] current = new byte[stride];
                    for (int column = 0; column < pw; column++)
                    {
                        for (int c = 0; c < channels; c++)
                        {
                            Put(current, (column * channels) + c, depth, sample(x0 + (column * dx), y0 + (row * dy), c));
                        }
                    }

                    int type = filter ?? ((row + passNumber) % 5);
                    raw.WriteByte((byte)type);
                    raw.Write(Filter(type, current, previous, bytesPerPixel));
                    previous = current;
                }
            }

            passNumber++;
        }

        byte[] idat = Deflate(raw.ToArray());
        List<(string Type, byte[] Data)> chunks = [];
        byte[] header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)height);
        header[8] = (byte)depth;
        header[9] = (byte)colorType;
        header[12] = (byte)(interlaced ? 1 : 0);
        chunks.Add(("IHDR", header));
        chunks.Add(("gAMA", [0, 1, 0x86, 0xA0]));
        if (palette is not null)
        {
            chunks.Add(("PLTE", palette));
        }

        if (transparency is not null)
        {
            chunks.Add(("tRNS", transparency));
        }

        chunks.Add(("tEXt", Encoding.ASCII.GetBytes("Comment\0ignored")));
        for (int at = 0; at < idat.Length; at += idatSize)
        {
            chunks.Add(("IDAT", idat[at..Math.Min(idat.Length, at + idatSize)]));
        }

        chunks.Add(("IEND", []));
        changeChunks?.Invoke(chunks);
        return Assemble(chunks);
    }

    internal static byte[] Assemble(IEnumerable<(string Type, byte[] Data)> chunks)
    {
        using MemoryStream file = new();
        file.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        foreach ((string type, byte[] data) in chunks)
        {
            byte[] length = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
            file.Write(length);
            byte[] name = Encoding.ASCII.GetBytes(type);
            file.Write(name);
            file.Write(data);
            byte[] crc = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(crc, Crc(name, data));
            file.Write(crc);
        }

        return file.ToArray();
    }

    /// <summary>Makes every chunk's CRC right again after a test changed bytes inside a file, so that the change reaches the decoder behind the CRC check.</summary>
    internal static void FixCrcs(byte[] png)
    {
        int position = 8;
        while (position + 12 <= png.Length)
        {
            uint length = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(position));
            if (length > png.Length || position + 12L + length > png.Length)
            {
                return;
            }

            uint crc = Crc(png.AsSpan(position + 4, 4), png.AsSpan(position + 8, (int)length));
            BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(position + 8 + (int)length), crc);
            position += 12 + (int)length;
        }
    }

    private static void Put(byte[] row, int index, int depth, int value)
    {
        switch (depth)
        {
            case 8:
                row[index] = (byte)value;
                break;
            case 16:
                row[index * 2] = (byte)(value >> 8);
                row[(index * 2) + 1] = (byte)value;
                break;
            default:
                int bit = index * depth;
                row[bit >> 3] |= (byte)(value << (8 - depth - (bit & 7)));
                break;
        }
    }

    private static byte[] Filter(int type, byte[] row, byte[] previous, int bytesPerPixel)
    {
        byte[] output = new byte[row.Length];
        for (int i = 0; i < row.Length; i++)
        {
            int a = i >= bytesPerPixel ? row[i - bytesPerPixel] : 0;
            int b = previous[i];
            int c = i >= bytesPerPixel ? previous[i - bytesPerPixel] : 0;
            int predictor = type switch
            {
                0 => 0,
                1 => a,
                2 => b,
                3 => (a + b) / 2,
                _ => Paeth(a, b, c),
            };
            output[i] = (byte)(row[i] - predictor);
        }

        return output;
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a);
        int pb = Math.Abs(p - b);
        int pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static byte[] Deflate(byte[] data)
    {
        using MemoryStream output = new();
        using (ZLibStream zlib = new(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(data);
        }

        return output.ToArray();
    }

    private static uint Crc(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        // The bitwise definition, written out so that the tests do not share a table with the code they check.
        uint crc = 0xFFFFFFFF;
        foreach (byte[] part in new[] { type.ToArray(), data.ToArray() })
        {
            foreach (byte b in part)
            {
                crc ^= b;
                for (int k = 0; k < 8; k++)
                {
                    crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
                }
            }
        }

        return ~crc;
    }

    // ---- JPEG skeletons: the markers of a file, with a scan that no decoder would accept but that the reader walks like a real one.

    internal static byte[] Segment(int marker, params byte[] payload)
    {
        byte[] segment = new byte[payload.Length + 4];
        segment[0] = 0xFF;
        segment[1] = (byte)marker;
        segment[2] = (byte)((payload.Length + 2) >> 8);
        segment[3] = (byte)(payload.Length + 2);
        payload.CopyTo(segment, 4);
        return segment;
    }

    internal static byte[] Frame(int marker, int width, int height, int components, int precision = 8)
    {
        List<byte> payload = [(byte)precision, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, (byte)components];
        for (int c = 0; c < components; c++)
        {
            payload.AddRange([(byte)(c + 1), 0x11, 0]);
        }

        return Segment(marker, [.. payload]);
    }

    internal static byte[] Adobe(int transform) => Segment(0xEE, [.. "Adobe"u8.ToArray(), 0, 100, 0, 0, 0, 0, (byte)transform]);

    internal static byte[] Jpeg(int marker, int width, int height, int components, bool adobe = false, int precision = 8, bool endOfImage = true, byte[]? scanData = null)
    {
        using MemoryStream file = new();
        file.Write([0xFF, 0xD8]);
        file.Write(Segment(0xE0, [.. "JFIF"u8.ToArray(), 0, 1, 1, 0, 0, 1, 0, 1, 0, 0]));
        if (adobe)
        {
            file.Write(Adobe(0));
        }

        file.Write(Segment(0xDB, [.. new byte[1], .. Enumerable.Repeat((byte)1, 64)]));
        file.Write(Frame(marker, width, height, components, precision));
        file.Write(Segment(0xC4, [0x00, .. new byte[16], 0]));
        List<byte> scan = [(byte)components];
        for (int c = 0; c < components; c++)
        {
            scan.AddRange([(byte)(c + 1), 0]);
        }

        scan.AddRange([0, 63, 0]);
        file.Write(Segment(0xDA, [.. scan]));

        // Entropy-coded data with a stuffed byte and a restart marker in it, which belong to the data and are not markers of the file.
        file.Write(scanData ?? [0x12, 0xFF, 0x00, 0x34, 0xFF, 0xD0, 0x56, 0xFF, 0xFF, 0x00, 0x78]);
        if (endOfImage)
        {
            file.Write([0xFF, 0xD9]);
        }

        return file.ToArray();
    }
}
