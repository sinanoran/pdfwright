using Pdfwright.Document;

namespace Pdfwright.Tests.Document;

/// <summary>The buffer a save writes the file into before the caller's stream: blocks of 64 KiB, written out in order, whatever the sizes of the writes.</summary>
public class SaveBufferTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(65535)]
    [InlineData(65536)]
    [InlineData(65537)]
    [InlineData(300_001)]
    public async Task What_is_written_comes_out_the_same_bytes_in_order(int length)
    {
        byte[] data = new byte[length];
        new Random(length).NextBytes(data);

        using SaveBuffer buffer = Fill(data);
        Assert.Equal(length, buffer.Length);
        using MemoryStream copy = new();
        buffer.WriteTo(copy);
        Assert.Equal(data, copy.ToArray());

        using MemoryStream asyncCopy = new();
        await buffer.WriteToAsync(asyncCopy, TestContext.Current.CancellationToken);
        Assert.Equal(data, asyncCopy.ToArray());
    }

    /// <summary>Writes of every size, small and over a block, so that they fall across the blocks' edges.</summary>
    private static SaveBuffer Fill(byte[] data)
    {
        SaveBuffer buffer = new();
        int offset = 0;
        int step = 1;
        while (offset < data.Length)
        {
            int count = Math.Min(step, data.Length - offset);
            buffer.Write(data, offset, count);
            offset += count;
            step = (step * 7 % 100_003) + 1;
        }

        return buffer;
    }

    [Fact]
    public void A_disposed_buffer_is_written_no_more()
    {
        SaveBuffer buffer = new();
        buffer.Write([1, 2, 3]);
        buffer.Dispose();
        Assert.Throws<ObjectDisposedException>(() => buffer.Write([4]));
        Assert.Throws<ObjectDisposedException>(() => buffer.WriteTo(new MemoryStream()));
    }

    /// <summary>The size of a block of the buffer.</summary>
    private const int BlockSize = 64 * 1024;

    [Fact]
    public void The_blocks_go_back_to_the_pool_cleared()
    {
        // Several blocks of a byte that is never 0, then given back.
        using (SaveBuffer buffer = new())
        {
            byte[] data = new byte[(BlockSize * 3) + 1000];
            Array.Fill(data, (byte)0xAB);
            buffer.Write(data);
        }

        AssertLastBlockGivenBackIsCleared();
    }

    [Fact]
    public void A_save_that_hits_its_byte_limit_after_several_blocks_writes_nothing_and_gives_its_blocks_back_cleared()
    {
        const long limit = 400_000;
        Assert.True(DocumentTestKit.Save(LongDocument(null)).Length > 2 * limit, "the document is not long enough to pass the limit after several blocks");

        using MemoryStream destination = new();
        PdfLimitExceededException error = Assert.Throws<PdfLimitExceededException>(() => LongDocument(limit).Save(destination));

        Assert.Equal(DocumentLimit.MaxBytes, error.Limit);
        Assert.Equal(0, destination.Length);
        AssertLastBlockGivenBackIsCleared();
    }

    [Fact]
    public async Task A_save_cancelled_after_several_blocks_of_the_render_writes_nothing()
    {
        // The rows are streamed and the enumerator cancels the token at a row far into the table, when the uncompressed file is several blocks long.
        using CancellationTokenSource source = new();
        int pulled = 0;
        IEnumerable<TableRow> Rows()
        {
            for (int i = 0; i < 100_000; i++)
            {
                pulled++;
                if (i == 5000)
                {
                    source.Cancel();
                }

                yield return new TableRow($"row {i}", $"value {i} of a table cancelled while it is laid out");
            }
        }

        PdfDocument document = new(DocumentTestKit.Options());
        document.Add(new Table([new(ColumnWidth.Fixed(Length.Pt(120))), new(ColumnWidth.Fixed(Length.Pt(300)))], new TableRow("Name", "Value"), Rows()));
        using MemoryStream destination = new();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => document.SaveAsync(destination, source.Token));

        Assert.InRange(pulled, 5001, 5003);
        Assert.Equal(0, destination.Length);
    }

    /// <summary>A document of many pages of uncompressed text, with a byte limit or none.</summary>
    private static PdfDocument LongDocument(long? maxBytes)
    {
        PdfDocument document = new(DocumentTestKit.Options(o => o.Limits = new DocumentLimits { MaxBytes = maxBytes }));
        for (int i = 0; i < 40; i++)
        {
            document.Add(new Paragraph(DocumentTestKit.LongText(1000, $"p{i}w")));
        }

        return document;
    }

    /// <summary>
    /// Rents a block's size from the shared pool on this thread and checks that it holds only zeros. The pool keeps the array given back last on a
    /// thread in that thread's own slot and hands it out first, so what is rented is the last block the buffer gave back (on this thread, so the
    /// tests that call this are synchronous); were the blocks not cleared, it would hold the file's bytes.
    /// </summary>
    private static void AssertLastBlockGivenBackIsCleared()
    {
        byte[] block = System.Buffers.ArrayPool<byte>.Shared.Rent(BlockSize);
        try
        {
            Assert.Equal(BlockSize, block.Length);
            Assert.Equal(-1, block.AsSpan().IndexOfAnyExcept((byte)0));
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(block, clearArray: true);
        }
    }
}
