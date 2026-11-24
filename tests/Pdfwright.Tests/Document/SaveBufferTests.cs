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
}
