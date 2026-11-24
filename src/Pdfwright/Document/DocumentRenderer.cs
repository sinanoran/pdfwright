using Pdfwright.Fonts;
using Pdfwright.Pdf;
using Pdfwright.Text;

namespace Pdfwright.Document;

/// <summary>
/// Lays the blocks of a document out from top to bottom inside the margins and writes the pages. It is the flow: it decides where a block goes, where a
/// paragraph is split between pages, and keeps a heading with the first line of what follows. One renderer is one save.
/// </summary>
internal sealed partial class DocumentRenderer
{
    private const int MaxReportedGlyphs = 1000;

    private readonly PdfDocumentOptions _options;
    private readonly IReadOnlyList<Block> _blocks;
    private readonly FontCollection.Snapshot _fonts;
    private readonly CancellationToken _cancellation;
    private readonly PdfDocumentWriter _writer;
    private readonly CountingStream _output;

    private readonly double _pageWidth;
    private readonly double _pageHeight;
    private readonly double _left;
    private readonly double _top;
    private readonly double _contentWidth;
    private readonly double _contentHeight;
    private readonly ResolvedStyle _base;
    private readonly ResolvedStyle[] _headings;

    private readonly List<MissingGlyph> _missing = [];
    private readonly HashSet<(int CodePoint, string? Language, int Page)> _missingSeen = [];
    private readonly List<string> _missingFamilies = [];
    private readonly List<TruncatedTable> _truncated = [];
    private readonly HashSet<string> _families;

    private readonly bool _overlay;
    private readonly PdfEmbeddedFont?[] _embeddedFonts;
    private readonly PdfEmbeddedFont?[] _drawEmbedded;
    private readonly string?[] _drawNames;
    private PdfPageWriter? _page;
    private double _y;
    private bool _hasContent;

    private DocumentRenderer(PdfDocumentOptions options, IReadOnlyList<Block> blocks, FontCollection.Snapshot fonts, Stream output, CancellationToken cancellation)
    {
        _options = options;
        _overlay = options.Header is not null || options.Footer is not null;
        _blocks = blocks;
        _fonts = fonts;
        _embeddedFonts = new PdfEmbeddedFont?[fonts.Entries.Length];
        _drawEmbedded = new PdfEmbeddedFont?[fonts.Entries.Length];
        _drawNames = new string?[fonts.Entries.Length];
        _cancellation = cancellation;
        _output = new CountingStream(output);
        _families = new HashSet<string>(fonts.Entries.Select(e => e.Family), StringComparer.OrdinalIgnoreCase);

        _pageWidth = options.PageSize.Width.Points;
        _pageHeight = options.PageSize.Height.Points;
        _left = options.Margins.Left.Points;
        _top = options.Margins.Top.Points;
        _contentWidth = _pageWidth - _left - options.Margins.Right.Points;
        _contentHeight = _pageHeight - _top - options.Margins.Bottom.Points;

        _base = ResolvedStyle.Base(fonts, options.DefaultStyle, options.Language);
        _headings = [.. Enumerable.Range(0, 6).Select(level => HeadingStyle(options, _base, level))];

        DateTimeOffset? created = options.Creation ?? (options.Deterministic ? null : DateTimeOffset.UtcNow);
        _writer = new PdfDocumentWriter(_output, new PdfWriterOptions
        {
            Compression = options.Compress ? PdfCompression.Flate : PdfCompression.None,
            Title = options.Title,
            Subject = options.Subject,
            Author = options.Author,
            Creator = options.Creator,
            Language = options.Language,
            Created = created,
            Tagged = options.Tagged,
        });
        _tagParent = _writer.Structure?.Document;
        _documentLanguage = TaggedLanguage(ResolvedStyle.Base(fonts, null, options.Language).Language);
        if (options.Tagged)
        {
            _accessibility = [];
            NoteDocumentIssues(options);
        }
    }

