using Pdfwright.Images;

namespace Pdfwright;

/// <summary>
/// A JPEG or PNG picture, read once and checked: its size is known, and it can be placed in any number of documents (it is written once to each document that uses it, however often it is drawn).
/// A JPEG is passed through to the file as it is (progressive and baseline, gray, RGB or CMYK); a PNG is decoded, and its transparency is kept. Use it with <see cref="Image"/> or
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

    /// <summary>Whether the picture has transparency (a PNG with an alpha channel or a <c>tRNS</c> chunk).</summary>
    public bool HasTransparency => Source.Mask is not null;

    internal ImageSource Source { get; }

    /// <summary>
    /// Reads a JPEG or a PNG, by what its first bytes say it is. The data is checked completely now (every PNG chunk's checksum, the structure of a JPEG file), so a bad picture fails here and not when
    /// the document is saved. A picture is limited to 65,535 pixels on a side and 100 million pixels in all; the colour profile of either format is ignored.
    /// </summary>
    /// <param name="bytes">The bytes of a JPEG or PNG file.</param>
    /// <returns>The picture.</returns>
    /// <exception cref="ArgumentException">The data is neither a JPEG nor a PNG, is damaged or cut short, uses a feature that is not supported (arithmetic coding, 12-bit samples, an unknown critical PNG chunk), or is larger than the limits allow; the reason is the message and the inner exception.</exception>
    public static PdfImage FromBytes(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return new PdfImage(ImageSource.FromBytes(bytes));
        }
        catch (ImageFormatException error)
        {
            throw new ArgumentException("The data is not a usable JPEG or PNG: " + error.Message, nameof(bytes), error);
        }
    }
}
