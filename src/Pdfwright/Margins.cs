namespace Pdfwright;

/// <summary>
/// The space between the edge of the page and where content may go. Margins are physical (top, right, bottom, left), because they describe
/// the sheet, not the text; the text inside them is laid out in logical directions.
/// </summary>
public readonly struct Margins : IEquatable<Margins>
{
    /// <summary>Margins of the given lengths.</summary>
    /// <param name="top">The top margin.</param>
    /// <param name="right">The right margin.</param>
    /// <param name="bottom">The bottom margin.</param>
    /// <param name="left">The left margin.</param>
    public Margins(Length top, Length right, Length bottom, Length left)
    {
        Top = top;
        Right = right;
        Bottom = bottom;
        Left = left;
    }

    /// <summary>The top margin.</summary>
    public Length Top { get; }

    /// <summary>The right margin.</summary>
    public Length Right { get; }

    /// <summary>The bottom margin.</summary>
    public Length Bottom { get; }

    /// <summary>The left margin.</summary>
    public Length Left { get; }

    /// <summary>Margins in millimetres.</summary>
    /// <param name="top">The top margin in millimetres.</param>
    /// <param name="right">The right margin in millimetres.</param>
    /// <param name="bottom">The bottom margin in millimetres.</param>
    /// <param name="left">The left margin in millimetres.</param>
    /// <returns>The margins.</returns>
    public static Margins Mm(double top, double right, double bottom, double left) => new(Length.Mm(top), Length.Mm(right), Length.Mm(bottom), Length.Mm(left));

    /// <summary>Margins in points.</summary>
    /// <param name="top">The top margin in points.</param>
    /// <param name="right">The right margin in points.</param>
    /// <param name="bottom">The bottom margin in points.</param>
    /// <param name="left">The left margin in points.</param>
    /// <returns>The margins.</returns>
    public static Margins Pt(double top, double right, double bottom, double left) => new(Length.Pt(top), Length.Pt(right), Length.Pt(bottom), Length.Pt(left));

    /// <summary>The same margin on all four sides.</summary>
    /// <param name="all">The length of every margin.</param>
    /// <returns>The margins.</returns>
    public static Margins Uniform(Length all) => new(all, all, all, all);

    /// <summary>Whether two sets of margins are equal on every side.</summary>
    /// <param name="left">The first margins.</param>
    /// <param name="right">The second margins.</param>
    /// <returns><see langword="true"/> when they are.</returns>
    public static bool operator ==(Margins left, Margins right) => left.Equals(right);

    /// <summary>Whether two sets of margins differ on some side.</summary>
    /// <param name="left">The first margins.</param>
    /// <param name="right">The second margins.</param>
    /// <returns><see langword="true"/> when they do.</returns>
    public static bool operator !=(Margins left, Margins right) => !left.Equals(right);

    /// <inheritdoc />
    public bool Equals(Margins other) => Top == other.Top && Right == other.Right && Bottom == other.Bottom && Left == other.Left;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is Margins other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Top, Right, Bottom, Left);

    /// <inheritdoc />
    public override string ToString() => $"top {Top}, right {Right}, bottom {Bottom}, left {Left}";
}
