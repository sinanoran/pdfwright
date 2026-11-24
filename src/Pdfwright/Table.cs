using System.Collections.ObjectModel;

namespace Pdfwright;

/// <summary>How a <see cref="ColumnWidth"/> is decided.</summary>
public enum ColumnWidthKind
{
    /// <summary>Measured from the content of the header and the first 100 rows.</summary>
    Auto,

    /// <summary>A given length.</summary>
    Fixed,

    /// <summary>A share of what the fixed and automatic columns leave.</summary>
    Fraction,
}

/// <summary>
/// The width of a table column: <see cref="Fixed"/> (a length), <see cref="Fraction"/> (a weight: the columns of this kind share what is left in proportion to their weights, like CSS <c>fr</c> units) or
/// <see cref="Auto"/> (measured from the header and the first 100 rows, see <see cref="Table"/>). A default value is <see cref="Auto"/>.
/// </summary>
public readonly record struct ColumnWidth
{
    private ColumnWidth(ColumnWidthKind kind, double value)
    {
        Kind = kind;
        Value = value;
    }

    /// <summary>Measured from the content of the header and the first 100 rows.</summary>
    public static ColumnWidth Auto => default;

    /// <summary>The kind of width.</summary>
    public ColumnWidthKind Kind { get; }

    /// <summary>The length of a fixed column, in points; zero for the other kinds.</summary>
    internal double Value { get; }

    /// <summary>The length of a <see cref="ColumnWidthKind.Fixed"/> column; zero for the other kinds.</summary>
    public Length Length => Kind == ColumnWidthKind.Fixed ? Length.Pt(Value) : Length.Zero;

    /// <summary>The weight of a <see cref="ColumnWidthKind.Fraction"/> column; zero for the other kinds.</summary>
    public double Weight => Kind == ColumnWidthKind.Fraction ? Value : 0;

    /// <summary>A column of the given width, whatever its content.</summary>
    /// <param name="width">The width, finite and greater than zero.</param>
    /// <returns>The column width.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The width is not a finite length greater than zero.</exception>
    public static ColumnWidth Fixed(Length width) =>
        width.IsFinite && width > Length.Zero
            ? new ColumnWidth(ColumnWidthKind.Fixed, width.Points)
            : throw new ArgumentOutOfRangeException(nameof(width), width, "A fixed column width is finite and greater than zero.");

    /// <summary>A column that takes a share of what the fixed and automatic columns leave, in proportion to its weight.</summary>
    /// <param name="weight">The weight, finite and greater than zero: <c>Fraction(2)</c> is twice as wide as <c>Fraction(1)</c>.</param>
    /// <returns>The column width.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The weight is not a finite number greater than zero.</exception>
    public static ColumnWidth Fraction(double weight) =>
        weight > 0 && double.IsFinite(weight)
            ? new ColumnWidth(ColumnWidthKind.Fraction, weight)
            : throw new ArgumentOutOfRangeException(nameof(weight), weight, "A column's weight is a finite number greater than zero.");
}

/// <summary>One column of a table: its width and the alignment of its cells' text.</summary>
public sealed record TableColumn
{
    private TextAlignment _alignment = TextAlignment.Start;

    /// <summary>A column of the given width.</summary>
    /// <param name="width">The width; <see cref="ColumnWidth.Auto"/> when not given.</param>
    public TableColumn(ColumnWidth width = default)
    {
        Width = width;
    }

    /// <summary>The width of the column.</summary>
    public ColumnWidth Width { get; }

    /// <summary>Where the text of the column's cells sits in the cell (logical: <see cref="TextAlignment.Start"/> is the right edge in a right-to-left paragraph). A cell can override it.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a <see cref="TextAlignment"/>.</exception>
    public TextAlignment Alignment
    {
        get => _alignment;
        init => _alignment = Check.Defined(value, nameof(Alignment));
    }
}

/// <summary>
/// One cell of a table: one or more paragraphs, with its own padding, fill, alignment and style where they differ from the table's. There are no cell spans.
/// The paragraphs of a cell are laid out one below the other with the space after each (and before the next); a paragraph's own alignment is used when it is not
/// <see cref="TextAlignment.Start"/>, otherwise the cell's alignment, otherwise the column's.
/// </summary>
public sealed record TableCell
{
    private TextAlignment? _alignment;
    private Margins? _padding;

