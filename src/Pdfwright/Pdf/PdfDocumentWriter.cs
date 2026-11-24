using System.Globalization;
using System.Security;
using System.Text;

namespace Pdfwright.Pdf;

/// <summary>What the writer puts in the document's own metadata. Nothing is ever read from the machine or the clock.</summary>
internal sealed class PdfWriterOptions
{
    internal PdfCompression Compression { get; init; } = PdfCompression.Flate;

    internal string? Title { get; init; }

    internal string? Subject { get; init; }

    internal string? Author { get; init; }

    internal string? Keywords { get; init; }

    /// <summary>The application that made the content the file was built from (not this library, which is the producer); omitted when null.</summary>
    internal string? Creator { get; init; }

    internal string Producer { get; init; } = "Pdfwright";

    /// <summary>The document's language as a BCP 47 tag (<c>/Lang</c>), which a screen reader uses to choose its voice.</summary>
    internal string? Language { get; init; }

    /// <summary>
    /// The creation date. When it is null the file has no date at all: the writer does not read the clock, so a document
    /// built twice from the same input is the same file.
    /// </summary>
    internal DateTimeOffset? Created { get; init; }

    /// <summary>
    /// Writes a tagged PDF: a structure tree (<see cref="PdfStructureTree"/>), <c>/StructParents</c> and <c>/Tabs /S</c> on every page and
    /// <c>/MarkInfo</c> on the catalog. Off by default, and a document written with it off is the same file it always was.
    /// </summary>
    internal bool Tagged { get; init; }
}

/// <summary>A rectangle in the page's own space (origin bottom left, y up), for an annotation.</summary>
internal readonly record struct PdfRectangle(double Left, double Bottom, double Right, double Top);

/// <summary>
/// A document being written: pages one after another, each written as soon as it is ended, then the structure that
/// refers to them (page tree, catalog, metadata, cross-reference stream) when the document is completed.
/// </summary>
internal sealed class PdfDocumentWriter : IDisposable
{
    private readonly PdfFile _file;
    private readonly PdfWriterOptions _options;
    private readonly PdfReference _pagesRoot;
    private readonly List<PdfReference> _pages = [];
    private readonly List<PendingDestination> _destinations = [];
    private readonly List<Action<PdfDocumentWriter>> _onComplete = [];
    private readonly Dictionary<object, object> _shared = new(ReferenceEqualityComparer.Instance);
    private PdfPageWriter? _open;
    private int _unfinished;
    private bool _completed;

    internal PdfDocumentWriter(Stream output, PdfWriterOptions options)
    {
        _options = options;
        _file = new PdfFile(output, options.Compression);
        _pagesRoot = _file.Allocate();
        if (options.Tagged)
        {
            Structure = new PdfStructureTree(this);
        }
    }

    internal int PageCount => _pages.Count;

    /// <summary>The structure tree of a tagged document (<see cref="PdfWriterOptions.Tagged"/>), or <see langword="null"/> for an untagged one.</summary>
    internal PdfStructureTree? Structure { get; }

    /// <summary>
    /// Whether the metadata claims PDF/UA-1 (<c>pdfuaid:part</c> 1). Set it before <see cref="Complete"/>, once what could stand in the way is known (a
    /// missing glyph is known only when the last page is drawn); only a tagged document can claim it.
    /// </summary>
    internal bool ClaimsPdfUA
    {
        get => _claimsPdfUA;
        set
        {
            if (value && Structure is null)
            {
                throw new InvalidOperationException("Only a tagged document can claim PDF/UA.");
            }

            if (_completed)
            {
                throw new InvalidOperationException("The document is complete.");
            }

            _claimsPdfUA = value;
        }
    }

    private bool _claimsPdfUA;

    /// <summary>Writes an object of the document that pages refer to (a font, an image) and returns its reference.</summary>
    internal PdfReference AddObject(PdfObject value) => _file.Add(value);

