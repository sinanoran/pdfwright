using System.Collections.ObjectModel;

namespace Pdfwright;

/// <summary>The rules about which blocks may hold which, checked where a block is made.</summary>
internal static class Nesting
{
    /// <summary>The blocks of a callout or a column cell, copied. Null blocks, a table and a page break are refused; in a column cell so are columns and a callout that holds columns.</summary>
    internal static ReadOnlyCollection<Block> Copy(IEnumerable<Block> blocks, bool inColumnCell, string paramName)
    {
        Block[] copy = [.. blocks];
        foreach (Block block in copy)
        {
            if (block is null)
            {
                throw new ArgumentException("A block is null.", paramName);
            }

            string? reason = block switch
            {
                Table => "A table cannot be inside a callout or a column: it can run over pages, and a cell or a callout holds blocks that stay in one place.",
                PageBreak => "A page break cannot be inside a callout or a column.",
                Columns when inColumnCell => "Columns cannot be inside a column cell (one level of columns only).",
                Callout { ContainsColumns: true } when inColumnCell => "A callout that holds columns cannot be inside a column cell (one level of columns only).",
                _ => null,
            };
            if (reason is not null)
            {
                throw new ArgumentException(reason, paramName);
            }
        }

        return new ReadOnlyCollection<Block>(copy);
    }

    internal static bool ContainsColumns(IEnumerable<Block> blocks) => blocks.Any(b => b is Columns or Callout { ContainsColumns: true });

    internal static bool SameBlocks(IReadOnlyList<Block> left, IReadOnlyList<Block> right) => left.SequenceEqual(right);

    internal static int HashBlocks(IEnumerable<Block> blocks)
    {
        HashCode hash = new();
        foreach (Block block in blocks)
        {
            hash.Add(block);
        }

        return hash.ToHashCode();
    }
}

/// <summary>
/// A row of 1 to 4 equal-width cells side by side (the KPI row), each holding blocks that flow down inside the cell. A cell may hold paragraphs, headings, spacers, drawings, images and callouts (a callout that
/// itself holds columns, a table, a page break and columns may not be inside a cell: one level of columns only). The cells are as high as the highest, and the row is <b>kept together</b>: it moves to the next page
/// when it does not fit on the page it starts on, and is never split between pages. A row that is taller than the content area of a whole page cannot be laid out and fails the save with an
/// <see cref="InvalidOperationException"/>. In a right-to-left document (<see cref="Direction"/>, else the document's) the first cell is at the right edge. Two columns blocks are equal when their cells (block by block) and settings are equal.
/// </summary>
public sealed record Columns : Block
{
    private readonly ReadOnlyCollection<ReadOnlyCollection<Block>> _cells;
    private Length _gap = Length.Pt(12);
    private TextDirection? _direction;
    private Length? _spaceBefore;
    private Length? _spaceAfter;

    /// <summary>A row with one block in each cell.</summary>
    /// <param name="cells">The blocks, one for each cell, in logical order: 1 to 4 of them.</param>
    /// <exception cref="ArgumentNullException"><paramref name="cells"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">There are not 1 to 4 cells.</exception>
    /// <exception cref="ArgumentException">A block is null, or is one that cannot be in a cell (a table, a page break, columns).</exception>
    public Columns(IEnumerable<Block> cells)
        : this(OneBlockEach(cells))
    {
    }

    /// <summary>A row whose cells hold several blocks each.</summary>
    /// <param name="cells">The blocks of each cell, in logical order: 1 to 4 cells.</param>
    /// <exception cref="ArgumentNullException"><paramref name="cells"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">There are not 1 to 4 cells.</exception>
    /// <exception cref="ArgumentException">A cell or a block is null, or a block cannot be in a cell (a table, a page break, columns).</exception>
    public Columns(IEnumerable<IEnumerable<Block>> cells)
    {
        ArgumentNullException.ThrowIfNull(cells);
        List<ReadOnlyCollection<Block>> copy = [];
        foreach (IEnumerable<Block> cell in cells)
        {
            if (cell is null)
            {
                throw new ArgumentException("A cell is null.", nameof(cells));
            }

            copy.Add(Nesting.Copy(cell, inColumnCell: true, nameof(cells)));
        }

        if (copy.Count is < 1 or > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(cells), copy.Count, "Columns has 1 to 4 cells.");
        }

