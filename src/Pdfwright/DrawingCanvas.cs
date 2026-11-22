using Pdfwright.Document;
using Pdfwright.Graphics;
using Pdfwright.Text;

namespace Pdfwright;

/// <summary>
/// What a <see cref="DrawingCanvas"/> needs from the document it draws on: the fonts, the document's base style and direction, and where missing glyphs and unknown families are reported.
/// </summary>
internal sealed class DrawingServices(FontCollection.Snapshot fonts, ResolvedStyle baseStyle, TextDirection direction, Action<int, string?> missingGlyph, Action<string> family)
{
    internal FontCollection.Snapshot Fonts { get; } = fonts;

    internal ResolvedStyle Base { get; } = baseStyle;

    internal TextDirection Direction { get; } = direction;

    internal Action<int, string?> MissingGlyph { get; } = missingGlyph;

    internal Action<string> Family { get; } = family;
}

/// <summary>
/// The surface a <see cref="Drawing"/> draws on, for charts and other figures. All coordinates, sizes and line widths are in <b>points</b> (1/72 inch) as <see cref="double"/> values, because chart code is
/// arithmetic and a unit type for each number would only be in its way; the origin is the <b>top left</b> corner of the drawing's box and y points <b>down</b>. The box is a clip: nothing is
/// drawn outside <see cref="Width"/> by <see cref="Height"/> (until a transform moves it: the clip is in the box's own coordinates).
/// </summary>
/// <remarks>
/// <para>
/// A canvas is valid only while the callback of its <see cref="Drawing"/> runs. When the callback returns, every <see cref="Save"/> that is still open is closed, and the canvas is finished: any further call, from a
/// stored reference or another thread, throws <see cref="ObjectDisposedException"/>. A canvas is not thread-safe. Text is drawn through the document's <see cref="FontCollection"/> with the same engine that sets paragraphs, so
/// a character no font has a glyph for is reported in <see cref="DocumentDiagnostics.MissingGlyphs"/> with the page it was drawn on. Colours are the document's: <see cref="Color"/> values, gray or RGB.
/// </para>
/// </remarks>
public sealed class DrawingCanvas
{
    private readonly Canvas _inner;
    private readonly DrawingServices _services;
    private bool _finished;

    internal DrawingCanvas(Canvas inner, double width, double height, DrawingServices services)
    {
        _inner = inner;
        _services = services;
        Width = width;
        Height = height;
    }

    /// <summary>The width of the drawing's box in points.</summary>
    public double Width { get; }

    /// <summary>The height of the drawing's box in points.</summary>
    public double Height { get; }

    /// <summary>Saves the graphics state: the transform, the clip and the colours and line style that were set (<c>q</c>).</summary>
    /// <exception cref="ObjectDisposedException">The drawing's callback has returned.</exception>
    public void Save()
    {
        Require();
        _inner.Save();
    }

    /// <summary>Goes back to the state of the last <see cref="Save"/> (<c>Q</c>).</summary>
    /// <exception cref="InvalidOperationException">No save is open.</exception>
    /// <exception cref="ObjectDisposedException">The drawing's callback has returned.</exception>
    public void Restore()
    {
        Require();
        _inner.Restore();
    }

    /// <summary>Moves the origin by a distance in the current units.</summary>
    /// <param name="dx">The distance to the right.</param>
    /// <param name="dy">The distance down.</param>
    /// <exception cref="ArgumentException">A number is not finite.</exception>
    /// <exception cref="ObjectDisposedException">The drawing's callback has returned.</exception>
    public void Translate(double dx, double dy)
    {
        Require();
        Finite(dx, nameof(dx));
        Finite(dy, nameof(dy));
        _inner.Translate(dx, dy);
    }

    /// <summary>Scales the axes from the origin. A scale of zero is refused.</summary>
    /// <param name="sx">The horizontal factor.</param>
    /// <param name="sy">The vertical factor.</param>
    /// <exception cref="ArgumentException">A number is not finite, or a factor is zero (the exception names the argument).</exception>
    /// <exception cref="ObjectDisposedException">The drawing's callback has returned.</exception>
    public void Scale(double sx, double sy)
    {
        Require();
        Finite(sx, nameof(sx));
        Finite(sy, nameof(sy));
        if (sx == 0 || sy == 0)
        {
            throw new ArgumentException("A scale of zero flattens the canvas and cannot be undone.", sx == 0 ? nameof(sx) : nameof(sy));
        }

        _inner.Scale(sx, sy);
    }

