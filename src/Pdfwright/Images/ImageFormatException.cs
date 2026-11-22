namespace Pdfwright.Images;

/// <summary>An image that cannot be used: it is not a JPEG, a PNG or a WebP this library reads, it is damaged or cut short, or it is larger than the limits allow.</summary>
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

    /// <summary>Makes the exception of an image that has more pixels than the limit allows.</summary>
    internal ImageFormatException(string message, bool limitExceeded)
        : base(message)
    {
        LimitExceeded = limitExceeded;
    }

    /// <summary>The image is too large for the pixel limit (its format is fine), and the message says how to raise the limit.</summary>
    internal bool LimitExceeded { get; }
}
