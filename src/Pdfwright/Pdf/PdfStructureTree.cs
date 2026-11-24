namespace Pdfwright.Pdf;

/// <summary>
/// One element of the structure tree of a tagged PDF (ISO 32000-1, 14.7.2): a paragraph, a heading, a table cell. It is made light, with its type,
/// its parent and what it says of itself (<c>/Alt</c>, <c>/Lang</c>, attributes), and has no object until it is <b>materialised</b>: when the first
/// content marked for it is drawn (or <see cref="PdfStructureTree.Open"/> asks for it, for an element with no content of its own). Its object is
/// written when it <b>closes</b>, so the writer holds the open elements, one branch of the tree, and not the tree.
/// </summary>
/// <remarks>
/// <para>An element that is never materialised is not in the file. An element that is closed cannot be added to again: its object is written.</para>
/// <para>
/// A tagged table has several elements for each of its rows, so an element is made to cost little: its object number, its page and its kids are
/// numbers held in the element (the first kid in a field, the others in an array the tree lends it while it is open), and no PDF object is built
/// for it; the tree writes its dictionary straight as bytes. Once written, nothing refers to an element but by its number, so a closed element can
/// be made again for another place (<see cref="PdfStructureTree.Reuse"/>).
/// </para>
/// </remarks>
internal sealed class PdfStructureElement
{
    internal PdfStructureElement(PdfStructureTree tree, PdfStructureElement? parent, string type, string? alt, string? language, PdfDictionary? attributes)
    {
        PdfStructureTree.CheckType(type);
        Tree = tree;
        Parent = parent;
        Type = type;
        Alt = alt;
        Language = language;
        _attributes = attributes;
    }

    /// <summary>The tree the element belongs to.</summary>
    internal PdfStructureTree Tree { get; }

    /// <summary>The element it is a child of, or <see langword="null"/> for the tree's <c>Document</c> element, whose parent is the tree's root.</summary>
    internal PdfStructureElement? Parent { get; private set; }

    /// <summary>The structure type (<c>/S</c>), a standard type such as <c>P</c> or <c>TD</c>; it is also the tag of the element's marked content.</summary>
    internal string Type { get; private set; }

    /// <summary>The alternative text (<c>/Alt</c>), for a figure.</summary>
    internal string? Alt { get; private set; }

    /// <summary>The element's language (<c>/Lang</c>), a BCP 47 tag, when it is not the language of the element around it.</summary>
    internal string? Language { get; private set; }

    /// <summary>
    /// The attribute dictionary (<c>/A</c>), such as <c>&lt;&lt;/O /Table /Scope /Column&gt;&gt;</c>. It may be set late, until the element closes:
    /// a figure's bounding box is known only when the figure is drawn. Once the element's object is written, neither the getter nor the setter
    /// can be used: the dictionary is mutable, and a change made to it then would be lost without a word, so it is not handed out at all.
    /// </summary>
    internal PdfDictionary? Attributes
    {
        get
        {
            ThrowIfClosed();
            return _attributes;
        }

        set
        {
            ThrowIfClosed();
            _attributes = value;
        }
    }

    private PdfDictionary? _attributes;

    private void ThrowIfClosed()
    {
        if (IsClosed)
        {
            throw new InvalidOperationException($"The {Type} element is closed and its object is written: its attributes cannot be read or changed.");
        }
    }

    /// <summary>The element's object number from the moment it is materialised, or 0 before.</summary>
    internal int Number { get; set; }

    /// <summary>The element's object, from the moment it is materialised (a new reference to <see cref="Number"/> at each call), or <see langword="null"/> before.</summary>
    internal PdfReference? Reference => Number == 0 ? null : new PdfReference(Number);

    /// <summary>
    /// The object number of the page of the element's first marked content (<c>/Pg</c>), or 0 when it has none: its marked content on that page is an
    /// <c>MCID</c> in <c>/K</c>, on another page a marked-content reference.
    /// </summary>
    internal int Page { get; set; }