        _cells = new ReadOnlyCollection<ReadOnlyCollection<Block>>(copy);
    }

    /// <summary>The blocks of each cell, in logical order.</summary>
    public IReadOnlyList<IReadOnlyList<Block>> Cells => _cells;

    /// <summary>The space between two cells; 12 points by default. The cells share what is left of the width equally; a gap that leaves a cell less than 4 points fails the save with an <see cref="InvalidOperationException"/> that names the gap.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative or not finite.</exception>
    public Length Gap
    {
        get => _gap;
        init => _gap = Check.Space(value, nameof(Gap)) ?? Length.Zero;
    }

    /// <summary>The direction of the row: the order of the cells (right to left puts the first cell at the right edge) and the base direction of the paragraphs inside that set none. <see langword="null"/> takes the document's; <see cref="TextDirection.Auto"/> means left to right for the cells.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a <see cref="TextDirection"/>.</exception>
    public TextDirection? Direction
    {
        get => _direction;
        init => _direction = Check.Defined(value, nameof(Direction));
    }

    /// <summary>The space above the row, or <see langword="null"/> for none. It is dropped at the top of a page.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative or not finite.</exception>
    public Length? SpaceBefore
    {
        get => _spaceBefore;
        init => _spaceBefore = Check.Space(value, nameof(SpaceBefore));
    }

    /// <summary>The space below the row, or <see langword="null"/> for the default of 0.6 of the document's font size. It is dropped at the bottom of a page.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative or not finite.</exception>
    public Length? SpaceAfter
    {
        get => _spaceAfter;
        init => _spaceAfter = Check.Space(value, nameof(SpaceAfter));
    }

    /// <summary>Whether another row has the same cells (block by block) and settings.</summary>
    /// <param name="other">The other row.</param>
    /// <returns><see langword="true"/> when it does.</returns>
    public bool Equals(Columns? other) =>
        other is not null
        && _cells.Count == other._cells.Count
        && _cells.Zip(other._cells).All(pair => Nesting.SameBlocks(pair.First, pair.Second))
        && Gap == other.Gap
        && Direction == other.Direction
        && SpaceBefore == other.SpaceBefore
        && SpaceAfter == other.SpaceAfter;

    /// <inheritdoc />
    public override int GetHashCode()
    {
        HashCode hash = new();
        foreach (ReadOnlyCollection<Block> cell in _cells)
        {
            hash.Add(Nesting.HashBlocks(cell));
        }

        hash.Add(Gap);
        hash.Add(Direction);
        hash.Add(SpaceBefore);
        hash.Add(SpaceAfter);
        return hash.ToHashCode();
    }

    private static IEnumerable<IEnumerable<Block>> OneBlockEach(IEnumerable<Block> cells)
    {
        ArgumentNullException.ThrowIfNull(cells);
        return [.. cells.Select(c => (IEnumerable<Block>)[c ?? throw new ArgumentException("A block is null.", nameof(cells))])];
    }
}

/// <summary>
/// A note: blocks on a filled background with a border, padding and, optionally, rounded corners. It holds paragraphs, headings, spacers, drawings, images, columns and other callouts (not a table or a page break).
/// A callout <b>may be split between pages</b> between the lines of its paragraphs (and between its blocks); each page's part is drawn as a complete box with its own fill, border and padding.
/// Set <see cref="KeepTogether"/> to move a callout that would be split to the next page instead; one taller than a page is split anyway. A drawing, an image or a row of columns inside is never split:
/// it moves to the next part, and one taller than a page fails the save with an <see cref="InvalidOperationException"/>, as does a callout inside a column cell or another callout that is taller than a page (those are never split), and one whose border and padding leave less than 4 points of width or no room for its first block on a page. A callout inside a column cell or another callout is not split. Two callouts are equal when their content (block by block) and settings are equal.
/// </summary>
public sealed record Callout : Block
{
    private readonly ReadOnlyCollection<Block> _content;
    private Margins _padding = Margins.Pt(8, 10, 8, 10);
    private Length _cornerRadius;
    private TextDirection? _direction;
    private Length? _spaceBefore;
    private Length? _spaceAfter;

