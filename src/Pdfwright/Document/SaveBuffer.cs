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

    /// <summary>
    /// The size of file from which its SHA-256 (the document's <c>/ID</c>) is taken with the framework's hash instead of the library's own: the library's is managed code at about 200 MB/s, which
    /// costs a file of this size about 10 ms, and the framework's runs at over 1 GB/s on the processor's SHA instructions but loads its cryptography assembly and the operating system's library
    /// (about 1 MB of working set) into the process. A file of ordinary size never loads them; a large one (pictures, long uncompressed tables) does not pay the managed hash for every byte.
    /// The two give the same digest, which the tests check.
    /// </summary>
    internal const long FrameworkHashThreshold = 2 * 1024 * 1024;

    /// <summary>The SHA-256 of the first <paramref name="length"/> bytes held: with the library's hash below <paramref name="frameworkThreshold"/> bytes, the framework's from there on.</summary>
    internal byte[] Sha256(long length, long frameworkThreshold = FrameworkHashThreshold)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, _length);
        return length < frameworkThreshold ? ManagedSha256(length) : FrameworkSha256(length);
    }

    private byte[] ManagedSha256(long length)
    {
        Pdf.Sha256 hash = new();
        long left = length;
        for (int i = 0; left > 0; i++)
        {
            int take = (int)Math.Min(left, Used(i));
            hash.Append(_blocks[i].AsSpan(0, take));
            left -= take;
        }

        return hash.GetHashAndReset();
    }

    /// <summary>The framework's SHA-256, in a method of its own so that the cryptography assembly is loaded only when a file is large enough to use it.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private byte[] FrameworkSha256(long length)
    {
        using System.Security.Cryptography.IncrementalHash hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        long left = length;
        for (int i = 0; left > 0; i++)
        {
            int take = (int)Math.Min(left, Used(i));
            hash.AppendData(_blocks[i].AsSpan(0, take));
            left -= take;
        }

        return hash.GetHashAndReset();
    }

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