    /// <summary>The number of kids recorded while the element is open: <c>MCID</c>s, marked-content references and child elements.</summary>
    internal int KidCount { get; set; }

    /// <summary>The first kid, held in the element: most elements have one (a cell's text, a paragraph on one page).</summary>
    internal PdfStructureKid FirstKid { get; set; }

    /// <summary>The kids after the first, in an array the tree lends the element while it is open (<see cref="KidCount"/> - 1 of them are used).</summary>
    internal PdfStructureKid[]? MoreKids { get; set; }

    /// <summary>The child that is open now, which is closed before another child is added.</summary>
    internal PdfStructureElement? OpenChild { get; set; }

    /// <summary>
    /// Whether the element's children stay open together until it closes, instead of each closing when the next begins: a table row split between
    /// pages draws a part of each of its cells on each page, so a cell's content comes back after the next cell's. Set it before the element has
    /// children. A row has as many children as cells, so the memory is still the depth of the tree (times the columns), not its size.
    /// </summary>
    internal bool KeepsChildrenOpen { get; set; }

    /// <summary>The children materialised while <see cref="KeepsChildrenOpen"/>, in order (a list the tree lends); they are closed when the element closes.</summary>
    internal List<PdfStructureElement>? OpenChildren { get; set; }

    /// <summary>The element's object is written.</summary>
    internal bool IsClosed { get; set; }

    /// <summary>The element has an object and is not closed yet.</summary>
    internal bool IsOpen => Number != 0 && !IsClosed;

    /// <summary>Makes a closed element light again, under <paramref name="parent"/>, as the constructor makes a new one.</summary>
    internal void Reset(PdfStructureElement parent, string type, string? alt, string? language, PdfDictionary? attributes)
    {
        PdfStructureTree.CheckType(type);
        Parent = parent;
        Type = type;
        Alt = alt;
        Language = language;
        _attributes = attributes;
        Number = 0;
        Page = 0;
        KidCount = 0;
        FirstKid = default;
        MoreKids = null;
        OpenChild = null;
        KeepsChildrenOpen = false;
        OpenChildren = null;
        IsClosed = false;
    }
}

/// <summary>
/// A kid of a structure element, as numbers: an <c>MCID</c> on the element's own page (<see cref="Page"/> 0, <see cref="Value"/> the <c>MCID</c>), a
/// marked-content reference to an <c>MCID</c> on another page (<see cref="Page"/> that page's object number), or a child element
/// (<see cref="Page"/> <see cref="ChildPage"/>, <see cref="Value"/> the child's object number).
/// </summary>
internal readonly record struct PdfStructureKid(int Page, int Value)
{
    /// <summary>The <see cref="Page"/> of a kid that is a child element.</summary>
    internal const int ChildPage = -1;
}

