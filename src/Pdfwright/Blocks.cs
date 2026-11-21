using System.Collections.ObjectModel;

namespace Pdfwright;

/// <summary>
/// A piece of a document that flows down the page. The set of blocks is closed by an internal constructor and a check at run time: only the types of this library derive
/// from <see cref="Block"/>, and a type from another assembly fails when it is made. C# cannot check a <c>switch</c> over a class hierarchy for completeness, so the compiler
/// does not guarantee that the layout handles every block; its <c>switch</c> throws for a block it does not know (which only a block added to the library and not handled there could be).
/// Blocks are immutable, and the same block may be added to any number of documents.
/// </summary>
public abstract record Block
{
    internal Block()
    {
        RequireLibraryType();
    }

    // The copy constructor of a record has to be protected, so a type outside the library could reach it; the check makes such a type fail when it is made.
    /// <summary>Copies a block; only the library's own blocks can be made.</summary>
    /// <param name="original">The block to copy.</param>
    protected Block(Block original)
    {
        RequireLibraryType();
    }

    private void RequireLibraryType()
    {
        if (GetType().Assembly != typeof(Block).Assembly)
        {
            throw new InvalidOperationException("The set of blocks is closed: Block cannot be derived from outside the Pdfwright assembly.");
        }
    }
}

/// <summary>A stretch of text in a paragraph or a heading, with the style that differs from the text around it.</summary>
public sealed record TextRun
{
    /// <summary>A run of text in the style of its paragraph.</summary>
    /// <param name="text">The text. A line feed, carriage return, or paragraph separator inside it is a line break.</param>
    public TextRun(string text)
        : this(text, null)
    {
    }

    /// <summary>A run of text with a style that overrides the paragraph's for the properties it sets.</summary>
    /// <param name="text">The text. A line feed, carriage return, or paragraph separator inside it is a line break.</param>
    /// <param name="style">The style of the run; its unset properties come from the paragraph, <see langword="null"/> for none.</param>
    public TextRun(string text, TextStyle? style)
    {
        ArgumentNullException.ThrowIfNull(text);
        Text = text;
        Style = style;
    }

    /// <summary>The text of the run.</summary>
    public string Text { get; }

    /// <summary>The style of the run, or <see langword="null"/> to take the paragraph's.</summary>
    public TextStyle? Style { get; }
}

/// <summary>
/// A paragraph: one or more runs of text, laid out in lines that break at the width of the page's content area, at the opportunities of
/// the Unicode line breaking algorithm. A paragraph that does not fit on the page is split between its lines. Two paragraphs are equal when their runs
/// (one by one) and their settings are equal.
/// </summary>
public sealed record Paragraph : Block
{
    private TextAlignment _alignment = TextAlignment.Start;
    private TextDirection? _direction;
    private Length? _spaceBefore;
    private Length? _spaceAfter;
    private double _lineSpacing = 1.0;

    /// <summary>A paragraph of one run in the document's default style.</summary>
    /// <param name="text">The text.</param>
    public Paragraph(string text)
        : this([new TextRun(text)])
    {
    }

    /// <summary>A paragraph of several runs, each of which may differ in style (a bold word, a coloured number) within the same lines.</summary>
    /// <param name="runs">The runs, in logical order.</param>
    public Paragraph(IEnumerable<TextRun> runs)
    {
        ArgumentNullException.ThrowIfNull(runs);
        Runs = new ReadOnlyCollection<TextRun>([.. runs]);
        if (Runs.Any(r => r is null))
        {
            throw new ArgumentException("A run is null.", nameof(runs));
        }
    }

    /// <summary>The runs of the paragraph.</summary>
    public IReadOnlyList<TextRun> Runs { get; }

    /// <summary>The style that applies to every run of the paragraph before the run's own style, or <see langword="null"/> for the document's default.</summary>
    public TextStyle? Style { get; init; }

    /// <summary>Where the lines sit in the width: <see cref="TextAlignment.Start"/> is the left edge in a left-to-right paragraph and the right edge in a right-to-left one.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a <see cref="TextAlignment"/>.</exception>
    public TextAlignment Alignment
    {
        get => _alignment;
        init => _alignment = Check.Defined(value, nameof(Alignment));
    }

    /// <summary>The base direction of this paragraph, or <see langword="null"/> to take the document's <see cref="PdfDocumentOptions.Direction"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a <see cref="TextDirection"/>.</exception>
    public TextDirection? Direction
    {
        get => _direction;
        init => _direction = Check.Defined(value, nameof(Direction));
    }

    /// <summary>The space above the paragraph, or <see langword="null"/> for none. It is dropped at the top of a page.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative or not finite.</exception>
    public Length? SpaceBefore
    {
        get => _spaceBefore;
        init => _spaceBefore = Check.Space(value, nameof(SpaceBefore));
    }

    /// <summary>The space below the paragraph, or <see langword="null"/> for the default of 0.6 of the paragraph's font size. It is dropped at the bottom of a page.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative or not finite.</exception>
    public Length? SpaceAfter
    {
        get => _spaceAfter;
        init => _spaceAfter = Check.Space(value, nameof(SpaceAfter));
    }

    /// <summary>A multiplier of the natural line height (the font's ascent plus descent); 1 by default.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a finite number greater than zero.</exception>
    public double LineSpacing
    {
        get => _lineSpacing;
        init => _lineSpacing = Check.LineSpacing(value, nameof(LineSpacing));
    }