    internal PdfReference AllocateObject() => _file.Allocate();

    /// <summary>
    /// What the document keeps once for everything that uses it: the embedded form of a font, the XObject of an image. The first call
    /// with a key (compared by reference) makes the value with <paramref name="create"/>, and every later call returns that same value.
    /// </summary>
    internal T GetOrAddShared<T>(object key, Func<T> create)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(create);
        if (_shared.TryGetValue(key, out object? found))
        {
            return (T)found;
        }

        T made = create();
        _shared[key] = made;
        return made;
    }

    /// <summary>
    /// Registers work for the moment the document is completed, before the page tree is written: an embedded font writes its
    /// objects then, when the glyphs the pages used are known.
    /// </summary>
    internal void OnComplete(Action<PdfDocumentWriter> action) => _onComplete.Add(action);

    internal void WriteObject(PdfReference reference, PdfObject value) => _file.Write(reference, value);

    /// <summary>Starts a page of the given size in points. The previous page must have been ended.</summary>
    internal PdfPageWriter BeginPage(double width, double height)
    {
        if (_open is not null)
        {
            throw new InvalidOperationException("The previous page was not ended.");
        }

        // ISO 32000-1, Annex C: a page is 3 to 14,400 units on a side (a unit is 1/72 inch). NaN and infinity fail these tests too.
        if (!(width is >= 3 and <= 14400))
        {
            throw new ArgumentOutOfRangeException(nameof(width), width, "A page is 3 to 14,400 units wide.");
        }

        if (!(height is >= 3 and <= 14400))
        {
            throw new ArgumentOutOfRangeException(nameof(height), height, "A page is 3 to 14,400 units high.");
        }

        _open = new PdfPageWriter(this, _pages.Count, width, height, _spareContent ?? new ContentStreamBuilder());
        _spareContent = null;
        if (Structure is not null)
        {
            // A tagged page's number is reserved now, not when it ends: the structure elements drawn on it name it (/Pg) while it is open.
            _open.Reference = _file.Allocate();
            _open.StructParents = Structure.TakeKey();
        }

        return _open;
    }

    /// <summary>
    /// The buffer of the content stream of the last page that was written, emptied, for the next page to use: a page's content grows its buffer by doubling (and a dense one passes the
    /// large object heap's threshold), which every page of a long table would repeat. It belongs to this writer, which is one save, so it is never shared between documents or threads.
    /// </summary>
    private ContentStreamBuilder? _spareContent;

    /// <summary>The largest buffer kept for the next page: a page with a lot of drawing does not make the rest of the save carry its buffer.</summary>
    private const int SpareContentLimit = 1 << 20;

    /// <summary>Takes the content builder of a page whose content stream is written, empties it and keeps it for the next page when it is not large.</summary>
    private void RecycleContent(PdfPageWriter page)
    {
        ContentStreamBuilder content = page.DropBody();
        if (content.Capacity <= SpareContentLimit)
        {
            content.Clear();
            _spareContent = content;
        }
    }

    /// <summary>Writes the page: its content stream first, then its dictionary, so nothing of it stays in memory.</summary>
    internal void EndPage(PdfPageWriter page)
    {
        if (!ReferenceEquals(page, _open))
        {
            throw new InvalidOperationException("That page is not the open one.");
        }

        CheckBalanced(page.Content, "body");
        PdfReference contents = _file.Add(new PdfStream(new PdfDictionary(), page.Content.ToMemory()));
        RecycleContent(page);
        PdfReference pageReference = page.Reference ?? _file.Allocate();
        WriteParentTreeEntry(page);
        _file.Write(pageReference, BuildPage(page, contents));
        _pages.Add(pageReference);
        _open = null;
    }

    /// <summary>
    /// Writes the page's content stream and keeps the page open for a header (a stream before the body) and a footer (a stream after it), drawn when the number of pages is known: the page's
    /// place in the page tree is fixed now, its dictionary is written by <see cref="FinishPage"/>. What the page holds in memory afterwards is its resources and annotations, not its content.
    /// </summary>
    internal void EndPageBody(PdfPageWriter page, bool header, bool footer)
    {
        if (!ReferenceEquals(page, _open))
        {
            throw new InvalidOperationException("That page is not the open one.");
        }

        CheckBalanced(page.Content, "body");
        page.BodyContents = _file.Add(new PdfStream(new PdfDictionary(), page.Content.ToMemory()));
        RecycleContent(page);
        page.Reference ??= _file.Allocate();
        WriteParentTreeEntry(page);
        page.HeaderContents = header ? _file.Allocate() : null;
        page.FooterContents = footer ? _file.Allocate() : null;
        _pages.Add(page.Reference);
        _open = null;
        _unfinished++;
    }

    /// <summary>Writes the header and footer streams and the dictionary of a page whose body was written by <see cref="EndPageBody"/>.</summary>
    internal void FinishPage(PdfPageWriter page)
    {
        if (page.Reference is null || page.BodyContents is null || page.Finished)
        {
            throw new InvalidOperationException("That page is not waiting for its header and footer.");
        }

        CheckBalanced(page.HeaderContent, "header");
        CheckBalanced(page.FooterContent, "footer");
        PdfArray contents = new();
        if (page.HeaderContents is { } headerReference)
        {
            _file.Write(headerReference, new PdfStream(new PdfDictionary(), page.HeaderContent.ToMemory()));
            contents.Add(headerReference);
        }

        contents.Add(page.BodyContents);
        if (page.FooterContents is { } footerReference)
        {
            _file.Write(footerReference, new PdfStream(new PdfDictionary(), page.FooterContent.ToMemory()));
            contents.Add(footerReference);
        }

        _file.Write(page.Reference, BuildPage(page, contents));
        page.Finished = true;
        _unfinished--;
    }

    /// <summary>A content stream is written whole: every marked-content sequence, artifact and span it began is ended in it (ISO 32000-1, 14.6).</summary>
    private static void CheckBalanced(ContentStreamBuilder content, string part)
    {
        if (content.MarkedContentDepth != 0)
        {
            throw new InvalidOperationException($"The page's {part} has {content.MarkedContentDepth} marked-content sequence(s) without their EMC.");
        }
    }

    private PdfDictionary BuildPage(PdfPageWriter page, PdfObject contents)
    {
        PdfDictionary dictionary = new PdfDictionary()
            .Set("Type", "Page")
            .Set("Parent", _pagesRoot)
            .Set("MediaBox", PdfArray.Of(0, 0, page.Width, page.Height))
            .Set("Resources", page.BuildResources())
            .Set("Contents", contents);
        if (page.StructParents is { } key)
        {
            // Tab order follows the structure (PDF/UA-1 asks for /Tabs /S on a page with annotations; every page has it, so none is forgotten).
            dictionary.Set("StructParents", key).Set("Tabs", "S");
        }

        PdfArray? annotations = null;
        foreach (PdfDictionary link in page.Links)
        {
            annotations ??= new PdfArray();
            annotations.Add(_file.Add(link));
        }

        foreach (PendingDestination destination in page.Destinations)
        {
            annotations ??= new PdfArray();
            PdfReference annotation = _file.Allocate();
            annotations.Add(annotation);
            _destinations.Add(destination with { Annotation = annotation });
        }

        if (annotations is not null)
        {
            dictionary.Set("Annots", annotations);
        }

        return dictionary;
    }

    /// <summary>
    /// Writes a tagged page's entry of the parent tree when its body ends: the array of the elements its <c>MCID</c>s belong to, in order. A header
    /// or a footer drawn after it is an artifact, so nothing is added to the array afterwards (<see cref="PdfPageWriter.BeginMarkedContent(PdfStructureElement)"/> refuses).
    /// </summary>
    private void WriteParentTreeEntry(PdfPageWriter page)
    {
        if (Structure is null || page.StructParents is not { } key)
        {
            return;
        }

        Structure.AddEntry(key, _file.Add(new PdfArray([.. page.TakeMarkedContentOwners()])));
    }

    /// <summary>Writes the page tree, the annotations that point at pages, the metadata and the catalog, and ends the file.</summary>
    internal void Complete()
    {
        if (_completed)
        {
            throw new InvalidOperationException("The document is complete.");
        }

        if (_open is not null)
        {
            throw new InvalidOperationException("A page is still open.");
        }

        if (_unfinished > 0)
        {
            throw new InvalidOperationException("A page is still waiting for its header and footer.");
        }

        if (_pages.Count == 0)
        {
            throw new InvalidOperationException("A document has at least one page.");
        }

        // Every link is checked before anything is written, so a bad link leaves the file as it was.
        foreach (PendingDestination destination in _destinations)
        {
            if (destination.TargetPage < 0 || destination.TargetPage >= _pages.Count)
            {
                throw new InvalidOperationException($"A link points at page {destination.TargetPage + 1}, and the document has {_pages.Count}.");
            }
        }

        _completed = true;
        foreach (Action<PdfDocumentWriter> action in _onComplete)
        {
            action(this);
        }

        Structure?.Complete();

        foreach (PendingDestination destination in _destinations)
        {
            PdfDictionary annotation = LinkAnnotation(destination.Area)
                .Set("Dest", new PdfArray(
                    _pages[destination.TargetPage],
                    new PdfName("XYZ"),
                    new PdfReal(destination.X),
                    new PdfReal(destination.Y),
                    PdfNull.Instance));
            _file.Write(destination.Annotation!, annotation);
        }

        _file.Write(_pagesRoot, new PdfDictionary()
            .Set("Type", "Pages")
            .Set("Count", _pages.Count)
            .Set("Kids", new PdfArray([.. _pages])));

        PdfReference? info = WriteInfo();
        PdfDictionary catalog = new PdfDictionary().Set("Type", "Catalog").Set("Pages", _pagesRoot);
        if (Structure is not null)
        {
            catalog.Set("MarkInfo", new PdfDictionary().Set("Marked", true)).Set("StructTreeRoot", Structure.Root);
        }

        if (Meta(_options.Language) is { } language)
        {
            catalog.Set("Lang", PdfString.Text(language));
        }

        if (Meta(_options.Title) is not null)
        {
            catalog.Set("ViewerPreferences", new PdfDictionary().Set("DisplayDocTitle", true));
        }

        catalog.Set("Metadata", _file.Add(new PdfStream(
            new PdfDictionary().Set("Type", "Metadata").Set("Subtype", "XML"),
            Encoding.UTF8.GetBytes(Xmp()))
        { NeverCompress = true }));

        PdfReference root = _file.Add(catalog);
        _file.Finish(root, info);
    }

    public void Dispose() => _file.Dispose();

    internal static PdfDictionary LinkAnnotation(PdfRectangle area)
    {
        if (!double.IsFinite(area.Left) || !double.IsFinite(area.Bottom) || !double.IsFinite(area.Right) || !double.IsFinite(area.Top))
        {
            throw new ArgumentOutOfRangeException(nameof(area), area, "A link's rectangle is finite.");
        }

        return new PdfDictionary()
            .Set("Type", "Annot")
            .Set("Subtype", "Link")
            // Print (flag 4): PDF/A-2 asks for it, and a link a reader can see is a link a printout can show.
            .Set("F", 4)
            .Set("Rect", PdfArray.Of(area.Left, area.Bottom, area.Right, area.Top))
            // No border: a link in a report is not boxed unless the layout draws a box.
            .Set("Border", PdfArray.Of(0, 0, 0));
    }

    private PdfReference? WriteInfo()
    {
        PdfDictionary info = new();
        if (Meta(_options.Title) is { } title)
        {
            info.Set("Title", PdfString.Text(title));
        }

        if (Meta(_options.Subject) is { } subject)
        {
            info.Set("Subject", PdfString.Text(subject));
        }

        if (Meta(_options.Author) is { } author)
        {
            info.Set("Author", PdfString.Text(author));
        }

        if (Meta(_options.Keywords) is { } keywords)
        {
            info.Set("Keywords", PdfString.Text(keywords));
        }

        if (Meta(_options.Creator) is { } creator)
        {
            info.Set("Creator", PdfString.Text(creator));
        }

        info.Set("Producer", PdfString.Text(Clean(_options.Producer)));
        if (_options.Created is { } created)
        {
            info.Set("CreationDate", PdfString.Date(created));
            info.Set("ModDate", PdfString.Date(created));
        }

        return _file.Add(info);
    }

    /// <summary>
    /// Text as the file carries it (see <see cref="Pdfwright.Text.TextNormalizer.Metadata"/>): line breaks and tabs become a space, other controls and the
    /// noncharacters U+FFFE and U+FFFF are dropped, a surrogate without its pair becomes U+FFFD, so that the XMP packet is always well-formed
    /// and the Info dictionary says the same as it.
    /// </summary>
    internal static string Clean(string text) => Pdfwright.Text.TextNormalizer.Metadata(text);

    /// <summary>An entry of the metadata as the file carries it, or <see langword="null"/> when nothing of it is left (it was empty, or only controls and white space): such an entry is left out of the Info dictionary and of the XMP packet alike.</summary>
    private static string? Meta(string? text) => text is null ? null : (Clean(text) is { Length: > 0 } cleaned ? cleaned : null);

    /// <summary>The XMP packet (ISO 16684-1) that repeats the Info dictionary in the form archives and indexers read.</summary>
    private string Xmp()
    {
        static string E(string text) => SecurityElement.Escape(Clean(text)) ?? string.Empty;

        StringBuilder xmp = new();
        xmp.Append("<?xpacket begin=\"﻿\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>\n");
        xmp.Append("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">\n<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">\n");
        xmp.Append("<rdf:Description rdf:about=\"\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\" xmlns:pdf=\"http://ns.adobe.com/pdf/1.3/\">\n");
        if (Meta(_options.Title) is { } title)
        {
            xmp.Append("<dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">").Append(E(title)).Append("</rdf:li></rdf:Alt></dc:title>\n");
        }

        if (Meta(_options.Subject) is { } subject)
        {
            xmp.Append("<dc:description><rdf:Alt><rdf:li xml:lang=\"x-default\">").Append(E(subject)).Append("</rdf:li></rdf:Alt></dc:description>\n");
        }

        if (Meta(_options.Author) is { } author)
        {
            xmp.Append("<dc:creator><rdf:Seq><rdf:li>").Append(E(author)).Append("</rdf:li></rdf:Seq></dc:creator>\n");
        }

        if (Meta(_options.Language) is { } language)
        {
            xmp.Append("<dc:language><rdf:Bag><rdf:li>").Append(E(language)).Append("</rdf:li></rdf:Bag></dc:language>\n");
        }

        xmp.Append("<pdf:Producer>").Append(E(_options.Producer)).Append("</pdf:Producer>\n");
        if (Meta(_options.Keywords) is { } keywords)
        {
            xmp.Append("<pdf:Keywords>").Append(E(keywords)).Append("</pdf:Keywords>\n");
        }

        if (Meta(_options.Creator) is { } creator)
        {
            xmp.Append("<xmp:CreatorTool>").Append(E(creator)).Append("</xmp:CreatorTool>\n");
        }
        if (_options.Created is { } created)
        {
            string stamp = created.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
            xmp.Append("<xmp:CreateDate>").Append(stamp).Append("</xmp:CreateDate>\n");
            xmp.Append("<xmp:ModifyDate>").Append(stamp).Append("</xmp:ModifyDate>\n");
        }

        xmp.Append("</rdf:Description>\n");
        if (ClaimsPdfUA)
        {
            // The PDF/UA identification schema (ISO 14289-1, 5); PDF/UA-1 asks for nothing else beside it (its extension schema description is a PDF/A rule).
            xmp.Append("<rdf:Description rdf:about=\"\" xmlns:pdfuaid=\"http://www.aiim.org/pdfua/ns/id/\">\n<pdfuaid:part>1</pdfuaid:part>\n</rdf:Description>\n");
        }

        xmp.Append("</rdf:RDF>\n</x:xmpmeta>\n<?xpacket end=\"w\"?>");
        return xmp.ToString();
    }

    /// <summary>An internal link whose target page is a number until the document is complete and every page has a reference.</summary>
    internal sealed record PendingDestination(PdfRectangle Area, int TargetPage, double X, double Y)
    {
        internal PdfReference? Annotation { get; init; }

    }
}