/// <summary>
/// The structure tree of a tagged PDF (ISO 32000-1, 14.7), written as the pages are: a <c>Document</c> element at the top, elements made when their
/// content is first drawn, so that the order of the tree is the drawing order, which is the reading order, and written when they close; then, when
/// the document is completed, the <c>StructTreeRoot</c> and its parent tree.
/// </summary>
/// <remarks>
/// <para>
/// <b>Opening and closing.</b> An element is materialised under its parent, which is materialised first if it is not yet. When a parent gets a
/// new child, the child it had open is closed, and with it that child's own open subtree: the content of a document arrives in reading order, so
/// a sibling that starts means the one before it is done; a table row keeps its cells open together instead
/// (<see cref="PdfStructureElement.KeepsChildrenOpen"/>), because a row split between pages returns to each cell on the next page. <see cref="Close"/> closes an element (and its open subtree) explicitly, and
/// <see cref="Complete"/> closes what is left. So the memory is the depth of the tree, not its size.
/// </para>
/// <para>
/// <b>The parent tree</b> is flat: one array per page under the page's <c>/StructParents</c> key, the element of each <c>MCID</c> of the page in
/// order, written as its own object when the page's body ends. The keys are given in page order from a counter, which leaves the keys after the
/// pages for what else the parent tree will hold (a link annotation's <c>/StructParent</c>).
/// </para>
/// <para>
/// <b>Cost.</b> An element's dictionary is written straight as the bytes the serializer writes for a <see cref="PdfDictionary"/> of the same
/// entries (the same keys in the same order, the same spacing), with no dictionary, array, name or reference made for it, and the arrays and lists
/// an open element borrows go back to the tree when it closes: a long tagged table costs its elements, not their PDF objects.
/// </para>
/// <para>
/// <b>Size.</b> In a compressed file the elements, the parent tree's arrays and the <c>StructTreeRoot</c> are written in compressed object streams
/// (<see cref="PdfFile.WritePacked(int, ReadOnlySpan{byte})"/>), not one by one: the 1,000-row table of the benchmarks went from 794 KB to 186 KB.
/// An uncompressed file has them as separate objects, as before.
/// </para>
/// <para>Object numbers are given in the order things happen, so the same document is the same file.</para>
/// </remarks>
internal sealed class PdfStructureTree
{
    /// <summary>The sizes of the arrays of kids kept for reuse: 4, 8, ... 128 kids.</summary>
    private const int KeptKidSizes = 6;

    private readonly PdfDocumentWriter _writer;
    private readonly List<PdfStructureElement> _stack = [];
    private readonly PdfArray _nums = new();

    /// <summary>The bytes of the element being written, kept from one element to the next.</summary>
    private readonly MemoryStream _scratch = new();

    /// <summary>The arrays of kids closed elements gave back, by size (4 &lt;&lt; index).</summary>
    private readonly Stack<PdfStructureKid[]>[] _spareKids = new Stack<PdfStructureKid[]>[KeptKidSizes];

    /// <summary>The lists of open children closed rows gave back.</summary>
    private readonly Stack<List<PdfStructureElement>> _spareChildren = new();
    private int _nextKey;
    private int _lastEntryKey = -1;
    private bool _completed;

    internal PdfStructureTree(PdfDocumentWriter writer)
    {
        _writer = writer;
        Root = writer.AllocateObject();
        Document = new PdfStructureElement(this, null, "Document", null, null, null);
    }

    /// <summary>The <c>StructTreeRoot</c>, which the catalog points at; its number is reserved when the tree is made.</summary>
    internal PdfReference Root { get; }

    /// <summary>The <c>Document</c> element, the root's only kid and the parent of everything else.</summary>
    internal PdfStructureElement Document { get; }

    /// <summary>The element <see cref="BeginElement"/> began last and <see cref="EndElement"/> has not ended, or <see cref="Document"/>.</summary>
    internal PdfStructureElement Current => _stack.Count > 0 ? _stack[^1] : Document;

    /// <summary>The next key of the parent tree: what is written as <c>/ParentTreeNextKey</c>.</summary>
    internal int NextKey => _nextKey;

    /// <summary>Makes a light element under <paramref name="parent"/>. It has no object until content is drawn for it or it is opened.</summary>
    internal PdfStructureElement Create(PdfStructureElement parent, string type, string? alt = null, string? language = null, PdfDictionary? attributes = null)
    {
        CheckParent(parent);
        return new PdfStructureElement(this, parent, type, alt, language, attributes);
    }

    /// <summary>
    /// Makes <paramref name="element"/>, which is closed (its object is written), a light element under <paramref name="parent"/> again, as
    /// <see cref="Create"/> makes a new one: a table makes each body row's elements from those of the row placed before it. The element's object is
    /// in the file, and its parent and the parent tree refer to it by number only, so the element itself is free; whoever made it must hold it no more
    /// for its old place.
    /// </summary>
    internal PdfStructureElement Reuse(PdfStructureElement element, PdfStructureElement parent, string type, string? alt = null, string? language = null, PdfDictionary? attributes = null)
    {
        ArgumentNullException.ThrowIfNull(element);
        CheckParent(parent);
        if (!ReferenceEquals(element.Tree, this))
        {
            throw new ArgumentException("The element belongs to another document.", nameof(element));
        }

        if (!element.IsClosed)
        {
            throw new InvalidOperationException($"The {element.Type} element is not written: only a closed element can be made again.");
        }

        element.Reset(parent, type, alt, language, attributes);
        return element;
    }