    /// <summary>A cell with one paragraph of text.</summary>
    /// <param name="text">The text. A line break of any kind inside it (line feed, carriage return, U+0085, vertical tab, form feed, U+2028, U+2029) is a hard line break in the same block, never a new paragraph; other control characters are removed and a surrogate without its pair is drawn as U+FFFD. The string is kept as given.</param>
    public TableCell(string text)
        : this(new Paragraph(text))
    {
    }

    /// <summary>A cell with one paragraph.</summary>
    /// <param name="paragraph">The paragraph.</param>
    public TableCell(Paragraph paragraph)
        : this([paragraph], owned: true)
    {
    }

    /// <summary>A cell with several paragraphs, one below the other.</summary>
    /// <param name="paragraphs">The paragraphs; at least one.</param>
    /// <exception cref="ArgumentException">There is no paragraph, or one is null.</exception>
    public TableCell(IEnumerable<Paragraph> paragraphs)
        : this(Check.Copy(paragraphs ?? throw new ArgumentNullException(nameof(paragraphs))), owned: true)
    {
    }

    /// <summary>A cell of the paragraphs of an array that is its own (a copy, or one made for it), which it keeps as its list of paragraphs.</summary>
    private TableCell(Paragraph[] paragraphs, bool owned)
    {
        _ = owned;
        if (paragraphs.Length == 0 || Check.AnyNull(paragraphs))
        {
            throw new ArgumentException("A cell has at least one paragraph, and none of them is null.", nameof(paragraphs));
        }

        Paragraphs = new ReadOnlyCollection<Paragraph>(paragraphs);
    }

    /// <summary>The paragraphs of the cell.</summary>
    public IReadOnlyList<Paragraph> Paragraphs { get; }

    /// <summary>The padding between the cell's edges and its text (physical, like <see cref="Margins"/>), or <see langword="null"/> for the table's <see cref="Table.CellPadding"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A side is negative or not finite.</exception>
    public Margins? Padding
    {
        get => _padding;
        init => _padding = Check.Padding(value, nameof(Padding));
    }

    /// <summary>The fill of the cell, over the row's and the table's, or <see langword="null"/> for none of its own.</summary>
    public Color? Fill { get; init; }

    /// <summary>The alignment of this cell's text, or <see langword="null"/> for the column's.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a <see cref="TextAlignment"/>.</exception>
    public TextAlignment? Alignment
    {
        get => _alignment;
        init => _alignment = Check.Defined(value, nameof(Alignment));
    }

    /// <summary>A style for this cell's text over the table's (and, in the header, the header's), under the paragraphs' own styles; <see langword="null"/> for none.</summary>
    public TextStyle? Style { get; init; }

    /// <summary>Whether another cell has the same paragraphs (compared one by one) and settings.</summary>
    /// <param name="other">The other cell.</param>
    /// <returns><see langword="true"/> when it does.</returns>
    public bool Equals(TableCell? other) =>
        other is not null
        && Paragraphs.SequenceEqual(other.Paragraphs)
        && Padding == other.Padding
        && Fill == other.Fill
        && Alignment == other.Alignment
        && Equals(Style, other.Style);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        HashCode hash = new();
        foreach (Paragraph paragraph in Paragraphs)
        {
            hash.Add(paragraph);
        }

        hash.Add(Padding);
        hash.Add(Fill);
        hash.Add(Alignment);
        hash.Add(Style);
        return hash.ToHashCode();
    }
}

/// <summary>A row of a table: its cells in logical order (the first is in the first column, which is on the right in a right-to-left table). A row with fewer cells than the table has columns leaves the rest empty.</summary>
public sealed record TableRow
{
    /// <summary>A row of cells.</summary>
    /// <param name="cells">The cells, at least one.</param>
    /// <exception cref="ArgumentException">There is no cell, or one is null.</exception>
    public TableRow(IEnumerable<TableCell> cells)
        : this(Check.Copy(cells ?? throw new ArgumentNullException(nameof(cells))), owned: true)
    {
    }

