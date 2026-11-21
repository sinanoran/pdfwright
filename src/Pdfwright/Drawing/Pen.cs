namespace Pdfwright.Graphics;

/// <summary>What a stroke is drawn with: a colour, a width, the shapes of its ends and corners, a dash pattern and an opacity.</summary>
internal sealed class Pen
{
    private readonly double[] _dashes;

    /// <param name="color">The colour of the line.</param>
    /// <param name="width">The line width in the canvas's units; zero draws the thinnest line the device can.</param>
    /// <param name="cap">The shape of the ends.</param>
    /// <param name="join">The shape of the corners.</param>
    /// <param name="miterLimit">Where a mitred corner is cut to a bevel, as a ratio of miter length to line width; at least 1.</param>
    /// <param name="dashes">The lengths of the dashes and the gaps in turn, or <see langword="null"/> or empty for a solid line.</param>
    /// <param name="dashPhase">How far into the pattern the line starts.</param>
    /// <param name="opacity">From 0 to 1.</param>
    /// <exception cref="ArgumentException">A cap or a join is not one of the defined values, a number is not finite, a width, dash or phase is negative, the miter limit is below 1, or the dashes are all zero.</exception>
    internal Pen(
        Color color,
        double width = 1,
        LineCap cap = LineCap.Butt,
        LineJoin join = LineJoin.Miter,
        double miterLimit = 10,
        IEnumerable<double>? dashes = null,
        double dashPhase = 0,
        double opacity = 1)
    {
        if (!Enum.IsDefined(cap))
        {
            throw new ArgumentOutOfRangeException(nameof(cap), cap, "A line cap is Butt, Round or Square.");
        }

        if (!Enum.IsDefined(join))
        {
            throw new ArgumentOutOfRangeException(nameof(join), join, "A line join is Miter, Round or Bevel.");
        }

        if (!(double.IsFinite(width) && width >= 0))
        {
            throw new ArgumentException($"A line width is a finite number of zero or more, not {width}.", nameof(width));
        }

        if (!(double.IsFinite(miterLimit) && miterLimit >= 1))
        {
            throw new ArgumentException($"A miter limit is a finite number of 1 or more, not {miterLimit}.", nameof(miterLimit));
        }

        if (!(double.IsFinite(dashPhase) && dashPhase >= 0))
        {
            throw new ArgumentException($"A dash phase is a finite number of zero or more, not {dashPhase}.", nameof(dashPhase));
        }

        if (!(opacity is >= 0 and <= 1))
        {
            throw new ArgumentException($"Opacity is a number from 0 to 1, not {opacity}.", nameof(opacity));
        }

        double[] pattern = dashes is null ? [] : [.. dashes];
        if (pattern.Any(d => !(double.IsFinite(d) && d >= 0)))
        {
            throw new ArgumentException("Every dash and gap is a finite number of zero or more.", nameof(dashes));
        }

        if (pattern.Length > 0 && pattern.All(d => d == 0))
        {
            throw new ArgumentException("A dash pattern cannot be all zeros.", nameof(dashes));
        }

        Color = color;
        Width = width;
        Cap = cap;
        Join = join;
        MiterLimit = miterLimit;
        _dashes = pattern;
        DashPhase = pattern.Length == 0 ? 0 : dashPhase;
        Opacity = opacity;
    }

    internal Color Color { get; }

    internal double Width { get; }

    internal LineCap Cap { get; }

    internal LineJoin Join { get; }

    internal double MiterLimit { get; }

    /// <summary>The dashes and gaps in turn; empty for a solid line.</summary>
    internal IReadOnlyList<double> Dashes => _dashes;

    internal double DashPhase { get; }

    /// <summary>From 0 (nothing) to 1 (opaque).</summary>
    internal double Opacity { get; }
}