    private void CheckParent(PdfStructureElement parent)
    {
        ArgumentNullException.ThrowIfNull(parent);
        if (!ReferenceEquals(parent.Tree, this))
        {
            throw new ArgumentException("The parent belongs to another document.", nameof(parent));
        }
    }

    /// <summary>Makes a light element under <see cref="Current"/> and makes it the current one, until <see cref="EndElement"/>.</summary>
    internal PdfStructureElement BeginElement(string type, string? alt = null, string? language = null, PdfDictionary? attributes = null)
    {
        PdfStructureElement element = Create(Current, type, alt, language, attributes);
        _stack.Add(element);
        return element;
    }

    /// <summary>Closes the element <see cref="BeginElement"/> began last (writing it, if it was materialised) and makes its parent the current one again.</summary>
    internal void EndElement()
    {
        if (_stack.Count == 0)
        {
            throw new InvalidOperationException("No element was begun.");
        }

        PdfStructureElement element = _stack[^1];
        _stack.RemoveAt(_stack.Count - 1);
        Close(element);
    }

    /// <summary>Materialises an element that may have no content of its own (an empty table cell is still a cell), and its parents.</summary>
    internal void Open(PdfStructureElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (!ReferenceEquals(element.Tree, this))
        {
            throw new ArgumentException("The element belongs to another document.", nameof(element));
        }

        if (_completed)
        {
            throw new InvalidOperationException("The structure tree is complete.");
        }

        if (element.IsClosed)
        {
            throw new InvalidOperationException($"The {element.Type} element is closed and its object is written: content in reading order cannot return to it.");
        }

        if (element.Number != 0)
        {
            return;
        }

        if (element.Parent is { } parent)
        {
            Open(parent);
            if (parent.OpenChild is { } previous)
            {
                CloseElement(previous);
            }

            element.Number = _writer.AllocateNumber();
            AddKid(parent, new PdfStructureKid(PdfStructureKid.ChildPage, element.Number));
            if (parent.KeepsChildrenOpen)
            {
                (parent.OpenChildren ??= _spareChildren.Count > 0 ? _spareChildren.Pop() : []).Add(element);
            }
            else
            {
                parent.OpenChild = element;
            }
        }
        else
        {
            element.Number = _writer.AllocateNumber();
        }
    }

    /// <summary>
    /// Closes an element: its open child first, then its own object is written. An element that was never materialised stays out of the file; one
    /// already closed is left as it is. The <c>Document</c> element is closed by <see cref="Complete"/> only: closed before, it would leave the
    /// document's completion nothing to write the rest of the tree under, after the fonts and pages were written.
    /// </summary>
    internal void Close(PdfStructureElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (!ReferenceEquals(element.Tree, this))
        {
            throw new ArgumentException("The element belongs to another document.", nameof(element));
        }

        if (ReferenceEquals(element, Document))
        {
            throw new InvalidOperationException("The Document element is closed when the document is completed, not before.");
        }

        CloseElement(element);
    }

    private void CloseElement(PdfStructureElement element)
    {
        if (!element.IsOpen)
        {
            return;
        }

        CloseChildren(element);
        WriteElement(element);
        element.IsClosed = true;
        if (element.MoreKids is { } more)
        {
            element.MoreKids = null;
            ReturnKids(more);
        }

        element.KidCount = 0;
        element.FirstKid = default;
        if (element.Parent is { } parent && ReferenceEquals(parent.OpenChild, element))
        {
            parent.OpenChild = null;
        }
    }