/// <summary>One page being drawn: its content stream, the resources the stream names, and its annotations.</summary>
internal sealed class PdfPageWriter
{
    private readonly PdfDocumentWriter _document;
    private readonly List<(string Name, PdfReference Font)> _fonts = [];
    private readonly List<(string Name, PdfReference Object)> _xObjects = [];
    private readonly List<(string Name, double Fill, double Stroke)> _graphicStates = [];

    internal PdfPageWriter(PdfDocumentWriter document, int index, double width, double height, ContentStreamBuilder content)
    {
        _document = document;
        _content = content;
        Index = index;
        Width = width;
        Height = height;
    }

    /// <summary>The document this page belongs to, for what it keeps once (fonts, images).</summary>
    internal PdfDocumentWriter Document => _document;

    internal int Index { get; }

    internal double Width { get; }

    internal double Height { get; }

    private ContentStreamBuilder? _content;

    /// <summary>The stream that is drawn on now: the body's, or after <see cref="UseHeader"/> or <see cref="UseFooter"/> that one. After <see cref="DropBody"/> it is an empty builder, made when something first asks for it.</summary>
    internal ContentStreamBuilder Content => _content ??= new ContentStreamBuilder();

    /// <summary>The content stream written by <see cref="End"/>'s sibling <see cref="EndBody"/>, while the page waits for its header and footer.</summary>
    internal PdfReference? BodyContents { get; set; }

