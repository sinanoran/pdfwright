using System.Text;

namespace Pdfwright.Tests.Document;

/// <summary>Saving: determinism, limits, the asynchronous path, cancellation, and what a document does when it is used wrongly.</summary>
public class DocumentSaveTests
{
    private static PdfDocument Big(Action<DocumentTestKit.OptionsBuilder>? change = null, int paragraphs = 6, int words = 1500)
    {
        PdfDocument document = new(DocumentTestKit.Options(change));
        document.Add(new Heading(1, "Report"));
        for (int i = 0; i < paragraphs; i++)
        {
            document.Add(new Paragraph(DocumentTestKit.LongText(words, "item" + i + "_")));
        }

        return document;
    }

    /// <summary>A stream that runs an action on its first write and otherwise throws the bytes away.</summary>
    private sealed class HookStream(Action onWrite) : Stream
    {
        private bool _hooked;

        internal long Written { get; private set; }

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
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            Written += count;
            if (!_hooked)
            {
                _hooked = true;
                onWrite();
            }
        }
    }

    /// <summary>A stream that can only be written to asynchronously: a synchronous write or flush is a failure.</summary>
    private sealed class AsyncOnlyStream : Stream
    {
        private readonly MemoryStream _inner = new();

        internal int AsyncWrites { get; private set; }

        internal byte[] Bytes => _inner.ToArray();

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new InvalidOperationException("synchronous flush");

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new InvalidOperationException("synchronous write to the caller's stream");

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            AsyncWrites++;
            await _inner.WriteAsync(buffer, cancellationToken);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    [Fact]
    public void Two_saves_of_a_deterministic_document_are_the_same_bytes()
    {
        PdfDocument document = Big(o => o.Deterministic = true);

        byte[] first = DocumentTestKit.Save(document);
        byte[] second = DocumentTestKit.Save(document);

        Assert.True(first.AsSpan().SequenceEqual(second));
        Assert.Equal(first.Length, document.Diagnostics.ByteCount);
    }

    [Fact]
    public void Two_documents_built_the_same_way_are_the_same_bytes_with_or_without_compression()
    {
        foreach (bool compress in new[] { false, true })
        {
            byte[] a = DocumentTestKit.Save(Big(o => { o.Deterministic = true; o.Compress = compress; }, 2, 300));
            byte[] b = DocumentTestKit.Save(Big(o => { o.Deterministic = true; o.Compress = compress; }, 2, 300));

            Assert.True(a.AsSpan().SequenceEqual(b), $"compress {compress}");
        }
    }

    [Fact]
    public void Compression_makes_the_file_smaller_and_it_still_reads_back()
    {
        byte[] plain = DocumentTestKit.Save(Big(o => o.Compress = false, 2, 300));
        byte[] compressed = DocumentTestKit.Save(Big(o => o.Compress = true, 2, 300));

        Assert.True(compressed.Length < plain.Length);
        Assert.Equal(DocumentTestKit.PageTexts(plain), DocumentTestKit.PageTexts(compressed));
        DocumentTestKit.AssertValid(compressed, "save-compressed");
    }

    [Fact]
    public void A_deterministic_file_has_no_date_unless_a_creation_is_given_and_the_other_has_the_time_of_the_save()
    {
        string Raw(Action<DocumentTestKit.OptionsBuilder> change)
        {
            PdfDocument d = new(DocumentTestKit.Options(change));
            d.Add(new Paragraph("x"));
            return Encoding.Latin1.GetString(DocumentTestKit.Save(d));
        }

        Assert.DoesNotContain("/CreationDate", Raw(o => o.Deterministic = true), StringComparison.Ordinal);
        Assert.Contains("/CreationDate (D:20260101000000", Raw(o => { o.Deterministic = true; o.Creation = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero); }), StringComparison.Ordinal);
        Assert.Contains("/CreationDate (D:", Raw(o => o.Deterministic = false), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveAsync_writes_the_same_file_as_Save_and_never_writes_to_the_stream_synchronously()
    {
        PdfDocument document = Big(o => o.Deterministic = true, 3, 800);
        byte[] expected = DocumentTestKit.Save(document);
        AsyncOnlyStream destination = new();

        await document.SaveAsync(destination, TestContext.Current.CancellationToken);

        Assert.True(destination.AsyncWrites > 0);
        Assert.True(expected.AsSpan().SequenceEqual(destination.Bytes));
        Assert.Equal(expected.Length, document.Diagnostics.ByteCount);
        DocumentTestKit.AssertValid(destination.Bytes, "save-async");
    }

    [Fact]
    public async Task SaveAsync_does_not_close_the_stream_and_can_be_repeated()
    {
        PdfDocument document = Big(o => o.Deterministic = true, 1, 200);
        using MemoryStream stream = new();

        await document.SaveAsync(stream, TestContext.Current.CancellationToken);
        long first = stream.Length;
        await document.SaveAsync(stream, TestContext.Current.CancellationToken);

        Assert.True(stream.CanWrite);
        Assert.Equal(2 * first, stream.Length);
    }

    [Fact]
    public async Task A_token_that_is_already_cancelled_writes_nothing()
    {
        PdfDocument document = Big();
        AsyncOnlyStream destination = new();
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => document.SaveAsync(destination, cancelled.Token));

        Assert.Equal(0, destination.AsyncWrites);
        Assert.Empty(destination.Bytes);
    }

    [Fact]
    public async Task A_render_cancelled_in_the_middle_of_the_layout_stops_with_an_OperationCanceledException()
    {
        // The destination cancels the token when the first block of the file reaches it, which is after several pages have been laid out and written.
        PdfDocument document = Big(o => o.Compress = false, 8, 3000);
        using CancellationTokenSource source = new();
        FakeDestination destination = new(source.Cancel);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.Run(() => RenderThrough(document, destination, source.Token), TestContext.Current.CancellationToken));

        Assert.True(destination.Written > 0);
        Assert.True(destination.Written < 2_000_000, "the render stopped long before the end");
    }

    private sealed class FakeDestination(Action onFirstWrite) : Stream
    {
        private readonly HookStream _hook = new(onFirstWrite);

        internal long Written => _hook.Written;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => _hook.Flush();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => _hook.Write(buffer, offset, count);
    }

    /// <summary>Runs the renderer itself (the internal one) with a token and a destination, to cancel in the middle of a layout.</summary>
    private static void RenderThrough(PdfDocument document, Stream destination, CancellationToken token)
    {
        System.Reflection.FieldInfo options = typeof(PdfDocument).GetField("_options", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        System.Reflection.FieldInfo blocks = typeof(PdfDocument).GetField("_blocks", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        Pdfwright.Document.DocumentRenderer.Render((PdfDocumentOptions)options.GetValue(document)!, (List<Block>)blocks.GetValue(document)!, destination, token);
    }

    [Fact]
    public void A_page_limit_throws_a_typed_exception_when_the_page_after_it_would_start()
    {
        PdfDocument document = Big(o => o.Limits = new DocumentLimits { MaxPages = 2 });

        PdfLimitExceededException error = Assert.Throws<PdfLimitExceededException>(() => DocumentTestKit.Save(document));

        Assert.Equal(DocumentLimit.MaxPages, error.Limit);
        Assert.Equal(2, error.Maximum);
        Assert.IsAssignableFrom<InvalidOperationException>(error);
    }

    [Fact]
    public void A_document_that_just_fits_the_page_limit_saves()
    {
        PdfDocument document = new(DocumentTestKit.Options(o => o.Limits = new DocumentLimits { MaxPages = 2 }));
        document.Add(new Paragraph("A")).Add(new PageBreak()).Add(new Paragraph("B"));

        Assert.Equal(["A", "B"], DocumentTestKit.PageTexts(DocumentTestKit.Save(document)));
    }

    [Fact]
    public void A_byte_limit_throws_a_typed_exception()
    {
        PdfDocument document = Big(o => o.Limits = new DocumentLimits { MaxBytes = 20_000 });

        PdfLimitExceededException error = Assert.Throws<PdfLimitExceededException>(() => DocumentTestKit.Save(document));

        Assert.Equal(DocumentLimit.MaxBytes, error.Limit);
        Assert.Equal(20_000, error.Maximum);
    }

    [Fact]
    public async Task A_limit_reached_in_SaveAsync_surfaces_the_same_exception_and_writes_nothing()
    {
        PdfDocument document = Big(o => o.Limits = new DocumentLimits { MaxPages = 1 });
        AsyncOnlyStream destination = new();

        PdfLimitExceededException error = await Assert.ThrowsAsync<PdfLimitExceededException>(() => document.SaveAsync(destination, TestContext.Current.CancellationToken));

        Assert.Equal(DocumentLimit.MaxPages, error.Limit);
        Assert.Empty(destination.Bytes);
    }

    [Fact]
    public void A_limit_of_zero_is_refused_when_the_document_is_made()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PdfDocument(DocumentTestKit.Options(o => o.Limits = new DocumentLimits { MaxPages = 0 })));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PdfDocument(DocumentTestKit.Options(o => o.Limits = new DocumentLimits { MaxBytes = 0 })));
    }

    [Fact]
    public void A_document_cannot_be_changed_or_saved_again_while_it_is_being_saved_and_works_afterwards()
    {
        PdfDocument document = Big(o => o.Deterministic = true, 1, 100);
        InvalidOperationException? add = null;
        InvalidOperationException? save = null;
        HookStream stream = new(() =>
        {
            add = Assert.Throws<InvalidOperationException>(() => document.Add(new Paragraph("late")));
            save = Assert.Throws<InvalidOperationException>(() => document.Save(Stream.Null));
        });

        document.Save(stream);

        Assert.NotNull(add);
        Assert.NotNull(save);
        Assert.Contains("being saved", add.Message, StringComparison.Ordinal);
        int blocks = document.BlockCount;
        document.Add(new Paragraph("after"));
        Assert.Equal(blocks + 1, document.BlockCount);
        byte[] again = DocumentTestKit.Save(document);
        Assert.Contains("after", string.Concat(DocumentTestKit.PageTexts(again)), StringComparison.Ordinal);
    }

    [Fact]
    public void A_failed_save_leaves_the_document_usable()
    {
        PdfDocument document = Big(o => o.Limits = new DocumentLimits { MaxPages = 1 });

        Assert.Throws<PdfLimitExceededException>(() => DocumentTestKit.Save(document));
        Assert.Throws<PdfLimitExceededException>(() => DocumentTestKit.Save(document));
    }

    [Fact]
    public async Task Saving_needs_a_writable_stream()
    {
        PdfDocument document = Big(null, 1, 10);
        using MemoryStream readOnly = new([], writable: false);

        Assert.Throws<ArgumentNullException>(() => document.Save(null!));
        Assert.Throws<ArgumentException>(() => document.Save(readOnly));
        await Assert.ThrowsAsync<ArgumentException>(() => document.SaveAsync(readOnly, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Save_works_on_a_stream_that_cannot_seek_and_leaves_it_open()
    {
        PdfDocument document = Big(null, 1, 100);
        using HookStream stream = new(() => { });

        document.Save(stream);

        Assert.True(stream.CanWrite);
        Assert.Equal(document.Diagnostics.ByteCount, stream.Written);
    }

    [Fact]
    public void The_file_does_not_depend_on_the_culture_of_the_thread()
    {
        byte[] Save()
        {
            PdfDocument d = new(DocumentTestKit.Options());
            d.Add(new Heading(2, "Culture 12,5 and 1.234"));
            d.Add(new Paragraph([new TextRun("a", new TextStyle { Size = Length.Pt(10.5) })]));
            return DocumentTestKit.Save(d);
        }

        System.Globalization.CultureInfo before = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("en-US");
            byte[] english = Save();
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("tr-TR");
            byte[] turkish = Save();
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            byte[] german = Save();

            Assert.True(english.AsSpan().SequenceEqual(turkish));
            Assert.True(english.AsSpan().SequenceEqual(german));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = before;
        }
    }
}
