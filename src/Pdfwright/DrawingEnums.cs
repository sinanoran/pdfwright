namespace Pdfwright;

/// <summary>How the inside of a closed shape is decided: by winding number, or by counting how many edges a ray crosses.</summary>
public enum FillRule
{
    /// <summary>Non-zero winding (<c>f</c>, <c>W</c>).</summary>
    NonZero,

    /// <summary>Even-odd (<c>f*</c>, <c>W*</c>): a shape inside a shape of the same path is a hole.</summary>
    EvenOdd,
}

/// <summary>How the end of an open line is drawn.</summary>
public enum LineCap
{
    /// <summary>The line ends square at its last point.</summary>
    Butt = 0,

    /// <summary>The line ends in a half circle.</summary>
    Round = 1,

    /// <summary>The line goes on for half its width past the last point.</summary>
    Square = 2,
}

/// <summary>How two lines of a path meet.</summary>
public enum LineJoin
{
    /// <summary>A sharp corner (cut to a bevel beyond the miter limit).</summary>
    Miter = 0,

    /// <summary>A rounded corner.</summary>
    Round = 1,

    /// <summary>A flat cut corner.</summary>
    Bevel = 2,
}