    /// <summary>The reference reserved for the header's content stream, which comes before the body's, or <see langword="null"/> for a page with no header.</summary>
    internal PdfReference? HeaderContents { get; set; }

    /// <summary>The reference reserved for the footer's content stream, which comes after the body's, or <see langword="null"/> for a page with no footer.</summary>
    internal PdfReference? FooterContents { get; set; }

    internal ContentStreamBuilder HeaderContent { get; } = new();

    internal ContentStreamBuilder FooterContent { get; } = new();

    /// <summary>The page's own reference, reserved when its body was written, or when the page began in a tagged document.</summary>
    internal PdfReference? Reference { get; set; }

    /// <summary>The page's key in the parent tree (<c>/StructParents</c>) in a tagged document, or <see langword="null"/>.</summary>
    internal int? StructParents { get; set; }

    /// <summary>The structure element of each <c>MCID</c> of the page, by number, made with the first one (an untagged page never makes it).</summary>
    private List<PdfReference>? _markedContentOwners;

    /// <summary>The page's entry of the parent tree is written: it takes no more marked content.</summary>
    private bool _parentTreeEntryWritten;

    /// <summary>
    /// Begins the page's next marked content for <paramref name="element"/>: the page's next <c>MCID</c>, recorded for the parent tree, the element
    /// materialised if it is not yet (and its parents), and <c>/Type &lt;&lt;/MCID n&gt;&gt; BDC</c> written. Returns the <c>MCID</c>. End it with
    /// <see cref="EndMarkedContent"/>. Only the body has marked content: a header and a footer, drawn after it, are artifacts.
    /// </summary>
    internal int BeginMarkedContent(PdfStructureElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        PdfStructureTree structure = _document.Structure ?? throw new InvalidOperationException("The document is not tagged.");
        if (!ReferenceEquals(element.Tree, structure))
        {
            throw new ArgumentException("The element belongs to another document.", nameof(element));
        }

        if (_parentTreeEntryWritten || Reference is null)
        {
            throw new InvalidOperationException("The page's body is written: a header or a footer is an artifact, with no marked content of the structure.");
        }

        // Nesting is checked before the tree records anything, so a refused call leaves the tree and the page as they were.
        Content.CheckMarkedContentAllowed();
        _markedContentOwners ??= [];
        int mcid = _markedContentOwners.Count;
        structure.AddMarkedContent(element, Reference, mcid);
        _markedContentOwners.Add(element.Reference!);
        Content.BeginMarkedContent(element.Type, mcid);
        return mcid;
    }

