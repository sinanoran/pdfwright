using Pdfwright.Images;
using Pdfwright.Pdf;
using Pdfwright.Text;

namespace Pdfwright.Drawing;

/// <summary>
/// A drawing surface over a box of a page. Its origin is the top left corner of the box and its y axis points down, as the authors of charts expect;
/// the canvas turns that into the PDF's own space (origin bottom left, y up) with one <c>cm</c> when it starts, and everything after it is
/// written in canvas coordinates. Text is drawn the right way up despite that flip.
/// </summary>
/// <remarks>
/// <para>
/// The graphics state of the page is assumed to be at its defaults when the canvas starts (nothing of opacity, colour or line style set), and the
/// canvas keeps track of what it has set so that an operator is written only when the value changes. <see cref="Save"/> and <see cref="Restore"/>
/// are <c>q</c> and <c>Q</c>, kept balanced: <see cref="End"/> closes any save that is still open, and a <see cref="Restore"/> with none open is an error.
/// A clip lasts until the <see cref="Restore"/> that matches the <see cref="Save"/> before it, or the end of the canvas.
/// </para>
/// </remarks>
internal sealed class Canvas : IDisposable
{
    private readonly PdfPageWriter _page;
    private readonly ContentStreamBuilder _content;
    private readonly Stack<GraphicsState> _saved = new();
    private GraphicsState _state = GraphicsState.Initial;
    private bool _ended;

    /// <summary>Starts a canvas on a page.</summary>
    /// <param name="page">The page to draw on.</param>
    /// <param name="left">The x of the box's left edge, in the page's own space (points from the left edge of the page).</param>
    /// <param name="top">The y of the box's top edge, in the page's own space (points from the bottom edge of the page, y up).</param>
    /// <param name="width">The width of the box in points.</param>
    /// <param name="height">The height of the box in points.</param>
    /// <exception cref="ArgumentException">A number is not finite, or a size is negative.</exception>
    internal Canvas(PdfPageWriter page, double left, double top, double width, double height)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (!double.IsFinite(left) || !double.IsFinite(top))
        {
            throw new ArgumentException("The box's origin is finite.");
        }

        if (!(double.IsFinite(width) && width >= 0) || !(double.IsFinite(height) && height >= 0))
        {
            throw new ArgumentException("The box's width and height are finite and not negative.");
        }

        _page = page;
        _content = page.Content;
        Left = left;
        Top = top;
        Width = width;
        Height = height;