    /// <summary>
    /// Writes the element's object as the serializer writes a <see cref="PdfDictionary"/> of the same entries: <c>/Type</c>, <c>/S</c> and
    /// <c>/P</c>, then <c>/Pg</c>, <c>/K</c>, <c>/Alt</c>, <c>/Lang</c> and <c>/A</c> when the element has them, each on its own line.
    /// </summary>
    private void WriteElement(PdfStructureElement element)
    {
        MemoryStream output = _scratch;
        output.SetLength(0);
        output.Write("<<\n/Type /StructElem\n/S "u8);
        PdfSerializer.WriteName(element.Type, output);
        output.Write("\n/P "u8);
        WriteReference(output, element.Parent?.Number ?? Root.Number);
        if (element.Page != 0)
        {
            output.Write("\n/Pg "u8);
            WriteReference(output, element.Page);
        }

        if (element.KidCount > 0)
        {
            output.Write("\n/K ["u8);
            WriteKid(output, element.FirstKid);
            for (int i = 0; i < element.KidCount - 1; i++)
            {
                output.WriteByte((byte)' ');
                WriteKid(output, element.MoreKids![i]);
            }

            output.WriteByte((byte)']');
        }

        if (element.Alt is { } alt)
        {
            output.Write("\n/Alt "u8);
            PdfSerializer.Write(PdfString.Text(PdfDocumentWriter.Clean(alt)), output);
        }

        if (element.Language is { } language)
        {
            output.Write("\n/Lang "u8);
            PdfSerializer.Write(PdfString.Text(language), output);
        }

        if (element.Attributes is { } attributes)
        {
            output.Write("\n/A "u8);
            PdfSerializer.Write(attributes, output);
        }

        output.Write("\n>>"u8);
        _writer.WritePackedObject(element.Number, output.GetBuffer().AsSpan(0, (int)output.Length));
    }

    /// <summary>A kid as the serializer writes it: an integer (<c>MCID</c>), a marked-content reference dictionary, or a reference to a child.</summary>
    private static void WriteKid(Stream output, PdfStructureKid kid)
    {
        switch (kid.Page)
        {
            case 0:
                PdfSerializer.Integer(output, kid.Value);
                break;
            case PdfStructureKid.ChildPage:
                WriteReference(output, kid.Value);
                break;
            default:
                output.Write("<<\n/Type /MCR\n/Pg "u8);
                WriteReference(output, kid.Page);
                output.Write("\n/MCID "u8);
                PdfSerializer.Integer(output, kid.Value);
                output.Write("\n>>"u8);
                break;
        }
    }

    /// <summary>An indirect reference, <c>n 0 R</c>, as the serializer writes a <see cref="PdfReference"/>.</summary>
    internal static void WriteReference(Stream output, int number)
    {
        PdfSerializer.Integer(output, number);
        output.Write(" 0 R"u8);
    }

    /// <summary>Adds a kid to an open element: the first in the element, the others in an array borrowed from the tree, which grows by doubling.</summary>
    private void AddKid(PdfStructureElement element, PdfStructureKid kid)
    {
        int count = element.KidCount;
        if (count == 0)
        {
            element.FirstKid = kid;
        }
        else
        {
            PdfStructureKid[]? more = element.MoreKids;
            if (more is null || count - 1 == more.Length)
            {
                PdfStructureKid[] grown = RentKids(more is null ? 4 : more.Length * 2);
                if (more is not null)
                {
                    more.AsSpan().CopyTo(grown);
                    ReturnKids(more);
                }

                element.MoreKids = more = grown;
            }

            more[count - 1] = kid;
        }

        element.KidCount = count + 1;
    }

    /// <summary>An array of <paramref name="length"/> kids (4 or a doubling of it), one a closed element gave back when there is one.</summary>
    private PdfStructureKid[] RentKids(int length)
    {
        int index = System.Numerics.BitOperations.Log2((uint)length) - 2;
        if (index < KeptKidSizes && _spareKids[index] is { Count: > 0 } spare)
        {
            return spare.Pop();
        }

        return new PdfStructureKid[length];
    }