    /// <summary>Begins the page's next marked content for the structure tree's current element (<see cref="PdfStructureTree.Current"/>).</summary>
    internal int BeginMarkedContent() =>
        BeginMarkedContent((_document.Structure ?? throw new InvalidOperationException("The document is not tagged.")).Current);

    /// <summary>Ends marked content, an artifact or a language span: <c>EMC</c>.</summary>
    internal void EndMarkedContent() => Content.EndMarkedContent();

    /// <summary>The elements of the page's <c>MCID</c>s, for its entry of the parent tree; after this the page takes no more marked content.</summary>
    internal List<PdfReference> TakeMarkedContentOwners()
    {
        if (_parentTreeEntryWritten)
        {
            throw new InvalidOperationException("The page's entry of the parent tree is written.");
        }

        List<PdfReference> owners = _markedContentOwners ?? [];
        _markedContentOwners = null;
        _parentTreeEntryWritten = true;
        return owners;
    }

    /// <summary>The page's header and footer have been written and so has its dictionary.</summary>
    internal bool Finished { get; set; }

    /// <summary>After <see cref="EndBody"/>, makes <see cref="Content"/> the header's stream (drawn before the body).</summary>
    internal void UseHeader() => _content = HeaderContent;

    /// <summary>
    /// Lets go of the body's content once it is written, so that a page waiting for its header and footer holds no more than its resources. No builder is made in its place: a page that is
    /// ended has no use for one, and a header or footer replaces it by <see cref="UseHeader"/> or <see cref="UseFooter"/>; drawing on the page before that still works, on a builder made then.
    /// </summary>
    internal ContentStreamBuilder DropBody()
    {
        ContentStreamBuilder body = Content;
        _content = null;
        return body;
    }