    /// <summary>A row of the given cells.</summary>
    /// <param name="cells">The cells, at least one.</param>
    /// <exception cref="ArgumentException">There is no cell, or one is null.</exception>
    public TableRow(params TableCell[] cells)
        : this((IEnumerable<TableCell>)cells)
    {
    }

    /// <summary>A row of cells that are each a paragraph of the given text.</summary>
    /// <param name="texts">The texts, at least one.</param>
    /// <exception cref="ArgumentException">There is no text.</exception>
    /// <exception cref="ArgumentNullException">A text is null.</exception>
    public TableRow(params string[] texts)
        : this(Texts(texts), owned: true)
    {
    }

    /// <summary>A row of the cells of an array that is its own (a copy, or one made for it), which it keeps as its list of cells.</summary>
    private TableRow(TableCell[] cells, bool owned)
    {
        _ = owned;
        if (cells.Length == 0 || Check.AnyNull(cells))
        {
            throw new ArgumentException("A row has at least one cell, and none of them is null.", nameof(cells));
        }

        Cells = new ReadOnlyCollection<TableCell>(cells);
    }

    /// <summary>The cells of the row.</summary>
    public IReadOnlyList<TableCell> Cells { get; }

    /// <summary>The fill of the row, under the fill of its cells and over the table's header and zebra fills; <see langword="null"/> for none of its own.</summary>
    public Color? Fill { get; init; }

    /// <summary>Whether another row has the same cells (compared one by one) and fill.</summary>
    /// <param name="other">The other row.</param>
    /// <returns><see langword="true"/> when it does.</returns>
    public bool Equals(TableRow? other) => other is not null && Cells.SequenceEqual(other.Cells) && Fill == other.Fill;

    /// <inheritdoc />
    public override int GetHashCode()
    {
        HashCode hash = new();
        foreach (TableCell cell in Cells)
        {
            hash.Add(cell);
        }

        hash.Add(Fill);
        return hash.ToHashCode();
    }

    /// <summary>A cell of one paragraph for each text, in an array of their number.</summary>
    private static TableCell[] Texts(string[] texts)
    {
        ArgumentNullException.ThrowIfNull(texts);
        if (texts.Length == 0)
        {
            throw new ArgumentException("A row has at least one cell.", nameof(texts));
        }

        TableCell[] cells = new TableCell[texts.Length];
        for (int i = 0; i < texts.Length; i++)
        {
            cells[i] = new TableCell(texts[i] ?? throw new ArgumentNullException(nameof(texts), "A text is null."));
        }

        return cells;
    }
}

/// <summary>A line of a table's border: a width and a colour.</summary>
public readonly record struct BorderLine
{
    /// <summary>A line of the given width and colour.</summary>
    /// <param name="width">The width, finite and greater than zero.</param>
    /// <param name="color">The colour.</param>
    /// <exception cref="ArgumentOutOfRangeException">The width is not a finite length greater than zero.</exception>
    public BorderLine(Length width, Color color)
    {
        if (!width.IsFinite || !(width > Length.Zero))
        {
            throw new ArgumentOutOfRangeException(nameof(width), width, "A border line's width is finite and greater than zero.");
        }

        Width = width;
        Color = color;
    }

    /// <summary>The width of the line.</summary>
    public Length Width { get; }

    /// <summary>The colour of the line.</summary>
    public Color Color { get; }
}

/// <summary>
/// The lines drawn around and between the cells of a table, by kind of edge: the outer edges at the top and bottom, the outer edges at the start and end, the lines between rows
/// and the lines between columns. An edge kind that is <see langword="null"/> is not drawn. When a table runs over several pages, each page's part is closed at the bottom with the outer horizontal line
/// and opened at the top with it. The header rows are rows like the others as far as borders go.
/// </summary>
public sealed record Borders
{
    private readonly BorderLine? _outerHorizontal;
    private readonly BorderLine? _outerVertical;
    private readonly BorderLine? _innerHorizontal;
    private readonly BorderLine? _innerVertical;

    /// <summary>No lines.</summary>
    public static Borders None { get; } = new();