    /// <summary>Keeps an array of kids for the next element that needs one of its size; the larger sizes are left to the collector.</summary>
    private void ReturnKids(PdfStructureKid[] kids)
    {
        int index = System.Numerics.BitOperations.Log2((uint)kids.Length) - 2;
        if (index < KeptKidSizes)
        {
            (_spareKids[index] ??= new Stack<PdfStructureKid[]>()).Push(kids);
        }
    }

    /// <summary>Closes the open children of an element: the one open now, or all those it keeps open (<see cref="PdfStructureElement.KeepsChildrenOpen"/>), in order.</summary>
    private void CloseChildren(PdfStructureElement element)
    {
        if (element.OpenChild is { } child)
        {
            CloseElement(child);
        }

        if (element.OpenChildren is { } children)
        {
            element.OpenChildren = null;
            foreach (PdfStructureElement open in children)
            {
                CloseElement(open);
            }

            children.Clear();
            _spareChildren.Push(children);
        }
    }

    /// <summary>
    /// Records marked content numbered <paramref name="mcid"/> on the page whose object number is <paramref name="page"/> as the next kid of
    /// <paramref name="element"/>, which is materialised if it is not yet: an <c>MCID</c> on the element's own page, a marked-content reference on another.
    /// </summary>
    internal void AddMarkedContent(PdfStructureElement element, int page, int mcid)
    {
        Open(element);

        // Content of the element itself after a child's content: the child is done.
        CloseChildren(element);

        if (element.Page == 0)
        {
            element.Page = page;
        }

        AddKid(element, new PdfStructureKid(element.Page == page ? 0 : page, mcid));
    }

    /// <summary>Gives a page its key in the parent tree (its <c>/StructParents</c>).</summary>
    internal int TakeKey() => _nextKey++;

    /// <summary>
    /// Records an entry of the parent tree: for a page, the array, already written, of the elements of its <c>MCID</c>s. A flat <c>/Nums</c> array
    /// is sorted by key (ISO 32000-1, 7.9.7), so the keys must arrive in increasing order; one out of order is a defect of the caller, refused here
    /// rather than written as a tree a reader searches wrongly.
    /// </summary>
    internal void AddEntry(int key, PdfReference elements)
    {
        if (key <= _lastEntryKey || key >= _nextKey)
        {
            throw new InvalidOperationException($"Parent-tree key {key} is out of order: the last one written is {_lastEntryKey} and the next to give is {_nextKey}.");
        }

        _lastEntryKey = key;
        _nums.Add(new PdfInteger(key)).Add(elements);
    }

    /// <summary>Closes every element still open (materialising the <c>Document</c> element if nothing was drawn) and writes the <c>StructTreeRoot</c>.</summary>
    internal void Complete()
    {
        if (_completed)
        {
            throw new InvalidOperationException("The structure tree is complete.");
        }

        _stack.Clear();
        Open(Document);
        CloseElement(Document);
        _completed = true;
        _writer.WritePackedObject(Root, new PdfDictionary()
            .Set("Type", "StructTreeRoot")
            .Set("K", new PdfReference(Document.Number))
            .Set("ParentTree", new PdfDictionary().Set("Nums", _nums))
            .Set("ParentTreeNextKey", _nextKey));
    }

    /// <summary>A structure type is a name and the tag of marked content, so it is held to the plain names a content stream takes.</summary>
    internal static void CheckType(string type)
    {
        ArgumentException.ThrowIfNullOrEmpty(type);
        foreach (char c in type)
        {
            if (c is not ((>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '.' or '-'))
            {
                throw new ArgumentException($"'{type}' is not a plain name: use letters, digits, underscore, dot and hyphen.", nameof(type));
            }
        }
    }
}