        // The one flip: the canvas's (x, y) is the page's (left + x, top - y).
        _content.SaveState().Transform(1, 0, 0, -1, left, top);
    }

    /// <summary>The x of the box's left edge in the page's space.</summary>
    internal double Left { get; }

    /// <summary>The y of the box's top edge in the page's space (y up).</summary>
    internal double Top { get; }

    internal double Width { get; }

    internal double Height { get; }

    /// <summary>The number of <see cref="Save"/> calls that no <see cref="Restore"/> has answered yet.</summary>
    internal int SaveDepth => _saved.Count;

    // ---- state

    /// <summary>Saves the graphics state (<c>q</c>): transform, clip, colours and line style.</summary>
    internal void Save()
    {
        RequireOpen();
        _saved.Push(_state);
        _content.SaveState();
    }

    /// <summary>Goes back to the state of the last <see cref="Save"/> (<c>Q</c>).</summary>
    /// <exception cref="InvalidOperationException">No save is open.</exception>
    internal void Restore()
    {
        RequireOpen();
        if (_saved.Count == 0)
        {
            throw new InvalidOperationException("Restore has no matching Save.");
        }

        _state = _saved.Pop();
        _content.RestoreState();
    }

    /// <summary>Moves the origin by a distance in the current units.</summary>
    internal void Translate(double dx, double dy) => Transform(Matrix.Translation(dx, dy));

    /// <summary>Scales the axes from the origin.</summary>
    internal void Scale(double sx, double sy) => Transform(Matrix.Scaling(sx, sy));

    /// <summary>Turns the axes about the origin by an angle in degrees, clockwise as seen on the canvas (y down).</summary>
    internal void Rotate(double degrees)
    {
        if (!double.IsFinite(degrees))
        {
            throw new ArgumentException("An angle is a finite number.", nameof(degrees));
        }

        Transform(Matrix.Rotation(degrees));
    }

    /// <summary>Applies a transform to everything drawn after it, before the transforms already in force (<c>cm</c>).</summary>
    internal void Transform(Matrix matrix)
    {
        RequireOpen();
        if (!matrix.IsFinite)
        {
            throw new ArgumentException("A transform's numbers are finite.", nameof(matrix));
        }

        double determinant = (matrix.A * matrix.D) - (matrix.B * matrix.C);
        if (!double.IsFinite(determinant) || determinant == 0)
        {
            throw new ArgumentException("A transform is invertible: its determinant is neither zero nor beyond the range of a number (a scale of 0 flattens the canvas).", nameof(matrix));
        }

        _content.Transform(matrix.A, matrix.B, matrix.C, matrix.D, matrix.E, matrix.F);
    }

    /// <summary>Restricts everything drawn after it to the inside of a path, until the matching <see cref="Restore"/> (<c>W</c> or <c>W*</c>, then <c>n</c>).</summary>
    internal void Clip(Path path, FillRule rule = FillRule.NonZero)
    {
        RequireOpen();
        ArgumentNullException.ThrowIfNull(path);
        if (path.IsEmpty)
        {
            // An empty clip is an empty area: nothing would show. Drawing a path of no area clips everything away, as a rectangle of no size does.
            _content.Rectangle(0, 0, 0, 0).Clip();
            return;
        }

        WritePath(path);
        _content.Clip(rule == FillRule.EvenOdd);
    }

    // ---- paths

    /// <summary>Paints the inside of a path. A path with nothing in it draws nothing.</summary>
    internal void Fill(Path path, Brush brush)
    {
        RequireOpen();
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(brush);
        if (path.IsEmpty)
        {
            return;
        }

        SetFillColor(brush.Color);
        SetOpacity(fill: brush.Opacity, stroke: null);
        WritePath(path);
        _content.Fill(brush.Rule == FillRule.EvenOdd);
    }

    /// <summary>Draws the outline of a path. A path with nothing in it draws nothing.</summary>
    internal void Stroke(Path path, Pen pen)
    {
        RequireOpen();
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(pen);
        if (path.IsEmpty)
        {
            return;
        }

        SetStrokeColor(pen.Color);
        SetLineStyle(pen);
        SetOpacity(fill: null, stroke: pen.Opacity);
        WritePath(path);
        _content.Stroke();
    }

    // ---- text

    /// <summary>
    /// Draws one paragraph of text with the start of its first baseline at a point. The text is laid out without a maximum width (it breaks only at hard line breaks), by
    /// the same <see cref="ParagraphLayouter"/> that <see cref="ParagraphMeasurer"/> asks, and written by <see cref="ParagraphLayout.Draw"/>, so what is measured is what is drawn.
    /// Later lines go down the canvas. <paramref name="alignment"/> places the text's block against the point: <see cref="TextAlignment.Start"/> puts the point at the edge where the text begins
    /// (the left edge of a left-to-right paragraph, the right edge of a right-to-left one), <see cref="TextAlignment.End"/> at the edge where it ends, and
    /// <see cref="TextAlignment.Center"/> in the middle; the lines of a text of several lines are aligned to one another in the same way.
    /// </summary>
    /// <param name="text">One paragraph.</param>
    /// <param name="fonts">The fonts to draw with, the first for preference and the rest as fallbacks.</param>
    /// <param name="size">The font size in points (the canvas's units, if it has been scaled).</param>
    /// <param name="at">The start of the first baseline.</param>
    /// <param name="alignment">Where the text sits against the point.</param>
    /// <param name="direction">The paragraph's base direction.</param>
    /// <param name="brush">The colour and opacity of the text (the fill rule does not apply); black and opaque when <see langword="null"/>.</param>
    /// <param name="language">A BCP 47 language tag for shaping, or <see langword="null"/>.</param>
    internal void DrawText(
        string text,
        IReadOnlyList<ShapingFont> fonts,
        double size,
        Point at,
        TextAlignment alignment = TextAlignment.Start,
        TextDirection direction = TextDirection.Auto,
        Brush? brush = null,
        string? language = null)
    {
        RequireOpen();
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(fonts);
        if (!double.IsFinite(at.X) || !double.IsFinite(at.Y))
        {
            throw new ArgumentException("A point's coordinates are finite.", nameof(at));
        }

        if (!(size > 0) || !double.IsFinite(size))
        {
            throw new ArgumentOutOfRangeException(nameof(size), size, "The size must be a positive number.");
        }

        ParagraphLayout layout = ParagraphLayouter.Layout(
            text,
            fonts,
            new ParagraphOptions
            {
                Size = size,
                Alignment = alignment,
                Language = language,
                ParagraphLevel = direction switch
                {
                    TextDirection.LeftToRight => 0,
                    TextDirection.RightToLeft => 1,
                    _ => Pdfwright.Unicode.UnicodeBidi.AutoLevel,
                },
            });

        if (text.Length == 0 || layout.Lines.All(l => l.Runs.Count == 0))
        {
            return;
        }

        // Only the fonts a run uses are embedded and named in the page's resources: a fallback that no character needed stays out of the file.
        HashSet<int> used = [.. layout.Lines.SelectMany(l => l.Runs).Select(r => r.Run.FontIndex)];
        PdfEmbeddedFont?[] embedded = new PdfEmbeddedFont?[fonts.Count];
        string?[] names = new string?[fonts.Count];
        foreach (int index in used)
        {
            ShapingFont f = fonts[index];
            PdfEmbeddedFont e = _page.Document.GetOrAddShared(f.Font, () => new PdfEmbeddedFont(_page.Document, f.Font));
            embedded[index] = e;
            names[index] = _page.UseFont(e.Reference);
        }

        // Where the block's left edge goes: the point is the start, the middle or the end of it, and start and end swap sides in a right-to-left paragraph.
        double width = layout.Width;
        double left = alignment switch
        {
            TextAlignment.Center => at.X - (width / 2),
            TextAlignment.End => layout.IsRightToLeft ? at.X : at.X - width,
            _ => layout.IsRightToLeft ? at.X - width : at.X,
        };

        Brush paint = brush ?? new Brush(Color.Black);
        SetFillColor(paint.Color);
        SetOpacity(fill: paint.Opacity, stroke: null);

        // The paragraph is drawn in a y-up space of its own: a flip back, local to this call, so that the glyphs are not mirrored. Its origin is the point,
        // and the baseline of the first line is at y = 0 in it (the paragraph's top is one ascent above).
        _content.SaveState().Transform(1, 0, 0, -1, 0, at.Y);
        layout.Draw(_content, embedded, names, left, layout.Lines[0].Ascent);
        _content.RestoreState();
    }

    // ---- images

    /// <summary>Draws an image into a rectangle of the canvas, its top left corner at (<paramref name="x"/>, <paramref name="y"/>); the image is stretched to the rectangle.</summary>
    /// <param name="image">The image, read once and written once to the document however often it is drawn.</param>
    /// <param name="x">The x of the rectangle's left edge.</param>
    /// <param name="y">The y of the rectangle's top edge.</param>
    /// <param name="width">The rectangle's width, more than zero.</param>
    /// <param name="height">The rectangle's height, more than zero.</param>
    /// <param name="opacity">From 0 to 1.</param>
    internal void DrawImage(ImageSource image, double x, double y, double width, double height, double opacity = 1)
    {
        RequireOpen();
        ArgumentNullException.ThrowIfNull(image);
        if (!double.IsFinite(x) || !double.IsFinite(y) || !(double.IsFinite(width) && width > 0) || !(double.IsFinite(height) && height > 0))
        {
            throw new ArgumentException("An image's rectangle is finite and has a positive width and height.");
        }

        if (!(opacity is >= 0 and <= 1))
        {
            throw new ArgumentException($"Opacity is a number from 0 to 1, not {opacity}.", nameof(opacity));
        }

        string name = _page.UseXObject(image.WriteTo(_page.Document));
        SetOpacity(fill: opacity, stroke: null);

        // An image fills the unit square with its first row at the top (y = 1) and the canvas has y down: a flip and a scale put it in the rectangle.
        _content.SaveState().Transform(width, 0, 0, -height, x, y + height).DrawXObject(name);
        _content.RestoreState();
    }

    // ---- the end

    /// <summary>Closes every save that is still open and ends the canvas. A canvas ends once; after that it draws nothing.</summary>
    internal void End()
    {
        if (_ended)
        {
            return;
        }

        while (_saved.Count > 0)
        {
            _saved.Pop();
            _content.RestoreState();
        }

        _content.RestoreState(); // the canvas's own q, from the start
        _ended = true;
    }

    public void Dispose() => End();

    // ---- what is written

    private void RequireOpen()
    {
        if (_ended)
        {
            throw new InvalidOperationException("The canvas has ended.");
        }
    }

    private void WritePath(Path path)
    {
        foreach (PathSegment s in path.Segments)
        {
            switch (s.Kind)
            {
                case PathSegmentKind.MoveTo:
                    _content.MoveTo(s.P1.X, s.P1.Y);
                    break;
                case PathSegmentKind.LineTo:
                    _content.LineTo(s.P1.X, s.P1.Y);
                    break;
                case PathSegmentKind.CubicTo:
                    _content.CurveTo(s.P1.X, s.P1.Y, s.P2.X, s.P2.Y, s.P3.X, s.P3.Y);
                    break;
                case PathSegmentKind.Rectangle:
                    _content.Rectangle(s.P1.X, s.P1.Y, s.P2.X, s.P2.Y);
                    break;
                default:
                    _content.ClosePath();
                    break;
            }
        }
    }

    private void SetFillColor(Color color)
    {
        if (_state.Fill == color)
        {
            return;
        }

        if (color.IsGray)
        {
            _content.FillGray(color.R);
        }
        else
        {
            _content.FillRgb(color.R, color.G, color.B);
        }

        _state = _state with { Fill = color };
    }

    private void SetStrokeColor(Color color)
    {
        if (_state.Stroke == color)
        {
            return;
        }

        if (color.IsGray)
        {
            _content.StrokeGray(color.R);
        }
        else
        {
            _content.StrokeRgb(color.R, color.G, color.B);
        }

        _state = _state with { Stroke = color };
    }

    private void SetLineStyle(Pen pen)
    {
        if (_state.Width != pen.Width)
        {
            _content.LineWidth(pen.Width);
            _state = _state with { Width = pen.Width };
        }

        if (_state.Cap != pen.Cap)
        {
            _content.LineCap((int)pen.Cap);
            _state = _state with { Cap = pen.Cap };
        }

        if (_state.Join != pen.Join)
        {
            _content.LineJoin((int)pen.Join);
            _state = _state with { Join = pen.Join };
        }

        if (_state.MiterLimit != pen.MiterLimit)
        {
            _content.MiterLimit(pen.MiterLimit);
            _state = _state with { MiterLimit = pen.MiterLimit };
        }

        if (_state.DashPhase != pen.DashPhase || !_state.Dashes.AsSpan().SequenceEqual(pen.Dashes.ToArray()))
        {
            _content.Dash([.. pen.Dashes], pen.DashPhase);
            _state = _state with { Dashes = [.. pen.Dashes], DashPhase = pen.DashPhase };
        }
    }

    /// <summary>Selects an <c>ExtGState</c> when the opacity of the fill or the stroke is not what it is now; the one that is not given stays as it is.</summary>
    private void SetOpacity(double? fill, double? stroke)
    {
        double newFill = fill ?? _state.FillAlpha;
        double newStroke = stroke ?? _state.StrokeAlpha;
        if (newFill == _state.FillAlpha && newStroke == _state.StrokeAlpha)
        {
            return;
        }

        _content.ExtGState(_page.UseOpacity(newFill, newStroke));
        _state = _state with { FillAlpha = newFill, StrokeAlpha = newStroke };
    }

    /// <summary>What the canvas has set in the graphics state, as the PDF's defaults are at the start.</summary>
    private sealed record GraphicsState(
        Color Fill,
        Color Stroke,
        double Width,
        LineCap Cap,
        LineJoin Join,
        double MiterLimit,
        double[] Dashes,
        double DashPhase,
        double FillAlpha,
        double StrokeAlpha)
    {
        internal static GraphicsState Initial { get; } = new(Color.Black, Color.Black, 1, LineCap.Butt, LineJoin.Miter, 10, [], 0, 1, 1);
    }
}