    /// <summary>The document's language as the catalog's <c>/Lang</c> has it, read by the same rule as a style's; <see langword="null"/> when it is not set or empty.</summary>
    private readonly string? _documentLanguage;

    /// <summary>Checks the options once, so that a bad document fails when it is made and not halfway through a save.</summary>
    internal static void Validate(PdfDocumentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Fonts is null || options.Fonts.Count == 0)
        {
            throw new ArgumentException("The document needs a font collection with at least one font.", nameof(options));
        }

        // A value that cannot be used is refused where it is set (see the options' init accessors); what is left is what only the values together decide.
        Margins m = options.Margins;
        if (options.PageSize.Width.Points - m.Left.Points - m.Right.Points < 1 || options.PageSize.Height.Points - m.Top.Points - m.Bottom.Points < 1)
        {
            throw new ArgumentException("The margins leave less than one point for content.", nameof(options));
        }

        if (options.HeaderOffset is { } headerOffset && headerOffset > m.Top)
        {
            throw new ArgumentException("The header offset is more than the top margin.", nameof(options));
        }

        if (options.FooterOffset is { } footerOffset && footerOffset > m.Bottom)
        {
            throw new ArgumentException("The footer offset is more than the bottom margin.", nameof(options));
        }

        FontCollection.Snapshot fonts = options.Fonts.Current;
        ResolvedStyle baseStyle = ResolvedStyle.Base(fonts, options.DefaultStyle, options.Language);
        for (int level = 0; level < 6; level++)
        {
            _ = HeadingStyle(options, baseStyle, level);
        }
    }

    /// <summary>Lays out and writes the document to <paramref name="output"/>, and returns what it found.</summary>
    internal static DocumentDiagnostics Render(PdfDocumentOptions options, IReadOnlyList<Block> blocks, Stream output, CancellationToken cancellation)
    {
        DocumentRenderer renderer = new(options, blocks, options.Fonts.Current, output, cancellation);
        using (renderer._writer)
        {
            DocumentDiagnostics report = renderer.Run();
            renderer._output.Flush();
            return report;
        }
    }

    private static ResolvedStyle HeadingStyle(PdfDocumentOptions options, ResolvedStyle baseStyle, int index)
    {
        if (options.HeadingStyles is { } styles)
        {
            return baseStyle.Apply(styles[index]);
        }

        double[] scale = [2.0, 1.6, 1.3, 1.15, 1.0, 1.0];
        return baseStyle.Apply(new TextStyle { Size = Length.Pt(baseStyle.Size * scale[index]), Weight = 700 });
    }

    private DocumentDiagnostics Run()
    {
        // The next block is prepared once, a block ahead of the loop, so that a heading can ask how tall the first line after it is.
        Prepared? ahead = _blocks.Count > 0 ? Prepare(_blocks[0]) : null;
        for (int i = 0; i < _blocks.Count; i++)
        {
            _cancellation.ThrowIfCancellationRequested();
            Prepared current = ahead!;
            ahead = i + 1 < _blocks.Count ? Prepare(_blocks[i + 1]) : null;
            switch (_blocks[i])
            {
                case Paragraph or Heading:
                    PlaceText(current, ahead);
                    break;
                case ItemList:
                    PlaceList(current, ahead);
                    break;
                case Table table:
                    PlaceTable(table, i);
                    break;
                case Columns or Drawing or Image:
                    PlaceFixed(current);
                    break;
                case Callout:
                    PlaceCallout(current);
                    break;
                case Spacer spacer:
                    PlaceSpacer(spacer.Height.Points);
                    break;
                case PageBreak:
                    if (_page is null)
                    {
                        OpenPage();
                    }

                    ClosePage();
                    break;
                default:
                    // C# cannot check a switch over a class hierarchy for completeness: a block added to the library and not handled above ends here.
                    throw new InvalidOperationException($"The block type {_blocks[i].GetType().Name} is not handled.");
            }
        }

        if (_writer.PageCount == 0 && _page is null)
        {
            OpenPage(); // a document has at least one page
        }

        ClosePage();
        _cancellation.ThrowIfCancellationRequested();
        DrawOverlays();

        // A tagged document claims PDF/UA-1 when nothing stood in the way; the header and the footer are drawn, so every missing glyph is known.
        bool pdfUA = _accessibility is { Count: 0 };
        if (pdfUA)
        {
            _writer.ClaimsPdfUA = true;
        }

        _writer.Complete();

        // The fonts are written by Complete, after the last page: this is the check that holds the limit. The caller's stream has not been touched, so nothing of an oversized file escapes.
        CheckBytes();
        return new DocumentDiagnostics(_missing.AsReadOnly(), _missingFamilies.AsReadOnly(), _writer.PageCount, _output.Count, _truncated.AsReadOnly(), _accessibility?.AsReadOnly(), pdfUA);
    }

    /// <summary>What keeps a tagged document from being PDF/UA-1, in the order found (<see cref="DocumentDiagnostics.AccessibilityIssues"/>), or <see langword="null"/> untagged: nothing is checked then.</summary>
    private readonly List<AccessibilityIssue>? _accessibility;

    /// <summary>The pages a missing glyph was reported on as an accessibility issue, once each; made with the first.</summary>
    private HashSet<int>? _glyphIssuePages;

    /// <summary>The level of the last heading drawn, 0 before the first.</summary>
    private int _headingLevel;

    /// <summary>The <see cref="PdfDocumentWriter.NotdefDrawn"/> count the last <see cref="NoteNotdef"/> saw.</summary>
    private long _notdefSeen;

    /// <summary>
    /// Notes a <c>.notdef</c> glyph drawn on a page since the last call, whatever drew it (the body, a header, a footer, a canvas) and whatever the
    /// character: one that no font has is reported with its code point as it is drawn, but a character the shaper leaves to the font's glyph 0 (a Hangul
    /// filler, which HarfBuzz does not hide) is found only here. Called when the page's body ends and when its header and footer are drawn.
    /// </summary>
    private void NoteNotdef(int pageNumber)
    {
        if (_accessibility is null || _writer.NotdefDrawn == _notdefSeen)
        {
            return;
        }

        _notdefSeen = _writer.NotdefDrawn;
        if ((_glyphIssuePages ??= []).Add(pageNumber))
        {
            _accessibility.Add(new AccessibilityIssue(
                AccessibilityIssueKind.MissingGlyph,
                pageNumber,
                "A character is drawn as the font's .notdef glyph, which PDF/UA-1 forbids (7.21.8)."));
        }
    }

    /// <summary>The issues of the document as a whole: PDF/UA-1 asks for a title (7.1, shown by the viewer as <c>DisplayDocTitle</c> says) and a language (7.2).</summary>
    private void NoteDocumentIssues(PdfDocumentOptions options)
    {
        if (options.Title is null || PdfDocumentWriter.Clean(options.Title).Length == 0)
        {
            _accessibility!.Add(new AccessibilityIssue(AccessibilityIssueKind.MissingTitle, null, "The document has no title: PDF/UA-1 asks for one in the metadata (dc:title)."));
        }

        if (options.Language is null || PdfDocumentWriter.Clean(options.Language).Length == 0)
        {
            _accessibility!.Add(new AccessibilityIssue(AccessibilityIssueKind.MissingLanguage, null, "The document has no language: PDF/UA-1 asks for the natural language of its text (/Lang)."));
        }
        else
        {
            NoteLanguage(PdfDocumentWriter.Clean(options.Language), null);
        }
    }

    /// <summary>The languages checked by <see cref="NoteLanguage"/>, each once; made with the first.</summary>
    private HashSet<string>? _checkedLanguages;

    /// <summary>
    /// Notes a language written as a <c>/Lang</c> (the catalog's, an element's or a span's) that is not a language tag, which PDF/UA-1 7.2 asks for:
    /// once for each language, on the page it is first drawn on (none for the document's own).
    /// </summary>
    private void NoteLanguage(string? language, int? pageNumber)
    {
        if (string.IsNullOrEmpty(language) || !(_checkedLanguages ??= new(StringComparer.Ordinal)).Add(language) || IsLanguageTag(language))
        {
            return;
        }

        _accessibility!.Add(new AccessibilityIssue(
            AccessibilityIssueKind.InvalidLanguage,
            pageNumber,
            $"'{language}' is not a language tag (letters, then subtags of letters and digits after hyphens, such as en-US): PDF/UA-1 asks for one in /Lang (7.2)."));
    }

    /// <summary>Whether a language is a Language-Tag of RFC 3066, as PDF 1.7 (14.9.2) defines /Lang: a primary subtag of 1 to 8 letters, then subtags of 1 to 8 letters and digits, each after a hyphen.</summary>
    internal static bool IsLanguageTag(string language)
    {
        int start = 0;
        for (int part = 0; start <= language.Length; part++)
        {
            int end = language.IndexOf('-', start);
            if (end < 0)
            {
                end = language.Length;
            }

            int length = end - start;
            if (length is < 1 or > 8)
            {
                return false;
            }

            for (int i = start; i < end; i++)
            {
                char c = language[i];
                if (!(char.IsAsciiLetter(c) || (part > 0 && char.IsAsciiDigit(c))))
                {
                    return false;
                }
            }

            start = end + 1;
        }

        return true;
    }

    /// <summary>
    /// Notes a heading when its first content is drawn, in reading order: the first must be a level 1 heading (Matterhorn 14-002) and none may be more
    /// than one level below the one before it (14-003). Levels are reported, not changed.
    /// </summary>
    private void NoteHeading(int level, int pageNumber)
    {
        if (_headingLevel == 0 && level != 1)
        {
            _accessibility!.Add(new AccessibilityIssue(AccessibilityIssueKind.FirstHeadingNotH1, pageNumber, $"The first heading is an H{level}: a document's first heading is an H1."));
        }
        else if (_headingLevel > 0 && level > _headingLevel + 1)
        {
            _accessibility!.Add(new AccessibilityIssue(AccessibilityIssueKind.SkippedHeadingLevel, pageNumber, $"An H{level} follows an H{_headingLevel}: a heading is at most one level below the heading before it."));
        }

        _headingLevel = level;
    }

    /// <summary>A block ready to place: its lines and its spacing. Blocks that have no text have no layout.</summary>
    private sealed class Prepared
    {
        internal StyledLayout? Text { get; init; }

        internal double Before { get; init; }

        internal double After { get; init; }

        internal bool KeepWithNext { get; init; }

        internal bool KeepTogether { get; init; }

        internal FixedItem? Fixed { get; init; }

        internal CalloutPlan? Callout { get; init; }

        /// <summary>The paragraphs of a list, one after the other, each with its marker when it is the first of an item; <see cref="Text"/> is the first of them.</summary>
        internal List<TextItem>? ListItems { get; init; }

        /// <summary>The height of what a block needs to start (its first line, a whole picture), when it is not a text block.</summary>
        internal double? FirstUnit { get; init; }

        /// <summary>The height of the first line of the block, or what the block takes to start a page for the purpose of keeping a heading with it.</summary>
        internal double FirstLineHeight => FirstUnit ?? (Text is { Layout.Lines.Count: > 0 } t ? t.Layout.Lines[0].Pitch : 0);

        internal bool HasFlow => Text is not null || FirstUnit is not null;

        /// <summary>The whole height of a block that asks to be kept together and fits a page, or <see langword="null"/>: a heading before it must stay with all of it, not only its first line.</summary>
        internal double? WholeHeight { get; init; }
    }

    private Prepared Prepare(Block block)
    {
        switch (block)
        {
            case Paragraph or Heading:
            {
                return PreparedOf(BuildTextItem(block, _contentWidth, _options.Direction));
            }

            case ItemList list:
            {
                List<TextItem> items = BuildList(list, _contentWidth, _options.Direction, 0, _base.Apply(list.Style).Size * 0.6);
                Prepared first = PreparedOf(items[0]);
                return new Prepared { Text = first.Text, Before = first.Before, After = first.After, ListItems = items };
            }

            default:
                return PrepareFlowBlock(block);
        }
    }

    private Prepared PreparedOf(TextItem item)
    {
        double whole = item.Height;
        return new Prepared
        {
            Text = item.Styled,
            Before = item.Before,
            After = item.After,
            KeepWithNext = item.KeepWithNext,
            KeepTogether = item.KeepTogether,
            WholeHeight = item.KeepTogether && whole <= _contentHeight ? whole : null,
        };
    }

    private static double Space(Length? length, double fallback) => length is { } value ? value.Points : fallback;

    /// <summary>Records the families that the styles of a laid out block name and the collection does not have (the block was set in other fonts), once each in order of first use.</summary>
    private void NoteFamilies(StyledLayout layout)
    {
        IReadOnlyList<ResolvedStyle> styles = layout.Styles;
        for (int i = 0; i < styles.Count; i++)
        {
            NoteFamily(styles[i].Family);
        }
    }

    /// <summary>
    /// A spacer is always honoured, at the top of a page as well (at the start of the document, after a page break): it is the author's intent, unlike the space before a paragraph, which is dropped there.
    /// What does not fit at the bottom is cut. After it the page has content, so that the next block is set below it and not drawn over it, or on a new page when none of it fits.
    /// </summary>
    private void PlaceSpacer(double height)
    {
        if (_page is null)
        {
            OpenPage();
        }

        _y = Math.Min(_y + height, _contentHeight);
        _hasContent = true;
    }

    private void PlaceText(Prepared block, Prepared? next)
    {
        StyledLayout styled = block.Text!;
        IReadOnlyList<LaidOutLine> lines = styled.Layout.Lines;
        int placed = 0;

        // A heading needs room for itself, the space after it, the space before what follows and the first line of what follows; when they do not fit, it goes to the next page
        // (unless it is already at the top of one). The space before the next block counts because it is dropped only at the top of a page, and the next block is below the heading.
        if (block.KeepWithNext && _hasContent && _page is not null)
        {
            double own = lines.Sum(l => l.Pitch) + block.After + (next is { HasFlow: true } ? next.Before : 0);
            double follow = next is { HasFlow: true } ? next.FirstLineHeight : 0;
            if (next?.WholeHeight is { } whole && own + whole <= _contentHeight)
            {
                follow = whole; // the next block will not be split, so the heading goes with all of it
            }

            double rest = own + follow;
            if (rest <= _contentHeight && _y + block.Before + rest > _contentHeight)
            {
                ClosePage();
            }
        }

        if (block.KeepTogether && _hasContent && _page is not null)
        {
            double all = lines.Sum(l => l.Pitch);
            if (all <= _contentHeight && _y + block.Before + all > _contentHeight + 1e-9)
            {
                ClosePage();
            }
        }

        bool first = true;
        while (placed < lines.Count)
        {
            _cancellation.ThrowIfCancellationRequested();
            if (_page is null)
            {
                OpenPage();
            }

            double before = first && _hasContent ? block.Before : 0;
            double available = _contentHeight - _y - before;
            int fit = 0;
            double used = 0;
            while (placed + fit < lines.Count && used + lines[placed + fit].Pitch <= available + 1e-9)
            {
                used += lines[placed + fit].Pitch;
                fit++;
            }

            if (fit == 0)
            {
                if (!_hasContent)
                {
                    // A line taller than a whole page: it is drawn anyway, because nothing else can be done with it.
                    fit = 1;
                    used = lines[placed].Pitch;
                }
                else
                {
                    ClosePage();
                    first = false;
                    continue;
                }
            }

            _y += before;
            Draw(styled, placed, fit);
            _y += used;
            _hasContent = true;
            placed += fit;
            first = false;
            if (placed < lines.Count)
            {
                ClosePage();
            }
        }

        _y = Math.Min(_y + block.After, _contentHeight);
    }

    private void Draw(StyledLayout styled, int firstLine, int count) => Draw(styled, firstLine, count, _left, _y);

    /// <summary>
    /// Draws lines of a laid out paragraph with the top left corner of the first at (<paramref name="x"/>, <paramref name="y"/>): x from the left edge of the page, y down from the top of the content area.
    /// <paramref name="repeated"/> is text drawn again inside a pagination artifact (a table's header row on a later page): tagged, it is drawn without its element, which its first drawing has.
    /// </summary>
    private void Draw(StyledLayout styled, int firstLine, int count, double x, double y, bool repeated = false)
    {
        ParagraphLayout layout = styled.Layout;
        if (styled.List is { } placement)
        {
            // A paragraph of a list item: the text is inset from the start edge, and the marker is drawn with the first line, so that the two are never apart. Tagged, the marker is its
            // item's Lbl, whose marked content ends here, before the paragraph's own begins below: the two never nest.
            if (firstLine == 0 && placement.Marker is { } marker)
            {
                LaidOutLine markerLine = marker.Layout.Lines[0];
                double markerLeft = placement.RightToLeft
                    ? x + placement.ContainerWidth - placement.MarkerInset - markerLine.Width
                    : x + placement.MarkerInset;
                Draw(marker, 0, 1, markerLeft - markerLine.X, y + layout.Lines[0].Ascent - markerLine.Ascent);
            }

            x += placement.RightToLeft ? 0 : placement.Inset;
        }

        PdfPageWriter page = _page!;

        // The fonts the lines use, in the order their first run names them (the order the page's font names are handed out in). The two arrays belong to this renderer
        // and are cleared for each call: a renderer draws on one thread, and a layout does not keep the arrays it is given. They are filled only here, after the recursive Draw of the list marker above has
        // returned, which is why that recursion cannot find them holding the fonts of this call.
        PdfEmbeddedFont?[] embedded = _drawEmbedded;
        string?[] names = _drawNames;
        Array.Clear(embedded);
        Array.Clear(names);
        for (int l = firstLine; l < firstLine + count; l++)
        {
            IReadOnlyList<PlacedRun> placedRuns = layout.Lines[l].Runs;
            for (int r = 0; r < placedRuns.Count; r++)
            {
                int index = placedRuns[r].Run.FontIndex;
                if (embedded[index] is not null)
                {
                    continue;
                }

                PdfEmbeddedFont font = EmbeddedFont(index);
                embedded[index] = font;
                names[index] = page.UseFont(font.Reference);
            }
        }

        // Tagged, the lines are the marked content of their element (an MCID on this page; a paragraph split between pages has one on each), with a span
        // around a run in another language than the paragraph's; text with no element (blank text, a header or a footer) is an artifact.
        bool marked = false;
        bool artifact = false;
        if (_writer.Structure is not null)
        {
            if (styled.Tag is { } element && !(repeated && _inArtifact))
            {
                if (_inArtifact)
                {
                    // Text with an element drawn inside an artifact would be the document's text and not at once: a defect of the renderer, never output.
                    throw new InvalidOperationException($"The text of a {element.Type} element is drawn inside an artifact.");
                }

                if (_accessibility is not null && element.Number == 0 && element.Type is ['H', >= '1' and <= '6'])
                {
                    NoteHeading(element.Type[1] - '0', page.Index + 1);
                }

                if (_accessibility is not null)
                {
                    // The element's /Lang and its spans' come from the styles of its text.
                    IReadOnlyList<ResolvedStyle> styles = styled.Styles;
                    for (int s = 0; s < styles.Count; s++)
                    {
                        NoteLanguage(styles[s].Language, page.Index + 1);
                    }
                }

                _ = page.BeginMarkedContent(element);
                marked = true;
            }
            else
            {
                artifact = BeginArtifact();
            }
        }

        layout.Draw(page.Content, embedded, names, x, _pageHeight - _top - y, firstLine, count, marked, TaggedLanguage(styled.Styles[0].Language) ?? _documentLanguage);
        if (marked)
        {
            page.EndMarkedContent();
        }

        EndArtifact(artifact);
        RecordMissing(styled, firstLine, count, page.Index + 1);
    }

    /// <summary>The element the text items built now belong to (<see cref="BuildTextItem"/>): the <c>Document</c>, a callout's or a column cell's <c>Div</c>, a list item's <c>LBody</c>, or <see langword="null"/> for none (untagged, or a header or footer).</summary>
    private PdfStructureElement? _tagParent;

    /// <summary>
    /// The content drawn now is inside an artifact (a header or a footer, a table's header row repeated on a later page): drawing does not begin another,
    /// and text with an element is refused there unless it is drawn again (<see cref="Draw(StyledLayout, int, int, double, double, bool)"/>).
    /// </summary>
    private bool _inArtifact;

    /// <summary>Makes a light structure element under the current parent (<see cref="_tagParent"/>), or nothing when there is none.</summary>
    private PdfStructureElement? Tag(string type) =>
        _tagParent is { } parent ? _writer.Structure!.Create(parent, type) : null;

    /// <summary>A language as tagging uses it: an empty one is not set (no <c>/Lang</c>, no span; the language around it applies).</summary>
    private static string? TaggedLanguage(string? language) => string.IsNullOrEmpty(language) ? null : language;

    /// <summary>
    /// Begins an artifact on the page's current stream when the document is tagged and none is open, for content that is not the document's text
    /// (a callout's box, a picture, a table's fills and borders); returns whether it began one, for <see cref="EndArtifact"/>.
    /// </summary>
    private bool BeginArtifact()
    {
        if (_writer.Structure is null || _inArtifact)
        {
            return false;
        }

        _ = _page!.Content.BeginArtifact();
        _inArtifact = true;
        return true;
    }

    /// <summary>
    /// Begins a pagination artifact with no subtype when the document is tagged and no artifact is open, for content drawn again because the page broke
    /// (a table's header rows at the top of a later page); returns whether it began one, for <see cref="EndArtifact"/>.
    /// </summary>
    private bool BeginRepeatedArtifact()
    {
        if (_writer.Structure is null || _inArtifact)
        {
            return false;
        }

        _ = _page!.Content.BeginPaginationArtifact(PdfPaginationArtifact.Repeated);
        _inArtifact = true;
        return true;
    }

    /// <summary>Ends the artifact <see cref="BeginArtifact"/> or <see cref="BeginRepeatedArtifact"/> began, when it began one.</summary>
    private void EndArtifact(bool began)
    {
        if (began)
        {
            _ = _page!.Content.EndMarkedContent();
            _inArtifact = false;
        }
    }

    /// <summary>The embedded form of a font of the collection in this document, made on first use and kept (the document keeps one for each font, see <see cref="PdfDocumentWriter.GetOrAddShared"/>).</summary>
    private PdfEmbeddedFont EmbeddedFont(int index)
    {
        return _embeddedFonts[index] ?? MakeEmbeddedFont(index);
    }

    /// <summary>The embedded font of the font at <paramref name="index"/> the first time it is asked for, apart from <see cref="EmbeddedFont"/> so that the closure it makes is made only then and not at every glyph run.</summary>
    private PdfEmbeddedFont MakeEmbeddedFont(int index)
    {
        TrueTypeFont font = _fonts.Entries[index].Font;
        PdfEmbeddedFont made = _writer.GetOrAddShared(font, () => new PdfEmbeddedFont(_writer, font));
        _embeddedFonts[index] = made;
        return made;
    }

    private void RecordMissing(StyledLayout styled, int firstLine, int count, int pageNumber)
    {
        ParagraphLayout layout = styled.Layout;
        if (layout.Missing.Count == 0)
        {
            return;
        }

        for (int l = firstLine; l < firstLine + count; l++)
        {
            LaidOutLine line = layout.Lines[l];
            foreach (int index in layout.Missing)
            {
                if (index < line.Start || index >= line.Start + line.Length)
                {
                    continue;
                }

                int codePoint = char.IsHighSurrogate(layout.Text[index]) && index + 1 < layout.Text.Length
                    ? char.ConvertToUtf32(layout.Text[index], layout.Text[index + 1])
                    : layout.Text[index];
                string? language = styled.LanguageAt(index);
                AddMissing(codePoint, language, pageNumber);
            }
        }
    }

    private void AddMissing(int codePoint, string? language, int pageNumber)
    {
        // Tagged, each page with a missing glyph is an accessibility issue, whatever the cap of the list of glyphs: its .notdef is in the content (PDF/UA-1 7.21.8).
        if (_accessibility is not null && (_glyphIssuePages ??= []).Add(pageNumber))
        {
            _accessibility.Add(new AccessibilityIssue(
                AccessibilityIssueKind.MissingGlyph,
                pageNumber,
                $"No font has a glyph for U+{codePoint:X4}, drawn as the font's .notdef glyph, which PDF/UA-1 forbids (7.21.8)."));
        }

        if (_missing.Count < MaxReportedGlyphs && _missingSeen.Add((codePoint, language, pageNumber)))
        {
            _missing.Add(new MissingGlyph(codePoint, language, pageNumber));
        }
    }

    private void NoteFamily(string family)
    {
        if (!_families.Contains(family) && !_missingFamilies.Contains(family, StringComparer.OrdinalIgnoreCase))
        {
            _missingFamilies.Add(family);
        }
    }

    private void OpenPage()
    {
        if (_options.Limits.MaxPages is { } max && _writer.PageCount + 1 > max)
        {
            throw new PdfLimitExceededException(DocumentLimit.MaxPages, max);
        }

        _page = _writer.BeginPage(_pageWidth, _pageHeight);
        _y = 0;
        _hasContent = false;
    }

    private void ClosePage()
    {
        if (_page is null)
        {
            return;
        }

        NoteNotdef(_page.Index + 1);
        if (_overlay)
        {
            _page.EndBody(_options.Header is not null, _options.Footer is not null);
            _pending.Add(_page);
        }
        else
        {
            _page.End();
        }

        _page = null;
        _y = 0;
        _hasContent = false;
        CheckBytes();
        _cancellation.ThrowIfCancellationRequested();
    }

    private void CheckBytes()
    {
        if (_options.Limits.MaxBytes is { } max && _output.Count > max)
        {
            throw new PdfLimitExceededException(DocumentLimit.MaxBytes, max);
        }
    }

    /// <summary>
    /// Counts what passes through (so that the size of the file is known without a seekable stream) and gathers the writer's many small writes into blocks
    /// before they reach the destination. It never closes the destination.
    /// </summary>
    private sealed class CountingStream(Stream inner) : Stream
    {
        private readonly byte[] _buffer = new byte[64 * 1024];
        private int _filled;

        internal long Count { get; private set; }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
            if (_filled > 0)
            {
                inner.Write(_buffer, 0, _filled);
                _filled = 0;
            }

            inner.Flush();
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Count += buffer.Length;
            if (buffer.Length >= _buffer.Length)
            {
                if (_filled > 0)
                {
                    inner.Write(_buffer, 0, _filled);
                    _filled = 0;
                }

                inner.Write(buffer);
                return;
            }

            if (_filled + buffer.Length > _buffer.Length)
            {
                inner.Write(_buffer, 0, _filled);
                _filled = 0;
            }

            buffer.CopyTo(_buffer.AsSpan(_filled));
            _filled += buffer.Length;
        }
    }
}
