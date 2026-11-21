namespace Pdfwright.Drawing;

/// <summary>The kinds of segment a <see cref="Path"/> holds, each one operator of a content stream.</summary>
internal enum PathSegmentKind
{
    MoveTo,
    LineTo,
    CubicTo,
    Rectangle,
    Close,
}

/// <summary>
/// One segment. A cubic uses all three points (two control points and the end); a move and a line use the first; a rectangle holds its
/// corner in the first point and its width and height in the second.
/// </summary>
internal readonly record struct PathSegment(PathSegmentKind Kind, Point P1, Point P2, Point P3);

/// <summary>A shape made of lines and curves, in the canvas's own coordinates (top left origin, y down). It never changes once built; see <see cref="PathBuilder"/>.</summary>
internal sealed class Path
{
    internal Path(IReadOnlyList<PathSegment> segments) => Segments = segments;

    internal static Path Empty { get; } = new([]);

    internal IReadOnlyList<PathSegment> Segments { get; }

    /// <summary>The path draws nothing: it has no segment but moves and closes.</summary>
    internal bool IsEmpty => Segments.All(s => s.Kind is PathSegmentKind.MoveTo or PathSegmentKind.Close);
}
