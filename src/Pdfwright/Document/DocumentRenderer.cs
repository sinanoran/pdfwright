using Pdfwright.Pdf;
using Pdfwright.Text;

namespace Pdfwright.Document;

/// <summary>
/// Lays the blocks of a document out from top to bottom inside the margins and writes the pages. It is the flow: it decides where a block goes, where a
/// paragraph is split between pages, and keeps a heading with the first line of what follows. One renderer is one save.
/// </summary>
internal sealed class DocumentRenderer
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
    private readonly HashSet<string> _families;

    private PdfPageWriter? _page;
    private double _y;
    private bool _hasContent;

    private DocumentRenderer(PdfDocumentOptions options, IReadOnlyList<Block> blocks, FontCollection.Snapshot fonts, Stream output, CancellationToken cancellation)
    {
        _options = options;
        _blocks = blocks;
        _fonts = fonts;
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
        });
    }

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
        _writer.Complete();

        // The fonts are written by Complete, after the last page: this is the check that holds the limit. The caller's stream has not been touched, so nothing of an oversized file escapes.
        CheckBytes();
        return new DocumentDiagnostics(_missing.AsReadOnly(), _missingFamilies.AsReadOnly(), _writer.PageCount, _output.Count);
    }

    /// <summary>A block ready to place: its lines and its spacing. Blocks that have no text have no layout.</summary>
    private sealed class Prepared
    {
        internal StyledLayout? Text { get; init; }

        internal double Before { get; init; }

        internal double After { get; init; }

        internal bool KeepWithNext { get; init; }

        /// <summary>The height of the first line of the block, or what the block takes to start a page for the purpose of keeping a heading with it.</summary>
        internal double FirstLineHeight => Text is { Layout.Lines.Count: > 0 } t ? t.Layout.Lines[0].Pitch : 0;
    }

    private Prepared Prepare(Block block)
    {
        switch (block)
        {
            case Paragraph paragraph:
            {
                ResolvedStyle style = _base.Apply(paragraph.Style);
                StyledLayout layout = BlockText.Layout(paragraph.Runs, style, _fonts, _contentWidth, paragraph.Alignment, paragraph.Direction ?? _options.Direction, paragraph.LineSpacing);
                NoteFamilies(layout);
                return new Prepared
                {
                    Text = layout,
                    Before = Space(paragraph.SpaceBefore, 0),
                    After = Space(paragraph.SpaceAfter, style.Size * 0.6),
                };
            }

            case Heading heading:
            {
                ResolvedStyle style = _headings[heading.Level - 1];
                StyledLayout layout = BlockText.Layout(heading.Runs, style, _fonts, _contentWidth, heading.Alignment, heading.Direction ?? _options.Direction, 1.0);
                NoteFamilies(layout);
                return new Prepared
                {
                    Text = layout,
                    Before = Space(heading.SpaceBefore, style.Size),
                    After = Space(heading.SpaceAfter, style.Size * 0.4),
                    KeepWithNext = true,
                };
            }

            default:
                return new Prepared();
        }
    }

    private static double Space(Length? length, double fallback) => length is { } value ? value.Points : fallback;

    /// <summary>Records the families that the styles of a laid out block name and the collection does not have (the block was set in other fonts), once each in order of first use.</summary>
    private void NoteFamilies(StyledLayout layout)
    {
        foreach (ResolvedStyle style in layout.Styles)
        {
            if (!_families.Contains(style.Family) && !_missingFamilies.Contains(style.Family, StringComparer.OrdinalIgnoreCase))
            {
                _missingFamilies.Add(style.Family);
            }
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
            double rest = lines.Sum(l => l.Pitch) + block.After + (next?.Text is not null ? next.Before + next.FirstLineHeight : 0);
            if (rest <= _contentHeight && _y + block.Before + rest > _contentHeight)
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

    private void Draw(StyledLayout styled, int firstLine, int count)
    {
        ParagraphLayout layout = styled.Layout;
        PdfPageWriter page = _page!;
        PdfEmbeddedFont?[] embedded = new PdfEmbeddedFont?[_fonts.Entries.Length];
        string?[] names = new string?[_fonts.Entries.Length];
        HashSet<int> used = [.. layout.Lines.Skip(firstLine).Take(count).SelectMany(l => l.Runs).Select(r => r.Run.FontIndex)];
        foreach (int index in used)
        {
            FontCollection.FontEntry entry = _fonts.Entries[index];
            PdfEmbeddedFont font = _writer.GetOrAddShared(entry.Font, () => new PdfEmbeddedFont(_writer, entry.Font));
            embedded[index] = font;
            names[index] = page.UseFont(font.Reference);
        }

        layout.Draw(page.Content, embedded, names, _left, _pageHeight - _top - _y, firstLine, count);
        RecordMissing(styled, firstLine, count, page.Index + 1);
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
                if (_missing.Count < MaxReportedGlyphs && _missingSeen.Add((codePoint, language, pageNumber)))
                {
                    _missing.Add(new MissingGlyph(codePoint, language, pageNumber));
                }
            }
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

        _page.End();
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
