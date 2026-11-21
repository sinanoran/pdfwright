using Pdfwright.Pdf;

namespace Pdfwright.Images;

/// <summary>The device colour space the samples of an image are in.</summary>
internal enum ImageColorSpace
{
    Gray,
    Rgb,
    Cmyk,

    /// <summary>One byte for each pixel, an index into <see cref="ImageSource.Palette"/>.</summary>
    Indexed,
}

/// <summary>
/// An image that has been read once: its size, the samples as a PDF wants them and, for a PNG that has transparency, a soft mask. It is
/// written to a document at most once, however often it is drawn (see <see cref="WriteTo"/>).
/// </summary>
internal sealed class ImageSource
{
    /// <summary>The most pixels an image may have (width times height), and so the most memory a decoder asks for.</summary>
    internal const long MaxPixels = 100_000_000;

    /// <summary>The most pixels an image may have along one side (what a 16-bit size in a header can hold).</summary>
    internal const int MaxSide = 65_535;

    private readonly byte[] _data;
    private readonly byte[]? _mask;

    internal ImageSource(
        int width,
        int height,
        ImageColorSpace colorSpace,
        byte[] data,
        bool isJpeg,
        byte[]? palette = null,
        double[]? decode = null,
        byte[]? mask = null)
    {
        Width = width;
        Height = height;
        ColorSpace = colorSpace;
        _data = data;
        IsJpeg = isJpeg;
        Palette = palette;
        Decode = decode;
        _mask = mask;
    }

    internal int Width { get; }

    internal int Height { get; }

    internal ImageColorSpace ColorSpace { get; }

    /// <summary>The samples as the image stream holds them: the JPEG file as it is, or one byte for each sample (rows one after another, no padding).</summary>
    internal ReadOnlyMemory<byte> Data => _data;

    /// <summary>The data is a JPEG file, written as it is with <c>DCTDecode</c>.</summary>
    internal bool IsJpeg { get; }

    /// <summary>The colours of an indexed image, three bytes (red, green, blue) for each entry.</summary>
    internal byte[]? Palette { get; }

    /// <summary>The <c>/Decode</c> array, or <see langword="null"/> for the default.</summary>
    internal double[]? Decode { get; }

    /// <summary>One byte of opacity for each pixel (0 is transparent), or <see langword="null"/> when the image is opaque.</summary>
    internal ReadOnlyMemory<byte>? Mask => _mask is null ? default(ReadOnlyMemory<byte>?) : new ReadOnlyMemory<byte>(_mask);

    /// <summary>Reads a JPEG or a PNG, by what its first bytes say it is.</summary>
    /// <exception cref="ImageFormatException">It is neither, or it is damaged, cut short or too large.</exception>
    internal static ImageSource FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (PngDecoder.HasSignature(bytes))
        {
            return PngDecoder.Decode(bytes);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xD8)
        {
            return JpegReader.Read(bytes);
        }

        throw new ImageFormatException("The data is neither a PNG nor a JPEG.");
    }

    /// <summary>Reads a JPEG.</summary>
    internal static ImageSource FromJpeg(ReadOnlySpan<byte> bytes) => JpegReader.Read(bytes);

    /// <summary>Reads a PNG.</summary>
    internal static ImageSource FromPng(ReadOnlySpan<byte> bytes) => PngDecoder.Decode(bytes);

    /// <summary>
    /// The image XObject of this image in a document, written the first time it is asked for and found again after that: a picture
    /// drawn a hundred times is in the file once. A PNG's samples carry no filter of their own, so the document's compression applies to them (Flate, unless it was switched off).
    /// </summary>
    internal PdfReference WriteTo(PdfDocumentWriter document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document.GetOrAddShared(this, () => Write(document));
    }

    private PdfReference Write(PdfDocumentWriter document)
    {
        PdfDictionary dictionary = new PdfDictionary()
            .Set("Type", "XObject")
            .Set("Subtype", "Image")
            .Set("Width", Width)
            .Set("Height", Height);

        dictionary.Set("ColorSpace", ColorSpace switch
        {
            ImageColorSpace.Gray => new PdfName("DeviceGray"),
            ImageColorSpace.Rgb => new PdfName("DeviceRGB"),
            ImageColorSpace.Cmyk => new PdfName("DeviceCMYK"),
            _ => new PdfArray(
                new PdfName("Indexed"),
                new PdfName("DeviceRGB"),
                new PdfInteger((Palette!.Length / 3) - 1),
                new PdfString(Palette, hex: true)),
        });
        dictionary.Set("BitsPerComponent", 8);
        if (Decode is not null)
        {
            dictionary.Set("Decode", PdfArray.Of(Decode));
        }

        if (IsJpeg)
        {
            dictionary.Set("Filter", "DCTDecode");
        }

        if (_mask is not null)
        {
            PdfDictionary soft = new PdfDictionary()
                .Set("Type", "XObject")
                .Set("Subtype", "Image")
                .Set("Width", Width)
                .Set("Height", Height)
                .Set("ColorSpace", "DeviceGray")
                .Set("BitsPerComponent", 8);
            dictionary.Set("SMask", document.AddObject(new PdfStream(soft, _mask)));
        }

        return document.AddObject(new PdfStream(dictionary, _data));
    }
}