    /// <summary>Every edge, in a hairline of 0.5 points in black.</summary>
    public static Borders All { get; } = Of(Line(), Line(), Line(), Line());

    /// <summary>Horizontal lines only: above the first row of a page, below the last, and between rows; 0.5 points in black.</summary>
    public static Borders Horizontal { get; } = Of(Line(), null, Line(), null);

    /// <summary>A box around the table (the outer edges) and nothing inside; 0.5 points in black.</summary>
    public static Borders Outer { get; } = Of(Line(), Line(), null, null);

    /// <summary>The lines above the first row and below the last row of the table's part on a page.</summary>
    public BorderLine? OuterHorizontal
    {
        get => _outerHorizontal;
        init => _outerHorizontal = Check.Line(value, nameof(OuterHorizontal));
    }

    /// <summary>The lines at the left and right edges of the table.</summary>
    public BorderLine? OuterVertical
    {
        get => _outerVertical;
        init => _outerVertical = Check.Line(value, nameof(OuterVertical));
    }

    /// <summary>The lines between two rows.</summary>
    public BorderLine? InnerHorizontal
    {
        get => _innerHorizontal;
        init => _innerHorizontal = Check.Line(value, nameof(InnerHorizontal));
    }

    /// <summary>The lines between two columns.</summary>
    public BorderLine? InnerVertical
    {
        get => _innerVertical;
        init => _innerVertical = Check.Line(value, nameof(InnerVertical));
    }

    private static BorderLine Line() => new(Length.Pt(0.5), Color.Black);

    private static Borders Of(BorderLine? outerHorizontal, BorderLine? outerVertical, BorderLine? innerHorizontal, BorderLine? innerVertical) =>
        new() { OuterHorizontal = outerHorizontal, OuterVertical = outerVertical, InnerHorizontal = innerHorizontal, InnerVertical = innerVertical };
}

/// <summary>
/// A table. The columns are fixed, fractional or automatic, the header rows are repeated on every page, and a row is never split between pages unless it is taller than a page. Cells have no spans.
/// </summary>
/// <remarks>
/// <para><b>Rows are streamed.</b> <see cref="Rows"/> is enumerated when a document is saved, once for each save of each time the table was added, and never held whole: a row is laid out, drawn and dropped.
/// Only for automatic columns, the first 100 rows are held while the columns are measured, and then the enumeration continues. A sequence that is a lazy iterator is therefore enumerated again by the next save, and must be able to be: give a
/// list, or a sequence that can be started again, when a document is saved more than once. (<c>IAsyncEnumerable</c> is not accepted: a save works on a worker thread and the writer is synchronous, so consuming one would block a thread on it.)</para>
/// <para><b>Column widths.</b> The columns take the content width of the page in this order. Fixed columns take their length. Automatic columns are measured from the cells of the header and of the first 100 rows: the narrowest a
/// column can be is its widest word (the minimum content width) and the widest it would want is its longest unbroken line (the maximum), each plus the cell padding. Fraction columns have no content minimum. When the minimums of all columns
/// (fixed lengths, automatic minimums, and the padding of fraction columns) do not fit, the room is shared by water-filling: a column whose minimum is within an equal share of what is left keeps it,
/// and only the wider columns are cut down, to an equal share of what the others leave, so one very long word narrows its own column and not the short ones. The text then wraps (a word wider than its cell is cut between characters).
/// The minimum of a fraction column is the <see cref="CellPadding"/> of the table; a cell with a bigger padding of its own falls back to an inner width of 1 point in a column that narrow. When they fit, the automatic columns grow towards their maximums
/// (in proportion to how much each wants, if there is not room for all), and the fraction columns share what is left in proportion to their weights. A table with no fraction column is as wide as its columns; one with a fraction column fills the content width.
/// A column is measured only from the sample, so a cell lower down that is wider than the column wraps.</para>
/// <para><b>Direction.</b> Columns run from the start edge: left to right in a table whose <see cref="Direction"/> is <see cref="TextDirection.LeftToRight"/>, right to left in a right-to-left one. When the table's direction is not set the document's
/// <see cref="PdfDocumentOptions.Direction"/> is used, and <see cref="TextDirection.Auto"/> there means left to right for the order of the columns (the text of each paragraph still finds its own direction). A table whose direction is set to
/// left-to-right or right-to-left also sets that direction for its paragraphs that do not name one.</para>
/// <para><b>Copied and live.</b> <see cref="Columns"/> and <see cref="Header"/> are copied when the table is made. <see cref="Rows"/> is kept as given and is live: a list that is changed after the table was added changes what is saved.</para>
/// <para>Two tables are equal when their columns and header rows (one by one) and settings are equal and their <see cref="Rows"/> are the same object: a sequence of rows is not compared element by element, because that would
/// enumerate it. Two tables with separate row lists that hold equal rows are therefore not equal.</para>
/// </remarks>
public sealed record Table : Block
{
    private Borders _borders = Borders.None;
    private Margins _cellPadding = Margins.Pt(3, 5, 3, 5);
    private TextDirection? _direction;
    private Length? _spaceBefore;
    private Length? _spaceAfter;
    private int? _maxRows;