    /// <summary>A callout around one block.</summary>
    /// <param name="content">The block, usually a <see cref="Paragraph"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="content"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The block cannot be in a callout (a table or a page break).</exception>
    public Callout(Block content)
        : this(new[] { content ?? throw new ArgumentNullException(nameof(content)) })
    {
    }

    /// <summary>A callout around several blocks, which flow down inside it.</summary>
    /// <param name="content">The blocks, at least one.</param>
    /// <exception cref="ArgumentNullException"><paramref name="content"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">There is no block, a block is null, or one cannot be in a callout (a table or a page break).</exception>
    public Callout(IEnumerable<Block> content)
    {
        ArgumentNullException.ThrowIfNull(content);
        _content = Nesting.Copy(content, inColumnCell: false, nameof(content));
        if (_content.Count == 0)
        {
            throw new ArgumentException("A callout holds at least one block.", nameof(content));
        }

        ContainsColumns = Nesting.ContainsColumns(_content);
    }

    /// <summary>The blocks inside the callout.</summary>
    public IReadOnlyList<Block> Content => _content;

    /// <summary>The colour of the background, or <see langword="null"/> for none. A light gray by default.</summary>
    public Color? Fill { get; init; } = Color.Gray(0.95);

    /// <summary>The line around the box (drawn inside its edge, so that the box is as wide as the content area), or <see langword="null"/> for none. A hairline of 0.75 points in mid gray by default.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The line's width is not greater than zero (a <c>default</c> value).</exception>
    public BorderLine? Border
    {
        get => _border;
        init => _border = Check.Line(value, nameof(Border));
    }

    private BorderLine? _border = new BorderLine(Length.Pt(0.75), Color.Gray(0.7));

    /// <summary>The space between the border and the content, on the four physical sides. 8 points above and below and 10 at the sides by default.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A side is negative or not finite.</exception>
    public Margins Padding
    {
        get => _padding;
        init => _padding = Check.Padding(value, nameof(Padding));
    }

    /// <summary>The radius of the corners of the box; zero (square corners) by default. A radius larger than half the shorter side of the box (of each page's part, for a split callout) is cut to that half, so the corners stay round and the box is at most a pill. Each page's part of a split callout has the rounded corners.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative or not finite.</exception>
    public Length CornerRadius
    {
        get => _cornerRadius;
        init => _cornerRadius = Check.Space(value, nameof(CornerRadius)) ?? Length.Zero;
    }

    /// <summary>Whether a callout that does not fit on the rest of the page moves, whole, to the next page, instead of being split between its lines. A callout taller than a page is split whatever this is.</summary>
    public bool KeepTogether { get; init; }

    /// <summary>The base direction of the paragraphs inside that set none, and of the columns inside. <see langword="null"/> takes the document's.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a <see cref="TextDirection"/>.</exception>
    public TextDirection? Direction
    {
        get => _direction;
        init => _direction = Check.Defined(value, nameof(Direction));
    }

    /// <summary>The space above the callout, or <see langword="null"/> for none. It is dropped at the top of a page.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative or not finite.</exception>
    public Length? SpaceBefore
    {
        get => _spaceBefore;
        init => _spaceBefore = Check.Space(value, nameof(SpaceBefore));
    }

    /// <summary>The space below the callout, or <see langword="null"/> for the default of 0.6 of the document's font size. It is dropped at the bottom of a page.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative or not finite.</exception>
    public Length? SpaceAfter
    {
        get => _spaceAfter;
        init => _spaceAfter = Check.Space(value, nameof(SpaceAfter));
    }

    internal bool ContainsColumns { get; }

