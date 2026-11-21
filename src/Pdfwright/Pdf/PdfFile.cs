using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;

namespace Pdfwright.Pdf;

/// <summary>Whether the writer compresses the streams it writes.</summary>
internal enum PdfCompression
{
    /// <summary>Flate (zlib) where it makes a stream smaller. The bytes of a compressed stream depend on the runtime's zlib.</summary>
    Flate,

    /// <summary>
    /// Streams as they are. The bytes of the file then depend on nothing but the input, on every runtime, which is what a
    /// golden file needs, and a content stream can be read with any text editor.
    /// </summary>
    None,
}

/// <summary>
/// The file structure of ISO 32000-1, section 7.5: a header, indirect objects written one after another to a
/// <see cref="Stream"/> that need not be seekable, a cross-reference <b>stream</b> (PDF 1.5) and a trailer. Nothing is held
/// but the offset of each object, so the memory of a document is the pages not yet written.
/// </summary>
/// <remarks>
/// The document <c>/ID</c> is the SHA-256 of every byte written before the cross-reference stream, so it identifies the
/// content and two equal documents are equal files, with no clock and no random number in them.
/// </remarks>
internal sealed class PdfFile : IDisposable
{
    private const int CompressionThreshold = 64;

    private readonly HashingStream _output;
    private readonly PdfCompression _compression;
    private readonly List<long> _offsets = [-1];
    private bool _finished;

    // Set when writing an object failed halfway: its bytes are in the file and cannot be taken back, so nothing more is written.
    private bool _faulted;

