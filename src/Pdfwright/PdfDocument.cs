using Pdfwright.Document;

namespace Pdfwright;

/// <summary>
/// A document: blocks that flow from the top of the first page down, breaking onto new pages as they fill. Add blocks, then save to a stream.
/// A document is not thread-safe: do not use one instance from several threads at once (adding while a save runs, or two saves at once, throws
/// <see cref="InvalidOperationException"/>). A document can be saved more than once, and a second save writes the same file as the first
/// when <see cref="PdfDocumentOptions.Deterministic"/> is set. The <see cref="FontCollection"/> may be shared between documents; it must not be disposed while one is saving.
/// </summary>
public sealed class PdfDocument
{
    private readonly PdfDocumentOptions _options;
    private readonly List<Block> _blocks = [];
    private int _saving;
    private DocumentDiagnostics _diagnostics = DocumentDiagnostics.Empty;

    /// <summary>Makes an empty document.</summary>
    /// <param name="options">What the document is made with. The options are read when the document is made and again at each save, so do not change the font collection's role in between.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The options are not usable: no font in the collection, or margins that leave no room on the page.</exception>
    public PdfDocument(PdfDocumentOptions options)
    {
        DocumentRenderer.Validate(options);
        _options = options;
    }

    /// <summary>The number of blocks added so far.</summary>
    public int BlockCount => _blocks.Count;

    /// <summary>What the last completed save found (missing glyphs, the page count and the number of bytes), or an empty report before the first save.</summary>
    public DocumentDiagnostics Diagnostics => _diagnostics;

    /// <summary>Adds a block after the ones already added.</summary>
    /// <param name="block">The block.</param>
    /// <returns>This document, so that calls can be chained.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="block"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The document is being saved.</exception>
    public PdfDocument Add(Block block)
    {
        ArgumentNullException.ThrowIfNull(block);
        if (Volatile.Read(ref _saving) != 0)
        {
            throw new InvalidOperationException("The document is being saved and cannot be changed.");
        }

        _blocks.Add(block);
        return this;
    }

    /// <summary>
    /// Lays the document out and writes it to <paramref name="destination"/> as a PDF. The file is made in memory first (so the memory held is about the size of the file) and written to the
    /// stream only when it is complete, so a document that fails, or hits one of its limits, writes nothing to the stream.
    /// </summary>
    /// <param name="destination">A writable stream; it need not be seekable and is not closed.</param>
    /// <exception cref="ArgumentNullException"><paramref name="destination"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The stream cannot be written to.</exception>
    /// <exception cref="InvalidOperationException">The document is already being saved.</exception>
    /// <exception cref="ObjectDisposedException">The font collection has been disposed.</exception>
    /// <exception cref="PdfLimitExceededException">A limit of <see cref="PdfDocumentOptions.Limits"/> was hit; nothing is written to the stream.</exception>
    public void Save(Stream destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
        {
            throw new ArgumentException("The stream cannot be written to.", nameof(destination));
        }

        Block[] blocks = Begin();
        try
        {
            using MemoryStream buffer = new();
            DocumentDiagnostics report = DocumentRenderer.Render(_options, blocks, buffer, CancellationToken.None);
            buffer.Position = 0;
            buffer.CopyTo(destination);
            destination.Flush();
            _diagnostics = report;
        }
        finally
        {
            Volatile.Write(ref _saving, 0);
        }
    }

    /// <summary>
    /// Lays the document out and writes it to <paramref name="destination"/> without blocking the calling thread on the stream. The file is made in memory
    /// first (so the memory held is about the size of the file) on a thread-pool thread, and then copied to the stream asynchronously, so the caller's stream is
    /// never written to from the synchronous writer. The token is checked between blocks and pages and while copying.
    /// </summary>
    /// <param name="destination">A writable stream; it need not be seekable and is not closed.</param>
    /// <param name="cancellationToken">Cancels the save; <see cref="OperationCanceledException"/> is thrown and nothing is written to the stream if the layout was not finished.</param>
    /// <returns>A task that completes when the file has been written.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="destination"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The stream cannot be written to.</exception>
    /// <exception cref="InvalidOperationException">The document is already being saved.</exception>
    /// <exception cref="ObjectDisposedException">The font collection has been disposed.</exception>
    /// <exception cref="PdfLimitExceededException">A limit of <see cref="PdfDocumentOptions.Limits"/> was hit; nothing is written to the stream.</exception>
    public async Task SaveAsync(Stream destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
        {
            throw new ArgumentException("The stream cannot be written to.", nameof(destination));
        }

        cancellationToken.ThrowIfCancellationRequested();
        Block[] blocks = Begin();
        try
        {
            using MemoryStream buffer = new();
            DocumentDiagnostics report = await Task.Run(() => DocumentRenderer.Render(_options, blocks, buffer, cancellationToken), cancellationToken).ConfigureAwait(false);
            buffer.Position = 0;
            await buffer.CopyToAsync(destination, 81920, cancellationToken).ConfigureAwait(false);
            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            _diagnostics = report;
        }
        finally
        {
            Volatile.Write(ref _saving, 0);
        }
    }

    private Block[] Begin()
    {
        if (Interlocked.CompareExchange(ref _saving, 1, 0) != 0)
        {
            throw new InvalidOperationException("The document is already being saved.");
        }

        return [.. _blocks];
    }
}
