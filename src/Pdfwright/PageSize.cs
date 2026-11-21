namespace Pdfwright;

/// <summary>The size of a page: a width and a height, as a reader shows it (before any rotation).</summary>
public readonly struct PageSize : IEquatable<PageSize>
{
    /// <summary>A page of the given width and height.</summary>
    /// <param name="width">The width; a PDF page is 3 to 14,400 points on a side.</param>
    /// <param name="height">The height; a PDF page is 3 to 14,400 points on a side.</param>
    /// <exception cref="ArgumentOutOfRangeException">A side is not a finite length of 3 to 14,400 points.</exception>
    public PageSize(Length width, Length height)
    {
        if (!(width.Points is >= 3 and <= 14400))
        {
            throw new ArgumentOutOfRangeException(nameof(width), width, "A page is 3 to 14,400 points wide.");
        }

        if (!(height.Points is >= 3 and <= 14400))
        {
            throw new ArgumentOutOfRangeException(nameof(height), height, "A page is 3 to 14,400 points high.");
        }

        Width = width;
        Height = height;
    }

    /// <summary>ISO A4, 210 by 297 millimetres, upright.</summary>
    public static PageSize A4 => new(Length.Mm(210), Length.Mm(297));

    /// <summary>ISO A5, 148 by 210 millimetres, upright.</summary>
    public static PageSize A5 => new(Length.Mm(148), Length.Mm(210));

    /// <summary>US Letter, 8.5 by 11 inches, upright.</summary>
    public static PageSize Letter => new(Length.In(8.5), Length.In(11));

    /// <summary>US Legal, 8.5 by 14 inches, upright.</summary>
    public static PageSize Legal => new(Length.In(8.5), Length.In(14));

    /// <summary>The width of the page.</summary>
    public Length Width { get; }

    /// <summary>The height of the page.</summary>
    public Length Height { get; }

    /// <summary>The same page turned on its side: the wider side is the width. A page that is already wider than it is high is returned as it is.</summary>
    /// <returns>The landscape page.</returns>
    public PageSize Landscape() => Width >= Height ? this : new PageSize(Height, Width);

    /// <summary>Whether two sizes have the same width and height.</summary>
    /// <param name="left">The first size.</param>
    /// <param name="right">The second size.</param>
    /// <returns><see langword="true"/> when they do.</returns>
    public static bool operator ==(PageSize left, PageSize right) => left.Equals(right);

    /// <summary>Whether two sizes differ.</summary>
    /// <param name="left">The first size.</param>
    /// <param name="right">The second size.</param>
    /// <returns><see langword="true"/> when they do.</returns>
    public static bool operator !=(PageSize left, PageSize right) => !left.Equals(right);

    /// <inheritdoc />
    public bool Equals(PageSize other) => Width == other.Width && Height == other.Height;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is PageSize other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Width, Height);

    /// <inheritdoc />
    public override string ToString() => $"{Width} x {Height}";
}
