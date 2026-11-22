using System.Buffers.Binary;
using System.Text;
using Pdfwright.Images;

namespace Pdfwright.Tests.Images;

/// <summary>The WebP test pictures (<c>Images/Files/webp</c>, see its README for where they come from), and the means to build a WebP file by hand: a RIFF container, and a bit writer for a crafted VP8L stream.</summary>
internal static class WebpTestKit
{
    internal static readonly string Folder = Path.Combine(AppContext.BaseDirectory, "Images", "Files", "webp");

    internal static byte[] Read(string name) => File.ReadAllBytes(Path.Combine(Folder, name));

    /// <summary>The pictures that the tests decode: (name of the WebP, name of the PNG it must equal or be near).</summary>
    internal static readonly (string Webp, string Png)[] Lossless =
    [
        ("blue-purple-pink.lossless.webp", "blue-purple-pink.png"),
        ("yellow_rose.lossless.webp", "yellow_rose.png"),
        ("tux.lossless.webp", "tux.png"),
        ("gopher-doc.1bpp.lossless.webp", "gopher-doc.1bpp.png"),
        ("gopher-doc.2bpp.lossless.webp", "gopher-doc.2bpp.png"),
        ("gopher-doc.4bpp.lossless.webp", "gopher-doc.4bpp.png"),
        ("gopher-doc.8bpp.lossless.webp", "gopher-doc.8bpp.png"),
        ("gopher-doc.with-alpha.lossless.webp", "gopher-doc.with-alpha.png"),
        ("gopher-doc.skip-hgroup.lossless.webp", "gopher-doc.8bpp.png"),
    ];

    internal static readonly string[] Everything =
    [
        .. Lossless.Select(p => p.Webp),
        "blue-purple-pink.lossy.webp",
        "yellow_rose.lossy.webp",
        "yellow_rose.lossy-with-alpha.webp",
        "video-001.lossy.webp",
    ];

    /// <summary>The samples of a picture as red, green and blue bytes with an alpha byte for each pixel (255 where the picture has no mask), whatever colour space it is in.</summary>
    internal static (byte[] Rgb, byte[] Alpha) Rgba(ImageSource image)
    {
        int pixels = image.Width * image.Height;
        byte[] rgb = new byte[3 * pixels];
        ReadOnlySpan<byte> data = image.Data.Span;
        for (int i = 0; i < pixels; i++)
        {
            switch (image.ColorSpace)
            {
                case ImageColorSpace.Gray:
                    rgb[3 * i] = rgb[(3 * i) + 1] = rgb[(3 * i) + 2] = data[i];
                    break;
                case ImageColorSpace.Indexed:
                    for (int c = 0; c < 3; c++)
                    {
                        rgb[(3 * i) + c] = image.Palette![(3 * data[i]) + c];
                    }

                    break;
                default:
                    data.Slice(3 * i, 3).CopyTo(rgb.AsSpan(3 * i));
                    break;
            }
        }

        byte[] alpha = new byte[pixels];
        if (image.Mask is { } mask)
        {
            mask.Span.CopyTo(alpha);
        }
        else
        {
            Array.Fill(alpha, (byte)255);
        }

        return (rgb, alpha);
    }

    /// <summary>The payload of the first chunk of a WebP file that has this four-character name.</summary>
    internal static byte[] Chunk(byte[] file, string id)
    {
        int position = 12;
        while (position + 8 <= file.Length)
        {
            int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(position + 4));
            if (Encoding.ASCII.GetString(file, position, 4) == id)
            {
                return file[(position + 8)..(position + 8 + length)];
            }

            position += 8 + length + (length & 1);
        }

