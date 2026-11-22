using System.Collections.ObjectModel;

namespace Pdfwright;

/// <summary>What stands before each item of an <see cref="ItemList"/>.</summary>
public enum ListMarker
{
    /// <summary>A bullet, U+2022, before every item. When no font of the collection has U+2022 (the Arabic and Hebrew fonts do not), the first of U+25CF, U+2219, U+00B7 and the hyphen-minus that a font has is used instead.</summary>
    Bullet,

    /// <summary>The numbers 1, 2, 3 ... in the digits 0 to 9, each followed by a full stop.</summary>
    Decimal,

    /// <summary>The numbers in the Arabic-Indic digits (U+0660 to U+0669), each followed by a full stop. The font has to have them: the Arabic fonts do.</summary>
    ArabicIndicDecimal,

    /// <summary>The letters a, b, c ... z, aa, ab ... each followed by a full stop.</summary>
    LowerAlpha,

    /// <summary>The letters A, B, C ... Z, AA, AB ... each followed by a full stop.</summary>
    UpperAlpha,
}

/// <summary>
/// One item of an <see cref="ItemList"/>: a <see cref="Paragraph"/> that the marker belongs to, then any more paragraphs of the item, which sit under its text, and nested lists (one level deep at most).
/// Two items are equal when their blocks (one by one) are equal.
/// </summary>
public sealed record ListItem
{
    /// <summary>An item of one paragraph in the list's style.</summary>
    /// <param name="text">The text. A line break of any kind inside it (line feed, carriage return, U+0085, vertical tab, form feed, U+2028, U+2029) is a hard line break in the same block, never a new paragraph; other control characters are removed and a surrogate without its pair is drawn as U+FFFD. The string is kept as given.</param>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
    public ListItem(string text)
        : this(new Paragraph(text))
    {
    }

    /// <summary>An item of one block.</summary>
    /// <param name="content">The block, a <see cref="Paragraph"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="content"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The block is not a paragraph.</exception>
    public ListItem(Block content)
        : this(new[] { content ?? throw new ArgumentNullException(nameof(content)) })
    {
    }

    /// <summary>An item of several blocks.</summary>
    /// <param name="content">The blocks in order: the first is a <see cref="Paragraph"/> (the marker stands before it), the others paragraphs or an <see cref="ItemList"/> (nested one level deep at most).</param>
    /// <exception cref="ArgumentNullException"><paramref name="content"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">There is no block, a block is null, the first is not a paragraph, one is neither a paragraph nor a list, or a nested list holds a list of its own.</exception>
    public ListItem(IEnumerable<Block> content)
    {
        ArgumentNullException.ThrowIfNull(content);
        Block[] copy = [.. content];
        if (copy.Length == 0)
        {
            throw new ArgumentException("A list item holds at least one block.", nameof(content));
        }

        foreach (Block block in copy)
        {
            if (block is null)
            {
                throw new ArgumentException("A block is null.", nameof(content));
            }

            if (block is not (Paragraph or ItemList))
            {
                throw new ArgumentException($"A {block.GetType().Name} cannot be in a list item: an item holds paragraphs and a nested list.", nameof(content));
            }

            if (block is ItemList { Depth: > 1 })
            {
                throw new ArgumentException("A list inside a list item cannot hold a list of its own: lists nest one level deep.", nameof(content));
            }
        }

        if (copy[0] is not Paragraph)
        {
            throw new ArgumentException("The first block of a list item is a paragraph: the marker stands before it.", nameof(content));
        }

        Content = new ReadOnlyCollection<Block>(copy);
    }

    /// <summary>The blocks of the item.</summary>
    public IReadOnlyList<Block> Content { get; }

    /// <summary>Whether another item has the same blocks (compared one by one).</summary>
    /// <param name="other">The other item.</param>
    /// <returns><see langword="true"/> when it does.</returns>
    public bool Equals(ListItem? other) => other is not null && Content.SequenceEqual(other.Content);

