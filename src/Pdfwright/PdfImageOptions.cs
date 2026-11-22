namespace Pdfwright;

/// <summary>What <see cref="PdfImage.FromBytes(ReadOnlySpan{byte}, PdfImageOptions)"/> reads a picture with. A value that cannot be used is refused when it is set, naming the property.</summary>
public sealed class PdfImageOptions
{
    private readonly long _maxPixels = Images.ImageSource.DefaultMaxPixels;

    /// <summary>
    /// The most pixels (width times height) a picture may have, JPEG, PNG or WebP: 40,000,000 by default. A PNG or a WebP is decoded whole, to up to 4 bytes a pixel (3 for colour and 1 for transparency),
    /// so the default allows about 160 MB, and 100,000,000 about 400 MB (a lossless WebP needs about twice that while it is decoded); a JPEG is passed through to the file as it is, but a reader of the PDF decodes it to a bitmap of that size, so
    /// the limit applies to it too. The check is made on the size in the header, before any memory is allocated for the picture. The side of a picture is limited to 65,535 pixels whatever this is.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is less than 1 or more than 100,000,000, the most that is allowed (a decoded PNG is held in arrays of 32-bit size, and 100 million pixels is well inside them).</exception>
    public long MaxPixels
    {
        get => _maxPixels;
        init => _maxPixels = value is < 1 or > Images.ImageSource.MaxPixelsCap
            ? throw new ArgumentOutOfRangeException(nameof(MaxPixels), value, string.Create(System.Globalization.CultureInfo.InvariantCulture, $"The pixel limit is between 1 and {Images.ImageSource.MaxPixelsCap:N0}."))
            : value;
    }
}
