using Pdfwright.Images;

namespace Pdfwright;

/// <summary>
/// A JPEG, PNG or WebP picture, read once and checked: its size is known, and it can be placed in any number of documents (it is written once to each document that uses it, however often it is drawn).
/// A JPEG is passed through to the file as it is (progressive and baseline, gray, RGB or CMYK); a PNG or a WebP is decoded, and its transparency is kept. Use it with <see cref="Image"/> or
/// <see cref="DrawingCanvas.DrawImage"/>. An instance is immutable and may be shared between threads and documents.
/// </summary>
public sealed class PdfImage
{
    private PdfImage(ImageSource source)
    {
        Source = source;
    }

    /// <summary>The width of the picture in pixels.</summary>
    public int PixelWidth => Source.Width;

    /// <summary>The height of the picture in pixels.</summary>
    public int PixelHeight => Source.Height;

    /// <summary>Whether the picture has transparency (a PNG with an alpha channel or a <c>tRNS</c> chunk, a WebP with an alpha plane that is not fully opaque).</summary>
    public bool HasTransparency => Source.Mask is not null;

    internal ImageSource Source { get; }

    /// <summary>
    /// Reads a JPEG, a PNG or a WebP, by what its first bytes say it is. The data is checked completely now (every PNG chunk's checksum, the structure of a JPEG file, every bit of a WebP), so a bad picture fails here and not when
    /// the document is saved. A picture is limited to 65,535 pixels on a side and to 40 million pixels in all (a PNG or WebP with transparency is decoded to about 160 MB at that size, more while it is decoded), a JPEG as well as the others;
    /// <see cref="FromBytes(ReadOnlySpan{byte}, PdfImageOptions)"/> changes the pixel limit. The colour profile of every format is ignored. A WebP may be lossy (VP8), lossless (VP8L) or extended (VP8X) with an alpha plane; an animated WebP is refused.
    /// </summary>
    /// <param name="bytes">The bytes of a JPEG, PNG or WebP file.</param>
    /// <returns>The picture.</returns>
    /// <exception cref="ArgumentException">The data is none of a JPEG, a PNG and a WebP, is damaged or cut short, uses a feature that is not supported (arithmetic coding, 12-bit samples, an unknown critical PNG chunk, an animated WebP), or is larger than the limits allow; the reason is in the message.</exception>
    public static PdfImage FromBytes(ReadOnlySpan<byte> bytes) => Read(bytes, ImageSource.DefaultMaxPixels);

    /// <summary>
    /// Reads a JPEG, a PNG or a WebP as <see cref="FromBytes(ReadOnlySpan{byte})"/> does, with the pixel limit of <paramref name="options"/> in place of the default of 40 million.
    /// </summary>
    /// <param name="bytes">The bytes of a JPEG, PNG or WebP file.</param>
    /// <param name="options">The limits to read with.</param>
    /// <returns>The picture.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">As for <see cref="FromBytes(ReadOnlySpan{byte})"/>; a picture of more pixels than <see cref="PdfImageOptions.MaxPixels"/> is refused with a message that names its size and the limit, before any memory is allocated for it.</exception>
    public static PdfImage FromBytes(ReadOnlySpan<byte> bytes, PdfImageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Read(bytes, options.MaxPixels);
    }

    private static PdfImage Read(ReadOnlySpan<byte> bytes, long maxPixels)
    {
        try
        {
            return new PdfImage(ImageSource.FromBytes(bytes, maxPixels));
        }
        catch (ImageFormatException error)
        {
            throw new ArgumentException(error.LimitExceeded ? error.Message : "The data is not a usable JPEG or PNG (or WebP) picture: " + error.Message, nameof(bytes));
        }
    }
}
