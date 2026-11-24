namespace Pdfwright.Pdf;

/// <summary>
/// One element of the structure tree of a tagged PDF (ISO 32000-1, 14.7.2): a paragraph, a heading, a table cell. It is made light, with its type,
/// its parent and what it says of itself (<c>/Alt</c>, <c>/Lang</c>, attributes), and has no object until it is <b>materialised</b>: when the first
/// content marked for it is drawn (or <see cref="PdfStructureTree.Open"/> asks for it, for an element with no content of its own). Its object is
/// written when it <b>closes</b>, so the writer holds the open elements, one branch of the tree, and not the tree.
/// </summary>
/// <remarks>
/// An element that is never materialised is not in the file. An element that is closed cannot be added to again: its object is written.
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
    internal PdfStructureElement? Parent { get; }

    /// <summary>The structure type (<c>/S</c>), a standard type such as <c>P</c> or <c>TD</c>; it is also the tag of the element's marked content.</summary>
    internal string Type { get; }

    /// <summary>The alternative text (<c>/Alt</c>), for a figure.</summary>
    internal string? Alt { get; }

    /// <summary>The element's language (<c>/Lang</c>), a BCP 47 tag, when it is not the language of the element around it.</summary>
    internal string? Language { get; }

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

    /// <summary>The element's object number, from the moment it is materialised.</summary>
    internal PdfReference? Reference { get; set; }

    /// <summary>The page of the element's first marked content (<c>/Pg</c>): its marked content on that page is an <c>MCID</c> in <c>/K</c>, on another page a marked-content reference.</summary>
    internal PdfReference? Page { get; set; }

    /// <summary>The element's kids in order, while it is open: <c>MCID</c>s, marked-content references and the references of child elements.</summary>
    internal List<PdfObject>? Kids { get; set; }

    /// <summary>The child that is open now, which is closed before another child is added.</summary>
    internal PdfStructureElement? OpenChild { get; set; }

    /// <summary>The element's object is written.</summary>
    internal bool IsClosed { get; set; }

    /// <summary>The element has an object and is not closed yet.</summary>
    internal bool IsOpen => Reference is not null && !IsClosed;
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
/// a sibling that starts means the one before it is done. <see cref="Close"/> closes an element (and its open subtree) explicitly, and
/// <see cref="Complete"/> closes what is left. So the memory is the depth of the tree, not its size.
/// </para>
/// <para>
/// <b>The parent tree</b> is flat: one array per page under the page's <c>/StructParents</c> key, the element of each <c>MCID</c> of the page in
/// order, written as its own object when the page's body ends. The keys are given in page order from a counter, which leaves the keys after the
/// pages for what else the parent tree will hold (a link annotation's <c>/StructParent</c>).
/// </para>
/// <para>Object numbers are given in the order things happen, so the same document is the same file.</para>
/// </remarks>
internal sealed class PdfStructureTree
{
    private readonly PdfDocumentWriter _writer;
    private readonly List<PdfStructureElement> _stack = [];
    private readonly PdfArray _nums = new();
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
        ArgumentNullException.ThrowIfNull(parent);
        if (!ReferenceEquals(parent.Tree, this))
        {
            throw new ArgumentException("The parent belongs to another document.", nameof(parent));
        }

        return new PdfStructureElement(this, parent, type, alt, language, attributes);
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

        if (element.Reference is not null)
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

            element.Reference = _writer.AllocateObject();
            parent.Kids!.Add(element.Reference);
            parent.OpenChild = element;
        }
        else
        {
            element.Reference = _writer.AllocateObject();
        }

        element.Kids = [];
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

        if (element.OpenChild is { } child)
        {
            CloseElement(child);
        }

        PdfDictionary dictionary = new PdfDictionary()
            .Set("Type", "StructElem")
            .Set("S", element.Type)
            .Set("P", element.Parent?.Reference ?? Root);
        if (element.Page is { } page)
        {
            dictionary.Set("Pg", page);
        }

        if (element.Kids is { Count: > 0 } kids)
        {
            dictionary.Set("K", new PdfArray([.. kids]));
        }

        if (element.Alt is { } alt)
        {
            dictionary.Set("Alt", PdfString.Text(PdfDocumentWriter.Clean(alt)));
        }

        if (element.Language is { } language)
        {
            dictionary.Set("Lang", PdfString.Text(language));
        }

        if (element.Attributes is { } attributes)
        {
            dictionary.Set("A", attributes);
        }

        _writer.WriteObject(element.Reference!, dictionary);
        element.IsClosed = true;
        element.Kids = null;
        if (element.Parent is { } parent && ReferenceEquals(parent.OpenChild, element))
        {
            parent.OpenChild = null;
        }
    }

    /// <summary>
    /// Records marked content numbered <paramref name="mcid"/> on <paramref name="page"/> as the next kid of <paramref name="element"/>, which is
    /// materialised if it is not yet: an <c>MCID</c> on the element's own page, a marked-content reference on another.
    /// </summary>
    internal void AddMarkedContent(PdfStructureElement element, PdfReference page, int mcid)
    {
        Open(element);
        if (element.OpenChild is { } child)
        {
            // Content of the element itself after a child's content: the child is done.
            CloseElement(child);
        }

        element.Page ??= page;
        element.Kids!.Add(element.Page.Number == page.Number
            ? new PdfInteger(mcid)
            : new PdfDictionary().Set("Type", "MCR").Set("Pg", page).Set("MCID", mcid));
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
        _writer.WriteObject(Root, new PdfDictionary()
            .Set("Type", "StructTreeRoot")
            .Set("K", Document.Reference!)
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