    /// <summary>A table without header rows.</summary>
    /// <param name="columns">The columns, at least one.</param>
    /// <param name="rows">The rows, enumerated at save time (see the remarks).</param>
    public Table(IEnumerable<TableColumn> columns, IEnumerable<TableRow> rows)
        : this(columns, (IEnumerable<TableRow>)[], rows)
    {
    }

    /// <summary>A table with one header row.</summary>
    /// <param name="columns">The columns, at least one.</param>
    /// <param name="header">The header row.</param>
    /// <param name="rows">The rows, enumerated at save time (see the remarks).</param>
    public Table(IEnumerable<TableColumn> columns, TableRow header, IEnumerable<TableRow> rows)
        : this(columns, [header ?? throw new ArgumentNullException(nameof(header))], rows)
    {
    }

    /// <summary>A table with header rows.</summary>
    /// <param name="columns">The columns, at least one.</param>
    /// <param name="header">The header rows (they are held, so they should be few).</param>
    /// <param name="rows">The rows, enumerated at save time (see the remarks).</param>
    /// <exception cref="ArgumentException">There is no column, a column, header row or cell is null, or a header row has more cells than there are columns.</exception>
    public Table(IEnumerable<TableColumn> columns, IEnumerable<TableRow> header, IEnumerable<TableRow> rows)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(rows);
        Columns = new ReadOnlyCollection<TableColumn>([.. columns]);
        if (Columns.Count == 0 || Columns.Any(c => c is null))
        {
            throw new ArgumentException("A table has at least one column, and none of them is null.", nameof(columns));
        }

        Header = new ReadOnlyCollection<TableRow>([.. header]);
        foreach (TableRow row in Header)
        {
            if (row is null)
            {
                throw new ArgumentException("A header row is null.", nameof(header));
            }

            if (row.Cells.Count > Columns.Count)
            {
                throw new ArgumentException($"A header row has {row.Cells.Count} cells and the table {Columns.Count} columns.", nameof(header));
            }
        }