    /// <summary>Whether another paragraph has the same runs (compared one by one), style and settings.</summary>
    /// <param name="other">The other paragraph.</param>
    /// <returns><see langword="true"/> when it does.</returns>
    public bool Equals(Paragraph? other) =>
        other is not null
        && Runs.SequenceEqual(other.Runs)
        && Equals(Style, other.Style)
        && Alignment == other.Alignment
        && Direction == other.Direction
        && SpaceBefore == other.SpaceBefore
        && SpaceAfter == other.SpaceAfter
        && LineSpacing.Equals(other.LineSpacing);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        HashCode hash = new();
        foreach (TextRun run in Runs)
        {
            hash.Add(run);
        }

        hash.Add(Style);
        hash.Add(Alignment);
        hash.Add(Direction);
        hash.Add(SpaceBefore);
        hash.Add(SpaceAfter);
        hash.Add(LineSpacing);
        return hash.ToHashCode();
    }
}

/// <summary>
/// A heading of level 1 (the largest) to 6. Its size and weight come from <see cref="PdfDocumentOptions.HeadingStyles"/> or the default scale, and a heading is kept
/// with the first line of the block that follows it: it is never left alone at the bottom of a page. Two headings are equal when their level, runs
/// (one by one) and settings are equal.
/// </summary>
public sealed record Heading : Block
{
    private TextAlignment _alignment = TextAlignment.Start;
    private TextDirection? _direction;
    private Length? _spaceBefore;
    private Length? _spaceAfter;

    /// <summary>A heading of one run.</summary>
    /// <param name="level">The level, 1 to 6.</param>
    /// <param name="text">The text.</param>
    /// <exception cref="ArgumentOutOfRangeException">The level is not from 1 to 6.</exception>
    public Heading(int level, string text)
        : this(level, [new TextRun(text)])
    {
    }

    /// <summary>A heading of several runs.</summary>
    /// <param name="level">The level, 1 to 6.</param>
    /// <param name="runs">The runs, in logical order.</param>
    /// <exception cref="ArgumentOutOfRangeException">The level is not from 1 to 6.</exception>
    public Heading(int level, IEnumerable<TextRun> runs)
    {
        ArgumentNullException.ThrowIfNull(runs);
        if (level is < 1 or > 6)
        {
            throw new ArgumentOutOfRangeException(nameof(level), level, "A heading's level is from 1 to 6.");
        }

        Level = level;
        Runs = new ReadOnlyCollection<TextRun>([.. runs]);
        if (Runs.Any(r => r is null))
        {
            throw new ArgumentException("A run is null.", nameof(runs));
        }
    }

    /// <summary>The level, 1 to 6.</summary>
    public int Level { get; }

    /// <summary>The runs of the heading.</summary>
    public IReadOnlyList<TextRun> Runs { get; }

    /// <summary>Where the lines sit in the width (logical: start, centre or end).</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a <see cref="TextAlignment"/>.</exception>
    public TextAlignment Alignment
    {
        get => _alignment;
        init => _alignment = Check.Defined(value, nameof(Alignment));
    }

    /// <summary>The base direction of this heading, or <see langword="null"/> to take the document's <see cref="PdfDocumentOptions.Direction"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a <see cref="TextDirection"/>.</exception>
    public TextDirection? Direction
    {
        get => _direction;
        init => _direction = Check.Defined(value, nameof(Direction));
    }

    /// <summary>The space above the heading, or <see langword="null"/> for the default (the heading's font size). It is dropped at the top of a page.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative or not finite.</exception>
    public Length? SpaceBefore
    {
        get => _spaceBefore;
        init => _spaceBefore = Check.Space(value, nameof(SpaceBefore));
    }

    /// <summary>The space below the heading, or <see langword="null"/> for the default (0.4 of the heading's font size).</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative or not finite.</exception>
    public Length? SpaceAfter
    {
        get => _spaceAfter;
        init => _spaceAfter = Check.Space(value, nameof(SpaceAfter));
    }

    /// <summary>Whether another heading has the same level, runs (compared one by one) and settings.</summary>
    /// <param name="other">The other heading.</param>
    /// <returns><see langword="true"/> when it does.</returns>
    public bool Equals(Heading? other) =>
        other is not null
        && Level == other.Level
        && Runs.SequenceEqual(other.Runs)
        && Alignment == other.Alignment
        && Direction == other.Direction
        && SpaceBefore == other.SpaceBefore
        && SpaceAfter == other.SpaceAfter;

    /// <inheritdoc />
    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(Level);
        foreach (TextRun run in Runs)
        {
            hash.Add(run);
        }

        hash.Add(Alignment);
        hash.Add(Direction);
        hash.Add(SpaceBefore);
        hash.Add(SpaceAfter);
        return hash.ToHashCode();
    }
}

/// <summary>
/// Empty vertical space. A spacer is always honoured, at the top of a page too: at the start of the document and after a <see cref="PageBreak"/> it moves what follows down
/// (only the space before a paragraph or a heading is dropped at the top of a page), and what does not fit at the bottom of a page is cut off, not carried over.
/// </summary>
public sealed record Spacer : Block
{
    /// <summary>A space of the given height.</summary>
    /// <param name="height">The height, not negative and finite.</param>
    /// <exception cref="ArgumentOutOfRangeException">The height is negative or not a finite number.</exception>
    public Spacer(Length height)
    {
        if (!height.IsFinite || height < Length.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(height), height, "A spacer's height is zero or more and finite.");
        }

        Height = height;
    }

    /// <summary>The height of the space.</summary>
    public Length Height { get; }
}

/// <summary>Ends the page: what follows starts on a new page. Two page breaks in a row make a blank page, and a break at the very start makes the first page blank.</summary>
public sealed record PageBreak : Block;