    /// <summary>After <see cref="EndBody"/>, makes <see cref="Content"/> the footer's stream (drawn after the body).</summary>
    internal void UseFooter() => _content = FooterContent;

    internal List<PdfDictionary> Links { get; } = [];

    internal List<PdfDocumentWriter.PendingDestination> Destinations { get; } = [];

    /// <summary>Makes a font object of the document available to this page's content and returns the name to use in <c>Tf</c>.</summary>
    internal string UseFont(PdfReference font)
    {
        foreach ((string known, PdfReference reference) in _fonts)
        {
            if (reference.Number == font.Number)
            {
                return known;
            }
        }

        string name = "F" + (_fonts.Count + 1).ToString(CultureInfo.InvariantCulture);
        _fonts.Add((name, font));
        return name;
    }

    internal string UseXObject(PdfReference xObject)
    {
        int found = _xObjects.FindIndex(x => x.Object.Number == xObject.Number);
        if (found >= 0)
        {
            return _xObjects[found].Name;
        }

        string name = "X" + (_xObjects.Count + 1).ToString(CultureInfo.InvariantCulture);
        _xObjects.Add((name, xObject));
        return name;
    }

    /// <summary>Makes an opacity available (fill and stroke alike) and returns the name to use in <c>gs</c>.</summary>
    internal string UseOpacity(double alpha) => UseOpacity(alpha, alpha);

