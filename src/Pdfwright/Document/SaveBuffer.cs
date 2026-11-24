using System.Buffers;

namespace Pdfwright.Document;

/// <summary>
/// The file being saved, held in memory until it is complete (a document that fails writes nothing to the caller's stream): a write-only stream of
/// blocks of 64 KiB borrowed from <see cref="ArrayPool{T}.Shared"/> and given back, cleared, when it is disposed. A <see cref="MemoryStream"/> would
/// grow by doubling and copy the file at each step, which costs about twice the file in allocation at every save, and a tagged file is many times the
/// size of an untagged one (its structure tree is written uncompressed); the blocks are never copied, and are the same blocks from one save to the next.
/// </summary>
internal sealed class SaveBuffer : Stream
{
    /// <summary>The size of a block: under the threshold of the large object heap, and the size of the blocks the renderer writes.</summary>
    private const int BlockSize = 64 * 1024;

    private readonly List<byte[]> _blocks = [];

    /// <summary>The bytes written in the last block.</summary>
    private int _filled = BlockSize;

    private long _length;

    private bool _disposed;

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => !_disposed;

    public override long Length => _length;

    public override long Position
    {
        get => _length;
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        while (!buffer.IsEmpty)
        {
            if (_filled == BlockSize)
            {
                _blocks.Add(ArrayPool<byte>.Shared.Rent(BlockSize));
                _filled = 0;
            }

            int take = Math.Min(buffer.Length, BlockSize - _filled);
            buffer[..take].CopyTo(_blocks[^1].AsSpan(_filled));
            _filled += take;
            _length += take;
            buffer = buffer[take..];
        }
    }

    public override void WriteByte(byte value) => Write([value]);

    /// <summary>Writes every byte held to <paramref name="destination"/>, block by block.</summary>
    internal void WriteTo(Stream destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        for (int i = 0; i < _blocks.Count; i++)
        {
            destination.Write(_blocks[i], 0, Used(i));
        }
    }

    /// <summary>Writes every byte held to <paramref name="destination"/>, block by block, asynchronously.</summary>
    internal async Task WriteToAsync(Stream destination, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        for (int i = 0; i < _blocks.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await destination.WriteAsync(_blocks[i].AsMemory(0, Used(i)), cancellationToken).ConfigureAwait(false);
        }
    }

    private int Used(int block) => block == _blocks.Count - 1 ? _filled : BlockSize;

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;

            // Cleared: the blocks go back to a pool the whole process shares, and the file may be someone's report.
            foreach (byte[] block in _blocks)
            {
                ArrayPool<byte>.Shared.Return(block, clearArray: true);
            }

            _blocks.Clear();
        }

        base.Dispose(disposing);
    }
}