    internal PdfFile(Stream output, PdfCompression compression)
    {
        ArgumentNullException.ThrowIfNull(output);
        _output = new HashingStream(output);
        _compression = compression;

        PdfSerializer.Ascii(_output, "%PDF-1.7\n");

        // A comment of four bytes above 127 tells transfer tools that the file is binary.
        _output.Write([(byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n']);
    }

    /// <summary>Reserves the number of an object to be written later, so that other objects can refer to it first.</summary>
    internal PdfReference Allocate()
    {
        _offsets.Add(-1);
        return new PdfReference(_offsets.Count - 1);
    }

    /// <summary>Writes an object under a number that <see cref="Allocate"/> returned.</summary>
    internal void Write(PdfReference reference, PdfObject value)
    {
        if (_finished)
        {
            throw new InvalidOperationException("The file is finished.");
        }

        if (_faulted)
        {
            throw new InvalidOperationException("An earlier object failed halfway and is in the file as partial bytes: the file cannot be continued.");
        }

        if (reference.Number < 1 || reference.Number >= _offsets.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(reference), reference.Number, $"No object {reference.Number} was allocated (1 to {_offsets.Count - 1}).");
        }

        if (_offsets[reference.Number] >= 0)
        {
            throw new InvalidOperationException($"Object {reference.Number} was written twice.");
        }

        long start = _output.Position;
        try
        {
            PdfSerializer.Ascii(_output, reference.Number.ToString(System.Globalization.CultureInfo.InvariantCulture) + " 0 obj\n");
            if (value is PdfStream stream)
            {
                WriteStreamBody(stream);
            }
            else
            {
                PdfSerializer.Write(value, _output);
            }

            PdfSerializer.Ascii(_output, "\nendobj\n");
        }
        catch
        {
            _faulted = true;
            throw;
        }

        _offsets[reference.Number] = start;
    }

    /// <summary>Allocates a number and writes the object under it.</summary>
    internal PdfReference Add(PdfObject value)
    {
        PdfReference reference = Allocate();
        Write(reference, value);
        return reference;
    }

    /// <summary>
    /// Writes the cross-reference stream and the trailer. Every number that was allocated must have been written: a
    /// reference to nothing is a defect of the document, and is reported here rather than left for a viewer to find.
    /// </summary>
    internal void Finish(PdfReference root, PdfReference? info)
    {
        if (_finished)
        {
            throw new InvalidOperationException("The file is finished.");
        }

        if (_faulted)
        {
            throw new InvalidOperationException("An earlier object failed halfway and is in the file as partial bytes: the file cannot be finished.");
        }

        for (int number = 1; number < _offsets.Count; number++)
        {
            if (_offsets[number] < 0)
            {
                throw new InvalidOperationException($"Object {number} was allocated and never written.");
            }
        }

        byte[] id = _output.Hash();
        PdfReference xref = Allocate();
        long xrefOffset = _output.Position;
        _offsets[xref.Number] = xrefOffset;

        // Fields: type (1 byte), offset or next free (4 bytes, or 8 when the file is 4 GiB or more), generation (2 bytes).
        int offsetBytes = _offsets.Max() > uint.MaxValue ? 8 : 4;
        int entrySize = 1 + offsetBytes + 2;
        byte[] table = new byte[_offsets.Count * entrySize];
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(1 + offsetBytes, 2), 65535);
        for (int number = 1; number < _offsets.Count; number++)
        {
            Span<byte> entry = table.AsSpan(number * entrySize, entrySize);
            entry[0] = 1;
            if (offsetBytes == 4)
            {
                BinaryPrimitives.WriteUInt32BigEndian(entry[1..5], (uint)_offsets[number]);
            }
            else
            {
                BinaryPrimitives.WriteUInt64BigEndian(entry[1..9], (ulong)_offsets[number]);
            }
        }

        PdfArray ids = new(new PdfString(id, hex: true), new PdfString(id, hex: true));
        PdfDictionary dictionary = new PdfDictionary()
            .Set("Type", "XRef")
            .Set("Size", _offsets.Count)
            .Set("W", PdfArray.Of(1, offsetBytes, 2))
            .Set("Root", root)
            .Set("ID", ids);
        if (info is not null)
        {
            dictionary.Set("Info", info);
        }

        _finished = true;
        PdfSerializer.Ascii(_output, xref.Number.ToString(System.Globalization.CultureInfo.InvariantCulture) + " 0 obj\n");
        WriteStreamBody(new PdfStream(dictionary, table));
        PdfSerializer.Ascii(_output, "\nendobj\n");
        PdfSerializer.Ascii(_output, "startxref\n" + xrefOffset.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n%%EOF\n");
        _output.Flush();
    }

    public void Dispose() => _output.Dispose();

    private void WriteStreamBody(PdfStream stream)
    {
        ReadOnlyMemory<byte> data = stream.Data;
        PdfDictionary dictionary = new();
        foreach (KeyValuePair<string, PdfObject> entry in stream.Dictionary.Entries)
        {
            dictionary.Set(entry.Key, entry.Value);
        }

        // A stream that already has a filter (a JPEG with /DCTDecode, for one) is written as it is: adding Flate over it would
        // leave the dictionary saying one filter for bytes that need two, and /DecodeParms belong to the filter they were written for.
        if (_compression == PdfCompression.Flate && !stream.NeverCompress && dictionary["Filter"] is null && data.Length >= CompressionThreshold)
        {
            byte[] deflated = Deflate(data.Span);
            if (deflated.Length < data.Length)
            {
                data = deflated;
                dictionary.Set("Filter", "FlateDecode");
            }
        }

        dictionary.Set("Length", data.Length);
        PdfSerializer.WriteDictionary(dictionary, _output);
        PdfSerializer.Ascii(_output, "\nstream\n");
        _output.Write(data.Span);
        PdfSerializer.Ascii(_output, "\nendstream");
    }

    private static byte[] Deflate(ReadOnlySpan<byte> data)
    {
        using MemoryStream compressed = new();
        using (ZLibStream zlib = new(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(data);
        }

        return compressed.ToArray();
    }

    /// <summary>A write-only stream that counts the bytes written and hashes them, so the output need not be seekable.</summary>
    private sealed class HashingStream(Stream inner) : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private long _position;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        internal byte[] Hash() => _hash.GetCurrentHash()[..16];

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            inner.Write(buffer);
            _hash.AppendData(buffer);
            _position += buffer.Length;
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _hash.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