    /// <summary>
    /// Makes a pair of opacities available, <paramref name="fill"/> for <c>ca</c> (fills, text and images) and <paramref name="stroke"/> for <c>CA</c>
    /// (strokes), and returns the name to use in <c>gs</c>. The same pair is one <c>ExtGState</c> however often it is asked for.
    /// </summary>
    internal string UseOpacity(double fill, double stroke)
    {
        if (!(fill is >= 0 and <= 1))
        {
            throw new ArgumentOutOfRangeException(nameof(fill), fill, "Opacity is between 0 and 1.");
        }

        if (!(stroke is >= 0 and <= 1))
        {
            throw new ArgumentOutOfRangeException(nameof(stroke), stroke, "Opacity is between 0 and 1.");
        }

        int found = _graphicStates.FindIndex(g => g.Fill == fill && g.Stroke == stroke);
        if (found >= 0)
        {
            return _graphicStates[found].Name;
        }

        string name = "G" + (_graphicStates.Count + 1).ToString(CultureInfo.InvariantCulture);
        _graphicStates.Add((name, fill, stroke));
        return name;
    }

    /// <summary>A link to an address outside the document, over a rectangle of this page.</summary>
    internal void AddUriLink(PdfRectangle area, string uri)
    {
        if (!uri.All(c => c is >= ' ' and <= '~'))
        {
            throw new ArgumentException("A URI is ASCII: percent-encode the rest.", nameof(uri));
        }

        Links.Add(PdfDocumentWriter.LinkAnnotation(area).Set(
            "A",
            new PdfDictionary().Set("S", "URI").Set("URI", new PdfString(Encoding.ASCII.GetBytes(uri), hex: false))));
    }

