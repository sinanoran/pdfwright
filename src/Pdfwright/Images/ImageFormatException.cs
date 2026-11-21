namespace Pdfwright.Images;

/// <summary>An image that cannot be used: it is not a JPEG or a PNG this library reads, it is damaged or cut short, or it is larger than the limits allow.</summary>
internal sealed class ImageFormatException : Exception
{
    internal ImageFormatException(string message)
        : base(message)
    {
    }

    internal ImageFormatException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