        throw new InvalidOperationException("no chunk " + id);
    }

    /// <summary>The width and height of the picture in the payload of a <c>VP8 </c> chunk.</summary>
    internal static (int Width, int Height) Vp8Size(byte[] vp8) => (((vp8[7] & 0x3f) << 8) | vp8[6], ((vp8[9] & 0x3f) << 8) | vp8[8]);

    /// <summary>A RIFF container with the chunks, padded to an even length as the format wants.</summary>
    internal static byte[] Riff(params (string Id, byte[] Data)[] chunks)
    {
        using MemoryStream body = new();
        body.Write("WEBP"u8);
        foreach ((string id, byte[] data) in chunks)
        {
            body.Write(Encoding.ASCII.GetBytes(id));
            byte[] length = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(length, (uint)data.Length);
            body.Write(length);
            body.Write(data);
            if ((data.Length & 1) != 0)
            {
                body.WriteByte(0);
            }
        }

        byte[] file = new byte[8 + body.Length];
        "RIFF"u8.CopyTo(file);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), (uint)body.Length);
        body.ToArray().CopyTo(file, 8);
        return file;
    }

    /// <summary>The payload of a <c>VP8X</c> chunk: the flags (0x10 alpha, 0x02 animation, 0x20 ICC profile, 0x08 EXIF, 0x04 XMP) and the canvas size.</summary>
    internal static byte[] Vp8x(int flags, long width, long height)
    {
        byte[] data = new byte[10];
        data[0] = (byte)flags;
        long w = width - 1;
        long h = height - 1;
        data[4] = (byte)w;
        data[5] = (byte)(w >> 8);
        data[6] = (byte)(w >> 16);
        data[7] = (byte)h;
        data[8] = (byte)(h >> 8);
        data[9] = (byte)(h >> 16);
        return data;
    }

    /// <summary>Writes bits the way VP8L reads them: the least significant bit of each value first, into the least significant end of each byte.</summary>
    internal sealed class BitWriter
    {
        private readonly List<byte> _bytes = [];
        private int _current;
        private int _count;

        internal void Write(uint value, int bits)
        {
            for (int i = 0; i < bits; i++)
            {
                _current |= (int)((value >> i) & 1) << _count;
                if (++_count == 8)
                {
                    _bytes.Add((byte)_current);
                    _current = 0;
                    _count = 0;
                }
            }
        }

        internal byte[] ToArray()
        {
            List<byte> all = [.. _bytes];
            if (_count > 0)
            {
                all.Add((byte)_current);
            }

            return [.. all];
        }
    }

    /// <summary>A Huffman tree of one symbol, the "simple" form: the symbol needs no bits when it is read.</summary>
    internal static void SimpleTree(BitWriter bits, uint symbol)
    {
        bits.Write(1, 1); // A simple code,
        bits.Write(0, 1); // of one symbol,
        bits.Write(1, 1); // that has 8 bits.
        bits.Write(symbol, 8);
    }

    /// <summary>A group of the five trees of a pixel (green, red, blue, alpha, distance), each of one symbol.</summary>
    internal static void SimpleGroup(BitWriter bits, uint green, uint red, uint blue, uint alpha)
    {
        SimpleTree(bits, green);
        SimpleTree(bits, red);
        SimpleTree(bits, blue);
        SimpleTree(bits, alpha);
        SimpleTree(bits, 0);
    }

    /// <summary>The header of a VP8L stream.</summary>
    internal static void Vp8lHeader(BitWriter bits, int width, int height)
    {
        bits.Write(0x2f, 8);
        bits.Write((uint)(width - 1), 14);
        bits.Write((uint)(height - 1), 14);
        bits.Write(1, 1);
        bits.Write(0, 3);
    }

    /// <summary>
    /// A VP8L picture whose Huffman image is one tile (so one pixel of red <paramref name="indexHigh"/> and green <paramref name="indexLow"/>, which together name group <c>indexHigh * 256 + indexLow</c>), with that many groups in the data,
    /// every one of one-symbol trees: the picture is one colour, (20, 10, 30, 255) in the last group, which is the only one a tile uses.
    /// </summary>
    internal static byte[] Vp8lNamingGroup(int width, int height, int indexHigh, int indexLow)
    {
        BitWriter bits = new();
        Vp8lHeader(bits, width, height);
        bits.Write(0, 1); // No transform.
        bits.Write(0, 1); // No colour cache.
        bits.Write(1, 1); // A Huffman image,
        bits.Write(0, 3); // with tiles of 4 by 4 (2 + 0 bits).
        bits.Write(0, 1); // The Huffman image has no colour cache,
        SimpleGroup(bits, (uint)indexLow, (uint)indexHigh, 0, 0); // and one group, whose only pixel is red = indexHigh, green = indexLow.
        int groups = (indexHigh * 256) + indexLow + 1;
        for (int g = 0; g < groups; g++)
        {
            SimpleGroup(bits, 10, 20, 30, 255);
        }

        return Riff(("VP8L", bits.ToArray()));
    }

    /// <summary>The coded alpha plane (a lossless stream without its header) in which every value is <paramref name="value"/>.</summary>
    internal static byte[] ConstantAlphaStream(byte value)
    {
        BitWriter bits = new();
        bits.Write(0, 1); // No transform.
        bits.Write(0, 1); // No colour cache.
        bits.Write(0, 1); // One group.
        SimpleGroup(bits, value, 0, 0, 255);
        return bits.ToArray();
    }
}