        Rows = rows;
    }

    /// <summary>The columns.</summary>
    public IReadOnlyList<TableColumn> Columns { get; }

    /// <summary>The header rows, drawn first and, when <see cref="RepeatHeader"/> is set, again at the top of every page the table continues on.</summary>
    public IReadOnlyList<TableRow> Header { get; }

    /// <summary>The rows, enumerated once for each save and never held whole (see the remarks of <see cref="Table"/>).</summary>
    public IEnumerable<TableRow> Rows { get; }

    /// <summary>Whether the header rows are repeated on every page the table continues on; <see langword="true"/> by default. A header that is taller than half a page is not repeated.</summary>
    public bool RepeatHeader { get; init; } = true;

    /// <summary>The lines around and between cells; <see cref="Borders.None"/> by default.</summary>
    /// <exception cref="ArgumentNullException">The value is null.</exception>
    public Borders Borders
    {
        get => _borders;
        init => _borders = value ?? throw new ArgumentNullException(nameof(Borders));
    }

    /// <summary>The fill of the header rows, or <see langword="null"/> for none.</summary>
    public Color? HeaderFill { get; init; }

    /// <summary>The fill of every second body row (the second, fourth and so on, counted over the pages), or <see langword="null"/> for none.</summary>
    public Color? ZebraFill { get; init; }

    /// <summary>The padding of cells that give none of their own; 3 points above and below and 5 at the sides by default.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A side is negative or not finite.</exception>
    public Margins CellPadding
    {
        get => _cellPadding;
        init => _cellPadding = Check.Padding(value, nameof(CellPadding));
    }

    /// <summary>A style for the text of every cell: it is over the document's default and under the cells' own styles and the paragraphs' own styles; <see langword="null"/> for none.</summary>
    public TextStyle? Style { get; init; }

    /// <summary>A style for the text of the header rows, over <see cref="Style"/>; bold by default.</summary>
    public TextStyle? HeaderStyle { get; init; } = new TextStyle { Weight = 700 };

    /// <summary>The direction of the table: the order of its columns and the base direction of its paragraphs that do not name one; <see langword="null"/> for the document's.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a <see cref="TextDirection"/>.</exception>
    public TextDirection? Direction
    {
        get => _direction;
        init => _direction = Check.Defined(value, nameof(Direction));
    }

    /// <summary>The space above the table, or <see langword="null"/> for none. It is dropped at the top of a page.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative or not finite.</exception>
    public Length? SpaceBefore
    {
        get => _spaceBefore;
        init => _spaceBefore = Check.Space(value, nameof(SpaceBefore));
    }

    /// <summary>The space below the table, or <see langword="null"/> for 0.6 of the document's font size. It is dropped at the bottom of a page.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative or not finite.</exception>
    public Length? SpaceAfter
    {
        get => _spaceAfter;
        init => _spaceAfter = Check.Space(value, nameof(SpaceAfter));
    }

    /// <summary>
    /// The most body rows this table draws, or <see langword="null"/> for no limit of its own. When the rows go on past it (one row more is read to find out), the rest are not drawn, and the table is listed in
    /// <see cref="DocumentDiagnostics.TruncatedTables"/>: the application decides what to say about the rows left out. (<see cref="DocumentLimits.MaxTableRows"/> is the limit that stops the save instead.)
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is less than 1.</exception>
    public int? MaxRows
    {
        get => _maxRows;
        init => _maxRows = value is < 1 ? throw new ArgumentOutOfRangeException(nameof(MaxRows), value, "A row limit is 1 or more.") : value;
    }

    /// <summary>Whether another table has the same columns, header rows (compared one by one) and settings, and the same <see cref="Rows"/> object.</summary>
    /// <param name="other">The other table.</param>
    /// <returns><see langword="true"/> when it does.</returns>
    public bool Equals(Table? other) =>
        other is not null
        && Columns.SequenceEqual(other.Columns)
        && Header.SequenceEqual(other.Header)
        && ReferenceEquals(Rows, other.Rows)
        && RepeatHeader == other.RepeatHeader
        && Borders == other.Borders
        && HeaderFill == other.HeaderFill
        && ZebraFill == other.ZebraFill
        && CellPadding == other.CellPadding
        && Equals(Style, other.Style)
        && Equals(HeaderStyle, other.HeaderStyle)
        && Direction == other.Direction
        && SpaceBefore == other.SpaceBefore
        && SpaceAfter == other.SpaceAfter
        && MaxRows == other.MaxRows;

    /// <inheritdoc />
    public override int GetHashCode()
    {
        HashCode hash = new();
        foreach (TableColumn column in Columns)
        {
            hash.Add(column);
        }

        foreach (TableRow row in Header)
        {
            hash.Add(row);
        }

        hash.Add(Rows);
        hash.Add(RepeatHeader);
        hash.Add(Borders);
        hash.Add(HeaderFill);
        hash.Add(ZebraFill);
        hash.Add(CellPadding);
        hash.Add(Style);
        hash.Add(HeaderStyle);
        hash.Add(Direction);
        hash.Add(SpaceBefore);
        hash.Add(SpaceAfter);
        hash.Add(MaxRows);
        return hash.ToHashCode();
    }
}