    /// <summary>Turns the axes about the origin by an angle in degrees, clockwise as seen on the canvas (y down).</summary>
    /// <param name="degrees">The angle.</param>
    /// <exception cref="ArgumentException">The angle is not finite.</exception>
    /// <exception cref="ObjectDisposedException">The drawing's callback has returned.</exception>
    public void Rotate(double degrees)
    {
        Require();
        Finite(degrees, nameof(degrees));
        _inner.Rotate(degrees);
    }

    /// <summary>Applies the transform <c>x' = a x + c y + e</c>, <c>y' = b x + d y + f</c> to everything drawn after it, before the transforms already in force.</summary>
    /// <param name="a">The a coefficient.</param>
    /// <param name="b">The b coefficient.</param>
    /// <param name="c">The c coefficient.</param>
    /// <param name="d">The d coefficient.</param>
    /// <param name="e">The horizontal translation.</param>
    /// <param name="f">The vertical translation.</param>
    /// <exception cref="ArgumentException">A number is not finite, or the transform cannot be undone (its determinant is zero).</exception>
    /// <exception cref="ObjectDisposedException">The drawing's callback has returned.</exception>
    public void Transform(double a, double b, double c, double d, double e, double f)
    {
        Require();
        Finite(a, nameof(a));
        Finite(b, nameof(b));
        Finite(c, nameof(c));
        Finite(d, nameof(d));
        Finite(e, nameof(e));
        Finite(f, nameof(f));
        double determinant = (a * d) - (b * c);
        if (!double.IsFinite(determinant) || determinant == 0)
        {
            throw new ArgumentException("A transform is invertible: its determinant (a d - b c) is neither zero nor beyond the range of a number.");
        }

        _inner.Transform(new Matrix(a, b, c, d, e, f));
    }

    /// <summary>Restricts everything drawn after it to the inside of a path, until the matching <see cref="Restore"/> (or the end of the drawing). An empty path clips everything away.</summary>
    /// <param name="path">The path.</param>
    /// <param name="rule">How the inside is decided.</param>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The rule is not a <see cref="FillRule"/>.</exception>
    /// <exception cref="ObjectDisposedException">The drawing's callback has returned.</exception>
    public void Clip(DrawingPath path, FillRule rule = FillRule.NonZero)
    {
        Require();
        ArgumentNullException.ThrowIfNull(path);
        _inner.Clip(path.Inner, Check.Defined(rule, nameof(rule)));
    }

    /// <summary>Paints the inside of a path. A path with nothing in it draws nothing.</summary>
    /// <param name="path">The path.</param>
    /// <param name="color">The colour.</param>
    /// <param name="opacity">From 0 (nothing) to 1 (opaque).</param>
    /// <param name="rule">How the inside is decided.</param>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="opacity"/> is not from 0 to 1.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The rule is not a <see cref="FillRule"/>.</exception>
    /// <exception cref="ObjectDisposedException">The drawing's callback has returned.</exception>
    public void Fill(DrawingPath path, Color color, double opacity = 1, FillRule rule = FillRule.NonZero)
    {
        Require();
        ArgumentNullException.ThrowIfNull(path);
        _inner.Fill(path.Inner, new Brush(color, Opacity(opacity), Check.Defined(rule, nameof(rule))));
    }

    /// <summary>Draws the outline of a path with a pen (its colour, width, dashes, caps, joins and opacity). A path with nothing in it draws nothing.</summary>
    /// <param name="path">The path.</param>
    /// <param name="pen">The pen.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The drawing's callback has returned.</exception>
    public void Stroke(DrawingPath path, DrawingPen pen)
    {
        Require();
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(pen);
        _inner.Stroke(path.Inner, pen.Pen);
    }

    /// <summary>Draws the outline of a path as a solid line.</summary>
    /// <param name="path">The path.</param>
    /// <param name="color">The colour.</param>
    /// <param name="width">The line width in points, zero or more.</param>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The width is negative or not finite.</exception>
    /// <exception cref="ObjectDisposedException">The drawing's callback has returned.</exception>
    public void Stroke(DrawingPath path, Color color, double width = 1)
    {
        Require();
        ArgumentNullException.ThrowIfNull(path);
        _inner.Stroke(path.Inner, new Pen(color, width));
    }