    /// <summary>Whether another callout has the same content (block by block) and settings.</summary>
    /// <param name="other">The other callout.</param>
    /// <returns><see langword="true"/> when it does.</returns>
    public bool Equals(Callout? other) =>
        other is not null
        && Nesting.SameBlocks(_content, other._content)
        && Fill == other.Fill
        && Border == other.Border
        && Padding == other.Padding
        && CornerRadius == other.CornerRadius
        && KeepTogether == other.KeepTogether
        && Direction == other.Direction
        && SpaceBefore == other.SpaceBefore
        && SpaceAfter == other.SpaceAfter;

    /// <inheritdoc />
    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(Nesting.HashBlocks(_content));
        hash.Add(Fill);
        hash.Add(Border);
        hash.Add(Padding);
        hash.Add(CornerRadius);
        hash.Add(KeepTogether);
        hash.Add(Direction);
        hash.Add(SpaceBefore);
        hash.Add(SpaceAfter);
        return hash.ToHashCode();
    }
}

/// <summary>
/// A figure drawn with code, usually a chart: a box of a fixed size and a callback that draws on a <see cref="DrawingCanvas"/> inside it. The callback runs while the document is saved, once each time the drawing is placed
/// (a drawing in a header runs once for each page), so it should be a pure function of its data. The drawing is never split and never scaled: it moves to the next page when it does not fit, and one wider than the space
/// it is in, or taller than a page, fails the save with an <see cref="InvalidOperationException"/>. It is placed at the start edge of the available width. <see cref="DrawingCanvas.DrawText"/> uses the direction of the <see cref="Columns"/> or <see cref="Callout"/> the drawing is in (their <c>Direction</c>, else the document's) when it is not given one. <see cref="AltText"/> is kept for tagged output, which does not exist yet:
/// it is written nowhere in the file today. Two drawings are equal when their size, callback (the same delegate) and settings are equal.
/// </summary>
public sealed record Drawing : Block
{
    private Length? _spaceBefore;
    private Length? _spaceAfter;

    /// <summary>A drawing of the given size.</summary>
    /// <param name="width">The width, finite and greater than zero.</param>
    /// <param name="height">The height, finite and greater than zero.</param>
    /// <param name="draw">What draws the figure; it is given the canvas, whose origin is the top left corner of the box.</param>
    /// <exception cref="ArgumentOutOfRangeException">A size is not a finite length greater than zero.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="draw"/> is <see langword="null"/>.</exception>
    public Drawing(Length width, Length height, Action<DrawingCanvas> draw)
    {
        if (!width.IsFinite || !(width > Length.Zero))
        {
            throw new ArgumentOutOfRangeException(nameof(width), width, "A drawing's width is finite and greater than zero.");
        }

        if (!height.IsFinite || !(height > Length.Zero))
        {
            throw new ArgumentOutOfRangeException(nameof(height), height, "A drawing's height is finite and greater than zero.");
        }

        ArgumentNullException.ThrowIfNull(draw);
        Width = width;
        Height = height;
        Draw = draw;
    }

    /// <summary>The width of the box.</summary>
    public Length Width { get; }

    /// <summary>The height of the box.</summary>
    public Length Height { get; }

    /// <summary>The callback that draws the figure.</summary>
    public Action<DrawingCanvas> Draw { get; }

    /// <summary>A text that says what the figure shows, for readers who cannot see it. It is kept for tagged output and written nowhere in the file yet.</summary>
    public string? AltText { get; init; }

    /// <summary>The space above the drawing, or <see langword="null"/> for none. It is dropped at the top of a page.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative or not finite.</exception>
    public Length? SpaceBefore
    {
        get => _spaceBefore;
        init => _spaceBefore = Check.Space(value, nameof(SpaceBefore));
    }

    /// <summary>The space below the drawing, or <see langword="null"/> for the default of 0.6 of the document's font size. It is dropped at the bottom of a page.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative or not finite.</exception>
    public Length? SpaceAfter
    {
        get => _spaceAfter;
        init => _spaceAfter = Check.Space(value, nameof(SpaceAfter));
    }
}

