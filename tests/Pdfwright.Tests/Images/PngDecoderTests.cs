using System.Buffers.Binary;
using Pdfwright.Images;

namespace Pdfwright.Tests.Images;

/// <summary>The PNG reader: every colour type and bit depth, interlaced or not, with each of the five filters, against pixels computed from the same samples.</summary>
public class PngDecoderTests
{
    private static readonly (int Width, int Height)[] Sizes = [(13, 11), (1, 1), (3, 2), (9, 1)];

    public static TheoryData<int, int, bool, int, int> Pictures()
    {
        TheoryData<int, int, bool, int, int> data = [];
        (int Type, int[] Depths)[] allowed = [(0, [1, 2, 4, 8, 16]), (2, [8, 16]), (3, [1, 2, 4, 8]), (4, [8, 16]), (6, [8, 16])];
        foreach ((int type, int[] depths) in allowed)
        {
            foreach (int depth in depths)
            {
                foreach (bool interlaced in new[] { false, true })
                {
                    foreach ((int w, int h) in Sizes)
                    {
                        data.Add(type, depth, interlaced, w, h);
                    }
                }
            }
        }

        return data;
    }

    internal static byte[] PaletteFor(int depth)
    {
        int entries = 1 << depth;
        byte[] palette = new byte[entries * 3];
        for (int i = 0; i < entries; i++)
        {
            palette[i * 3] = (byte)((i * 3) % 256);
            palette[(i * 3) + 1] = (byte)(((i * 5) + 1) % 256);
            palette[(i * 3) + 2] = (byte)(((i * 7) + 2) % 256);
        }

        return palette;
    }