    /// <inheritdoc />
    public override int GetHashCode() => Nesting.HashBlocks(Content);
}

/// <summary>
/// A list: items, each after a marker (a bullet, a number or a letter). The lines of an item that wrap are aligned with the text of its first line, not with the marker (a hanging indent), and the marker stands
/// at the start edge: in a right-to-left item (the item's paragraph direction, else <see cref="Direction"/>, else the document's) it is at the right edge, with the text hanging to its left. The marker of a number is
/// set in the direction of its item, so that in a right-to-left item the full stop is to the left of the digits, where right-to-left readers expect it. Items are split between pages line by line, like paragraphs, and the marker stays with the first line of its item.
/// An item may hold more paragraphs and nested lists (one level deep: a list in a list may not hold a list). A list may be in a <see cref="Callout"/>, in a cell of <see cref="Columns"/>, or be a header or a footer. The first paragraph's space before and the last paragraph's space after are the larger of the list's and the paragraph's own.
/// The marker of a bullet is U+2022, or, when no font of the collection has it, the first of U+25CF, U+2219, U+00B7 and the hyphen-minus that a font has. A marker larger than the text makes the first line of its item taller. The name is not <c>List</c>, which
/// is <see cref="System.Collections.Generic.List{T}"/> in most files. Two lists are equal when their items (one by one) and settings are equal.
/// </summary>
public sealed record ItemList : Block
{
    private readonly ReadOnlyCollection<ListItem> _items;
    private ListMarker _marker = ListMarker.Bullet;
    private int _start = 1;
    private Length _indent = Length.Pt(12);
    private Length _markerGap = Length.Pt(6);
    private Length? _itemSpacing;
    private TextDirection? _direction;
    private Length? _spaceBefore;
    private Length? _spaceAfter;

    /// <summary>A list of items.</summary>
    /// <param name="items">The items, at least one.</param>
    /// <exception cref="ArgumentNullException"><paramref name="items"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">There is no item, an item is null, or an item holds a nested list that holds a list.</exception>
    public ItemList(IEnumerable<ListItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        ListItem[] copy = [.. items];
        if (copy.Length == 0)
        {
            throw new ArgumentException("A list holds at least one item.", nameof(items));
        }

        if (copy.Any(i => i is null))
        {
            throw new ArgumentException("An item is null.", nameof(items));
        }

        _items = new ReadOnlyCollection<ListItem>(copy);
        Depth = copy.SelectMany(i => i.Content).OfType<ItemList>().Select(l => l.Depth).DefaultIfEmpty(0).Max() + 1;
    }

    /// <summary>A list whose items are each a paragraph of one text.</summary>
    /// <param name="items">The texts, at least one.</param>
    /// <exception cref="ArgumentNullException"><paramref name="items"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">There is no text, or a text is null.</exception>
    public ItemList(IEnumerable<string> items)
        : this(ToItems(items))
    {
    }

    /// <summary>The items.</summary>
    public IReadOnlyList<ListItem> Items => _items;

    /// <summary>What stands before each item; a bullet by default.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a <see cref="ListMarker"/>.</exception>
    public ListMarker Marker
    {
        get => _marker;
        init => _marker = Check.Defined(value, nameof(Marker));
    }

    /// <summary>The number of the first item (the number or the place in the alphabet), from 1 to 1,000,000; 1 by default. It has no effect on bullets.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not from 1 to 1,000,000.</exception>
    public int Start
    {
        get => _start;
        init => _start = value is < 1 or > 1_000_000 ? throw new ArgumentOutOfRangeException(nameof(Start), value, "The first number of a list is from 1 to 1,000,000.") : value;
    }

    /// <summary>The space between the start edge of the list (of the text of the item a nested list is in) and the marker; 12 points by default. In a right-to-left item the start edge is the right one.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative or not finite.</exception>
    public Length Indent
    {
        get => _indent;
        init => _indent = Check.Space(value, nameof(Indent)) ?? Length.Zero;
    }

