namespace Pdfwright.Drawing;

/// <summary>How a closed shape's inside is decided: by winding number, or by counting how many edges a ray crosses.</summary>
internal enum FillRule
{
    /// <summary>Non-zero winding (<c>f</c>, <c>W</c>).</summary>
    NonZero,

    /// <summary>Even-odd (<c>f*</c>, <c>W*</c>).</summary>
    EvenOdd,
}

/// <summary>What a fill is painted with: a colour, an opacity and the rule that decides what is inside.</summary>
internal sealed class Brush
{
    internal Brush(Color color, double opacity = 1, FillRule rule = FillRule.NonZero)
    {
        if (!(opacity is >= 0 and <= 1))
        {
            throw new ArgumentException($"Opacity is a number from 0 to 1, not {opacity}.", nameof(opacity));
        }

        Color = color;
        Opacity = opacity;
        Rule = rule;
    }

    internal Color Color { get; }

    /// <summary>From 0 (nothing) to 1 (opaque).</summary>
    internal double Opacity { get; }

    internal FillRule Rule { get; }
}