    /// <summary>What the reader must make of a picture made by <see cref="ImageTestKit.Png"/>, computed from the samples rather than from the file.</summary>
    internal static (ImageColorSpace Space, byte[] Samples, byte[]? Alpha, byte[]? Palette) Expected(int w, int h, int depth, int type, byte[]? palette, byte[]? transparency)
    {
        int channels = ImageTestKit.Channels(type);
        int[] key = [];
        if (transparency is not null && type == 0)
        {
            key = [BinaryPrimitives.ReadUInt16BigEndian(transparency) & ImageTestKit.MaxSample(depth)];
        }
        else if (transparency is not null && type == 2)
        {
            key = [.. Enumerable.Range(0, 3).Select(i => BinaryPrimitives.ReadUInt16BigEndian(transparency.AsSpan(i * 2)) & ImageTestKit.MaxSample(depth))];
        }

        bool paletteAlpha = type == 3 && transparency is not null && transparency.Any(a => a != 255);
        List<byte> samples = [];
        List<byte> alpha = [];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int[] s = [.. Enumerable.Range(0, channels).Select(c => ImageTestKit.Sample(x, y, c, depth, type))];
                switch (type)
                {
                    case 0:
                        samples.Add(ImageTestKit.To8(s[0], depth));
                        if (key.Length > 0)
                        {
                            alpha.Add(s[0] == key[0] ? (byte)0 : (byte)255);
                        }

                        break;
                    case 2:
                        samples.AddRange(s.Select(v => ImageTestKit.To8(v, depth)));
                        if (key.Length > 0)
                        {
                            alpha.Add(s[0] == key[0] && s[1] == key[1] && s[2] == key[2] ? (byte)0 : (byte)255);
                        }

                        break;
                    case 3 when paletteAlpha:
                        samples.AddRange(palette!.AsSpan(s[0] * 3, 3).ToArray());
                        alpha.Add(s[0] < transparency!.Length ? transparency[s[0]] : (byte)255);
                        break;
                    case 3:
                        samples.Add((byte)s[0]);
                        break;
                    case 4:
                        samples.Add(ImageTestKit.To8(s[0], depth));
                        alpha.Add(ImageTestKit.To8(s[1], depth));
                        break;
                    default:
                        samples.AddRange(s.Take(3).Select(v => ImageTestKit.To8(v, depth)));
                        alpha.Add(ImageTestKit.To8(s[3], depth));
                        break;
                }
            }
        }

        ImageColorSpace space = type switch
        {
            3 when !paletteAlpha => ImageColorSpace.Indexed,
            0 or 4 => ImageColorSpace.Gray,
            _ => ImageColorSpace.Rgb,
        };
        return (space, [.. samples], alpha.Count > 0 ? [.. alpha] : null, space == ImageColorSpace.Indexed ? palette : null);
    }

    internal static void AssertDecodes(byte[] png, int w, int h, int depth, int type, byte[]? palette, byte[]? transparency)
    {
        ImageSource image = ImageSource.FromBytes(png);
        (ImageColorSpace space, byte[] samples, byte[]? alpha, byte[]? expectedPalette) = Expected(w, h, depth, type, palette, transparency);
        Assert.Equal(w, image.Width);
        Assert.Equal(h, image.Height);
        Assert.False(image.IsJpeg);
        Assert.Equal(space, image.ColorSpace);
        Assert.Equal(samples, image.Data.ToArray());
        Assert.Equal(alpha, image.Mask?.ToArray());
        Assert.Equal(expectedPalette, image.Palette);
    }

    [Theory]
    [MemberData(nameof(Pictures))]
    public void Every_colour_type_and_bit_depth_decodes_to_the_pixels_it_holds(int type, int depth, bool interlaced, int w, int h)
    {
        byte[]? palette = type == 3 ? PaletteFor(depth) : null;
        byte[] png = ImageTestKit.Png(w, h, depth, type, interlaced, palette);
        AssertDecodes(png, w, h, depth, type, palette, null);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Each_of_the_five_filters_is_undone(int filter)
    {
        // Colour types 2 and 4 at both depths use the byte distance of 3, 6, 2 and 4 between a byte and the one it is predicted from.
        foreach ((int type, int depth) in new[] { (2, 8), (2, 16), (4, 8), (6, 16), (0, 8), (0, 4) })
        {
            byte[] png = ImageTestKit.Png(17, 9, depth, type, filter: filter);
            AssertDecodes(png, 17, 9, depth, type, null, null);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_gray_colour_key_makes_the_pixels_of_that_gray_transparent(bool interlaced)
    {
        foreach (int depth in new[] { 1, 2, 4, 8, 16 })
        {
            int key = ImageTestKit.Sample(1, 1, 0, depth, 0);
            byte[] transparency = [(byte)(key >> 8), (byte)key];
            byte[] png = ImageTestKit.Png(13, 11, depth, 0, interlaced, transparency: transparency);
            AssertDecodes(png, 13, 11, depth, 0, null, transparency);
            Assert.Contains((byte)0, ImageSource.FromBytes(png).Mask!.Value.ToArray());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_rgb_colour_key_makes_the_pixels_of_that_colour_transparent(bool interlaced)
    {
        foreach (int depth in new[] { 8, 16 })
        {
            byte[] transparency = new byte[6];
            for (int c = 0; c < 3; c++)
            {
                int value = ImageTestKit.Sample(2, 3, c, depth, 2);
                transparency[c * 2] = (byte)(value >> 8);
                transparency[(c * 2) + 1] = (byte)value;
            }

            byte[] png = ImageTestKit.Png(13, 11, depth, 2, interlaced, transparency: transparency);
            AssertDecodes(png, 13, 11, depth, 2, null, transparency);
            Assert.Contains((byte)0, ImageSource.FromBytes(png).Mask!.Value.ToArray());
        }
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(4, false)]
    [InlineData(8, true)]
    public void A_palette_with_transparency_is_expanded_to_rgb_with_a_mask(int depth, bool interlaced)
    {
        byte[] palette = PaletteFor(depth);
        byte[] transparency = depth == 1 ? [0, 128] : [0, 128, 255, 7];
        byte[] png = ImageTestKit.Png(13, 11, depth, 3, interlaced, palette, transparency);
        AssertDecodes(png, 13, 11, depth, 3, palette, transparency);
        ImageSource image = ImageSource.FromBytes(png);
        Assert.Equal(ImageColorSpace.Rgb, image.ColorSpace);
        Assert.NotNull(image.Mask);
    }

    [Fact]
    public void A_palette_whose_transparency_is_all_opaque_stays_indexed_with_no_mask()
    {
        byte[] palette = PaletteFor(4);
        byte[] png = ImageTestKit.Png(8, 8, 4, 3, palette: palette, transparency: [255, 255, 255]);
        ImageSource image = ImageSource.FromBytes(png);
        Assert.Equal(ImageColorSpace.Indexed, image.ColorSpace);
        Assert.Null(image.Mask);
        Assert.Equal(palette, image.Palette);
    }

    [Fact]
    public void Sixteen_bit_samples_are_rounded_to_the_nearest_byte()
    {
        // 0xFFFF is white, 0x8000 is 128 (127.5 rounds up), 0x0080 is 0 (0.5 of a step is less than half), and 0x00FF is 1 (0.99).
        byte[] png = ImageTestKit.Png(4, 1, 16, 0, sample: (x, _, _) => new[] { 0xFFFF, 0x8000, 0x0080, 0x00FF }[x]);
        Assert.Equal<byte>([255, 128, 0, 1], ImageSource.FromBytes(png).Data.ToArray());
    }

    [Fact]
    public void Chunks_other_than_the_ones_that_are_read_are_skipped_whatever_they_say()
    {
        byte[] png = ImageTestKit.Png(
            9,
            9,
            8,
            2,
            changeChunks: chunks =>
            {
                chunks.Insert(1, ("iCCP", [1, 2, 3]));
                chunks.Insert(1, ("sRGB", [0]));
                chunks.Insert(1, ("zzZz", [9, 9]));
                chunks.Insert(chunks.Count - 1, ("tIME", [0, 0, 0, 0, 0, 0, 0]));
            });
        AssertDecodes(png, 9, 9, 8, 2, null, null);
    }

    [Fact]
    public void An_image_data_stream_split_into_many_chunks_is_one_stream()
    {
        byte[] png = ImageTestKit.Png(30, 30, 8, 6, idatSize: 7);
        AssertDecodes(png, 30, 30, 8, 6, null, null);
    }

    [Fact]
    public void The_reader_does_not_keep_the_caller_s_bytes()
    {
        byte[] png = ImageTestKit.Png(5, 5, 8, 0);
        ImageSource image = ImageSource.FromPng(png);
        byte[] before = image.Data.ToArray();
        Array.Clear(png);
        Assert.Equal(before, image.Data.ToArray());
    }
}