    /// <summary>The space between the widest marker of the list and the text; 6 points by default. The text of every item starts at the same place, whatever the width of its marker.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative or not finite.</exception>
    public Length MarkerGap
    {
        get => _markerGap;
        init => _markerGap = Check.Space(value, nameof(MarkerGap)) ?? Length.Zero;
    }

    /// <summary>The space below each paragraph of an item that has no space of its own, so between two items; <see langword="null"/> for 0.3 of the font size.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative or not finite.</exception>
    public Length? ItemSpacing
    {
        get => _itemSpacing;
        init => _itemSpacing = Check.Space(value, nameof(ItemSpacing));
    }

    /// <summary>The style that applies to the paragraphs and the markers of the list before the paragraph's own style, or <see langword="null"/> for the document's default.</summary>
    public TextStyle? Style { get; init; }

    /// <summary>The style of the markers over <see cref="Style"/> and the first paragraph's style (a colour or a weight for the numbers), or <see langword="null"/> for none. A size that differs from the text's puts the marker on the baseline of the first line; a larger marker makes that line taller (above and below the baseline) so that it does not reach into the text above.</summary>
    public TextStyle? MarkerStyle { get; init; }

    /// <summary>The base direction of the paragraphs of the list that set none, and so the side the marker is on. <see langword="null"/> takes the document's (or the enclosing <see cref="Columns"/> or <see cref="Callout"/>'s); <see cref="TextDirection.Auto"/> lets each item's text decide.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a <see cref="TextDirection"/>.</exception>
    public TextDirection? Direction
    {
        get => _direction;
        init => _direction = Check.Defined(value, nameof(Direction));
    }

    /// <summary>The space above the list, or <see langword="null"/> for none. It is dropped at the top of a page.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative or not finite.</exception>
    public Length? SpaceBefore
    {
        get => _spaceBefore;
        init => _spaceBefore = Check.Space(value, nameof(SpaceBefore));
    }

    /// <summary>The space below the list, or <see langword="null"/> for the default of 0.6 of the list's font size (<see cref="Style"/> over the document's; for a nested list, the space between items). It is dropped at the bottom of a page.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative or not finite.</exception>
    public Length? SpaceAfter
    {
        get => _spaceAfter;
        init => _spaceAfter = Check.Space(value, nameof(SpaceAfter));
    }

    /// <summary>1 for a list without a nested list, 2 for one with a list nested in an item.</summary>
    internal int Depth { get; }

    /// <summary>Whether another list has the same items (one by one) and settings.</summary>
    /// <param name="other">The other list.</param>
    /// <returns><see langword="true"/> when it does.</returns>
    public bool Equals(ItemList? other) =>
        other is not null
        && _items.SequenceEqual(other._items)
        && Marker == other.Marker
        && Start == other.Start
        && Indent == other.Indent
        && MarkerGap == other.MarkerGap
        && ItemSpacing == other.ItemSpacing
        && Equals(Style, other.Style)
        && Equals(MarkerStyle, other.MarkerStyle)
        && Direction == other.Direction
        && SpaceBefore == other.SpaceBefore
        && SpaceAfter == other.SpaceAfter;

    /// <inheritdoc />
    public override int GetHashCode()
    {
        HashCode hash = new();
        foreach (ListItem item in _items)
        {
            hash.Add(item);
        }

        hash.Add(Marker);
        hash.Add(Start);
        hash.Add(Indent);
        hash.Add(MarkerGap);
        hash.Add(ItemSpacing);
        hash.Add(Style);
        hash.Add(MarkerStyle);
        hash.Add(Direction);
        hash.Add(SpaceBefore);
        hash.Add(SpaceAfter);
        return hash.ToHashCode();
    }

    private static IEnumerable<ListItem> ToItems(IEnumerable<string> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return [.. items.Select(t => new ListItem(t ?? throw new ArgumentException("A text is null.", nameof(items))))];
    }
}