    /// <summary>A link to a position on a page of this document (0-based page number; <c>x</c>, <c>y</c> in that page's space).</summary>
    internal void AddPageLink(PdfRectangle area, int targetPage, double x, double y)
    {
        _ = PdfDocumentWriter.LinkAnnotation(area);
        if (!double.IsFinite(x) || !double.IsFinite(y))
        {
            throw new ArgumentOutOfRangeException(nameof(x), "A link's target position is finite.");
        }

        Destinations.Add(new PdfDocumentWriter.PendingDestination(area, targetPage, x, y));
    }

    internal PdfDictionary BuildResources()
    {
        PdfDictionary resources = new();
        if (_fonts.Count > 0)
        {
            PdfDictionary fonts = new();
            foreach ((string name, PdfReference font) in _fonts)
            {
                fonts.Set(name, font);
            }

            resources.Set("Font", fonts);
        }

        if (_xObjects.Count > 0)
        {
            PdfDictionary xObjects = new();
            foreach ((string name, PdfReference xObject) in _xObjects)
            {
                xObjects.Set(name, xObject);
            }

            resources.Set("XObject", xObjects);
        }

        if (_graphicStates.Count > 0)
        {
            PdfDictionary states = new();
            foreach ((string name, double fill, double stroke) in _graphicStates)
            {
                states.Set(name, new PdfDictionary().Set("Type", "ExtGState").Set("CA", new PdfReal(stroke)).Set("ca", new PdfReal(fill)));
            }

            resources.Set("ExtGState", states);
        }

        return resources;
    }

    internal void End() => _document.EndPage(this);

    /// <summary>Writes the content so far and keeps the page for a header and a footer (each a stream of its own, before and after the body); choose which with <see cref="UseHeader"/> and <see cref="UseFooter"/>. Finish it with <see cref="Finish"/>.</summary>
    internal void EndBody(bool header, bool footer) => _document.EndPageBody(this, header, footer);

    /// <summary>Writes the header and footer streams and the page's dictionary (after <see cref="EndBody"/>).</summary>
    internal void Finish() => _document.FinishPage(this);
}
