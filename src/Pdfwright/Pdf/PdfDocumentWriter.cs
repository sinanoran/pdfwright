using System.Globalization;
using System.Security;
using System.Text;

namespace Pdfwright.Pdf;

/// <summary>What the writer puts in the document's own metadata. Nothing is ever read from the machine or the clock.</summary>
internal sealed class PdfDocumentOptions
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
    private readonly PdfDocumentOptions _options;
    private readonly PdfReference _pagesRoot;
    private readonly List<PdfReference> _pages = [];
    private readonly List<PendingDestination> _destinations = [];
    private readonly List<Action<PdfDocumentWriter>> _onComplete = [];
    private PdfPageWriter? _open;
    private bool _completed;

    internal PdfDocumentWriter(Stream output, PdfDocumentOptions options)
    {
        _options = options;
        _file = new PdfFile(output, options.Compression);
        _pagesRoot = _file.Allocate();
    }

    internal int PageCount => _pages.Count;

    /// <summary>Writes an object of the document that pages refer to (a font, an image) and returns its reference.</summary>
    internal PdfReference AddObject(PdfObject value) => _file.Add(value);

    internal PdfReference AllocateObject() => _file.Allocate();

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

        _open = new PdfPageWriter(this, _pages.Count, width, height);
        return _open;
    }

    /// <summary>Writes the page: its content stream first, then its dictionary, so nothing of it stays in memory.</summary>
    internal void EndPage(PdfPageWriter page)
    {
        if (!ReferenceEquals(page, _open))
        {
            throw new InvalidOperationException("That page is not the open one.");
        }

        PdfReference contents = _file.Add(new PdfStream(new PdfDictionary(), page.Content.ToMemory()));
        PdfReference pageReference = _file.Allocate();

        PdfDictionary dictionary = new PdfDictionary()
            .Set("Type", "Page")
            .Set("Parent", _pagesRoot)
            .Set("MediaBox", PdfArray.Of(0, 0, page.Width, page.Height))
            .Set("Resources", page.BuildResources())
            .Set("Contents", contents);

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

        _file.Write(pageReference, dictionary);
        _pages.Add(pageReference);
        _open = null;
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
        if (_options.Language is { Length: > 0 } language)
        {
            catalog.Set("Lang", PdfString.Text(language));
        }

        if (_options.Title is { Length: > 0 })
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
        if (_options.Title is { Length: > 0 } title)
        {
            info.Set("Title", PdfString.Text(Clean(title)));
        }

        if (_options.Subject is { Length: > 0 } subject)
        {
            info.Set("Subject", PdfString.Text(Clean(subject)));
        }

        if (_options.Author is { Length: > 0 } author)
        {
            info.Set("Author", PdfString.Text(Clean(author)));
        }

        if (_options.Keywords is { Length: > 0 } keywords)
        {
            info.Set("Keywords", PdfString.Text(Clean(keywords)));
        }

        if (_options.Creator is { Length: > 0 } creator)
        {
            info.Set("Creator", PdfString.Text(Clean(creator)));
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
    /// Text as the file carries it: the characters XML 1.0 forbids (controls other than tab, line feed and carriage return, the
    /// noncharacters U+FFFE and U+FFFF, a surrogate without its pair) are dropped, so that the XMP packet is always well-formed
    /// and the Info dictionary says the same as it.
    /// </summary>
    internal static string Clean(string text)
    {
        StringBuilder clean = new(text.Length);
        foreach (Rune rune in text.EnumerateRunes())
        {
            int value = rune.Value;
            bool allowed = value is 0x9 or 0xA or 0xD or (>= 0x20 and <= 0xD7FF) or (>= 0xE000 and <= 0xFFFD) or (>= 0x10000 and <= 0x10FFFF);
            if (allowed && rune != Rune.ReplacementChar)
            {
                clean.Append(rune.ToString());
            }
        }

        return clean.ToString();
    }

    /// <summary>The XMP packet (ISO 16684-1) that repeats the Info dictionary in the form archives and indexers read.</summary>
    private string Xmp()
    {
        static string E(string text) => SecurityElement.Escape(Clean(text)) ?? string.Empty;

        StringBuilder xmp = new();
        xmp.Append("<?xpacket begin=\"﻿\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>\n");
        xmp.Append("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">\n<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">\n");
        xmp.Append("<rdf:Description rdf:about=\"\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\" xmlns:pdf=\"http://ns.adobe.com/pdf/1.3/\">\n");
        if (_options.Title is { Length: > 0 } title)
        {
            xmp.Append("<dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">").Append(E(title)).Append("</rdf:li></rdf:Alt></dc:title>\n");
        }

        if (_options.Subject is { Length: > 0 } subject)
        {
            xmp.Append("<dc:description><rdf:Alt><rdf:li xml:lang=\"x-default\">").Append(E(subject)).Append("</rdf:li></rdf:Alt></dc:description>\n");
        }

        if (_options.Author is { Length: > 0 } author)
        {
            xmp.Append("<dc:creator><rdf:Seq><rdf:li>").Append(E(author)).Append("</rdf:li></rdf:Seq></dc:creator>\n");
        }

        if (_options.Language is { Length: > 0 } language)
        {
            xmp.Append("<dc:language><rdf:Bag><rdf:li>").Append(E(language)).Append("</rdf:li></rdf:Bag></dc:language>\n");
        }

        xmp.Append("<pdf:Producer>").Append(E(_options.Producer)).Append("</pdf:Producer>\n");
        if (_options.Keywords is { Length: > 0 } keywords)
        {
            xmp.Append("<pdf:Keywords>").Append(E(keywords)).Append("</pdf:Keywords>\n");
        }

        if (_options.Creator is { Length: > 0 } creator)
        {
            xmp.Append("<xmp:CreatorTool>").Append(E(creator)).Append("</xmp:CreatorTool>\n");
        }
        if (_options.Created is { } created)
        {
            string stamp = created.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
            xmp.Append("<xmp:CreateDate>").Append(stamp).Append("</xmp:CreateDate>\n");
            xmp.Append("<xmp:ModifyDate>").Append(stamp).Append("</xmp:ModifyDate>\n");
        }

        xmp.Append("</rdf:Description>\n</rdf:RDF>\n</x:xmpmeta>\n<?xpacket end=\"w\"?>");
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
    private readonly List<(string Name, double Alpha)> _graphicStates = [];

    internal PdfPageWriter(PdfDocumentWriter document, int index, double width, double height)
    {
        _document = document;
        Index = index;
        Width = width;
        Height = height;
    }

    internal int Index { get; }

    internal double Width { get; }

    internal double Height { get; }

    internal ContentStreamBuilder Content { get; } = new();

    internal List<PdfDictionary> Links { get; } = [];

    internal List<PdfDocumentWriter.PendingDestination> Destinations { get; } = [];

    /// <summary>Makes a font object of the document available to this page's content and returns the name to use in <c>Tf</c>.</summary>
    internal string UseFont(PdfReference font)
    {
        int found = _fonts.FindIndex(f => f.Font.Number == font.Number);
        if (found >= 0)
        {
            return _fonts[found].Name;
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
    internal string UseOpacity(double alpha)
    {
        if (alpha is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(alpha), alpha, "Opacity is between 0 and 1.");
        }

        int found = _graphicStates.FindIndex(g => g.Alpha == alpha);
        if (found >= 0)
        {
            return _graphicStates[found].Name;
        }

        string name = "G" + (_graphicStates.Count + 1).ToString(CultureInfo.InvariantCulture);
        _graphicStates.Add((name, alpha));
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
            foreach ((string name, double alpha) in _graphicStates)
            {
                states.Set(name, new PdfDictionary().Set("Type", "ExtGState").Set("CA", new PdfReal(alpha)).Set("ca", new PdfReal(alpha)));
            }

            resources.Set("ExtGState", states);
        }

        return resources;
    }

    internal void End() => _document.EndPage(this);
}