    /// <summary>
    /// Draws one paragraph of text with the start of its first baseline at a point. The text breaks only at hard line breaks (it has no maximum width; use <see cref="TextMeasurer"/> to wrap or to fit text yourself),
    /// and later lines go down the canvas. The style's unset properties come from the document's default style, then the library's defaults. <paramref name="alignment"/> places the text's block against the point:
    /// <see cref="TextAlignment.Start"/> puts the point at the edge where the text begins (the left edge of a left-to-right paragraph, the right edge of a right-to-left one), <see cref="TextAlignment.End"/> at the edge where it ends, and
    /// <see cref="TextAlignment.Center"/> in the middle. A character no font has a glyph for is reported in the document's diagnostics, with the page.
    /// </summary>
    /// <param name="text">One paragraph: a line break of any kind inside it (line feed, carriage return, U+0085, vertical tab, form feed, U+2028, U+2029) is a hard line break, other control characters are removed, and a surrogate without its pair is drawn as U+FFFD.</param>
    /// <param name="x">The x of the start of the first baseline.</param>
    /// <param name="y">The y of the first baseline (below the top of the text by its ascent).</param>
    /// <param name="style">The style, or <see langword="null"/> for the document's default.</param>
    /// <param name="alignment">Where the text sits against the point.</param>
    /// <param name="direction">The paragraph's base direction, or <see langword="null"/> for the document's.</param>
    /// <param name="opacity">From 0 (nothing) to 1 (opaque).</param>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A number is not finite, or the opacity is not from 0 to 1.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The alignment or the direction is not a defined value.</exception>
    /// <exception cref="ObjectDisposedException">The drawing's callback has returned.</exception>
    public void DrawText(string text, double x, double y, TextStyle? style = null, TextAlignment alignment = TextAlignment.Start, TextDirection? direction = null, double opacity = 1)
    {
        Require();
        ArgumentNullException.ThrowIfNull(text);
        _ = Check.Defined(alignment, nameof(alignment));
        _ = Check.Defined(direction, nameof(direction));
        Finite(x, nameof(x));
        Finite(y, nameof(y));
        double alpha = Opacity(opacity);
        ResolvedStyle resolved = _services.Base.Apply(style);
        _services.Family(resolved.Family);
        int[] order = _services.Fonts.Order(resolved.Family, resolved.Weight, resolved.Italic);
        ShapingFont[] fonts = [.. order.Select(i => _services.Fonts.Shaping[i])];
        _inner.DrawText(text, fonts, resolved.Size, new Point(x, y), alignment, direction ?? _services.Direction, new Brush(resolved.Color, alpha), resolved.Language);
    }

    /// <summary>Draws a picture into a rectangle, its top left corner at (<paramref name="x"/>, <paramref name="y"/>); the picture is stretched to the rectangle.</summary>
    /// <param name="image">The picture, written once to the document however often it is drawn.</param>
    /// <param name="x">The x of the rectangle's left edge.</param>
    /// <param name="y">The y of the rectangle's top edge.</param>
    /// <param name="width">The rectangle's width, more than zero.</param>
    /// <param name="height">The rectangle's height, more than zero.</param>
    /// <param name="opacity">From 0 (nothing) to 1 (opaque).</param>
    /// <exception cref="ArgumentNullException"><paramref name="image"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A number is not finite, a size is not more than zero, or the opacity is not from 0 to 1.</exception>
    /// <exception cref="ObjectDisposedException">The drawing's callback has returned.</exception>
    public void DrawImage(PdfImage image, double x, double y, double width, double height, double opacity = 1)
    {
        Require();
        ArgumentNullException.ThrowIfNull(image);
        Finite(x, nameof(x));
        Finite(y, nameof(y));
        Positive(width, nameof(width));
        Positive(height, nameof(height));
        _inner.DrawImage(image.Source, x, y, width, height, Opacity(opacity));
    }

    /// <summary>Ends the canvas: closes every save still open. Called by the document layer when the callback returns, even if it threw.</summary>
    internal void Finish()
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        _inner.End();
    }

    private static double Opacity(double opacity) =>
        opacity is >= 0 and <= 1 ? opacity : throw new ArgumentException($"Opacity is a number from 0 to 1, not {opacity}.", nameof(opacity));

    private static void Finite(double value, string name)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentException($"A number is finite, not {value}.", name);
        }
    }

    private static void Positive(double value, string name)
    {
        if (!(double.IsFinite(value) && value > 0))
        {
            throw new ArgumentException($"A size is finite and more than zero, not {value}.", name);
        }
    }

    private void Require() => ObjectDisposedException.ThrowIf(_finished, this);
}