/// <summary>
/// A picture in the flow of blocks. With neither a <see cref="Width"/> nor a <see cref="Height"/> it has its natural size (a pixel is 0.75 point, 96 pixels to the inch); with one, the other follows the picture's proportions;
/// with both, the picture is stretched to them (<see cref="DrawingCanvas.DrawImage"/> always stretches a picture to its rectangle). It is never wider than the space it is in or taller than a page: it is scaled down, in proportion, to fit. It is never split, and moves to the next page when it does not fit.
/// <see cref="Alignment"/> places a picture that is narrower than the space (logical: start is the left edge in a left-to-right document). <see cref="AltText"/> is kept for tagged output, which does not exist yet,
/// and is written nowhere in the file today. The names <c>Image</c> and <c>Drawing</c> are also in <c>System.Drawing</c>: where both namespaces are imported, write <c>using Image = Pdfwright.Image;</c>. Two images are equal when they are the same <see cref="PdfImage"/> object and their settings are equal.
/// </summary>
public sealed record Image : Block
{
    private Length? _width;
    private Length? _height;
    private TextAlignment _alignment = TextAlignment.Start;
    private Length? _spaceBefore;
    private Length? _spaceAfter;

    /// <summary>An image block for a picture.</summary>
    /// <param name="source">The picture.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
    public Image(PdfImage source)
    {
        ArgumentNullException.ThrowIfNull(source);
        Source = source;
    }

    /// <summary>The picture.</summary>
    public PdfImage Source { get; }

    /// <summary>The width, or <see langword="null"/> to take it from the height or the picture's natural size.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a finite length greater than zero.</exception>
    public Length? Width
    {
        get => _width;
        init => _width = Check.Size(value, nameof(Width), "An image width");
    }

    /// <summary>The height, or <see langword="null"/> to take it from the width or the picture's natural size.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a finite length greater than zero.</exception>
    public Length? Height
    {
        get => _height;
        init => _height = Check.Size(value, nameof(Height), "An image height");
    }

    /// <summary>Where a picture narrower than the space sits: <see cref="TextAlignment.Start"/> (the default), centre or end. Logical, so start is the right edge in a right-to-left document.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a <see cref="TextAlignment"/>.</exception>
    public TextAlignment Alignment
    {
        get => _alignment;
        init => _alignment = Check.Defined(value, nameof(Alignment));
    }

    /// <summary>A text that says what the picture shows, for readers who cannot see it. It is kept for tagged output and written nowhere in the file yet.</summary>
    public string? AltText { get; init; }

    /// <summary>The space above the picture, or <see langword="null"/> for none. It is dropped at the top of a page.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative or not finite.</exception>
    public Length? SpaceBefore
    {
        get => _spaceBefore;
        init => _spaceBefore = Check.Space(value, nameof(SpaceBefore));
    }

    /// <summary>The space below the picture, or <see langword="null"/> for the default of 0.6 of the document's font size. It is dropped at the bottom of a page.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative or not finite.</exception>
    public Length? SpaceAfter
    {
        get => _spaceAfter;
        init => _spaceAfter = Check.Space(value, nameof(SpaceAfter));
    }
}

/// <summary>What a header or a footer is told about the page it is made for.</summary>
public sealed class PageContext
{
    internal PageContext(int number, int count, PageSize pageSize)
    {
        Number = number;
        Count = count;
        PageSize = pageSize;
    }

    /// <summary>The number of the page, from 1.</summary>
    public int Number { get; }

    /// <summary>The number of pages of the document. It is always the final number: headers and footers are made after the whole body has been laid out, in one pass, so reading it costs nothing.</summary>
    public int Count { get; }

    /// <summary>Whether this is the first page.</summary>
    public bool IsFirst => Number == 1;

    /// <summary>Whether this is the last page.</summary>
    public bool IsLast => Number == Count;

    /// <summary>The size of the page.</summary>
    public PageSize PageSize { get; }
}
