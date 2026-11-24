using System.Buffers;
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
/// <para>
/// The document <c>/ID</c> is the SHA-256 of every byte written before the cross-reference stream, so it identifies the
/// content and two equal documents are equal files, with no clock and no random number in them.
/// </para>
/// <para>
/// With <b>object streams</b> (ISO 32000-1, 7.5.7), which a tagged document asks for and which are used only when the file is compressed, the
/// objects written by <see cref="WritePacked(int, ReadOnlySpan{byte})"/> go into compressed object streams of up to <see cref="PackedObjectLimit"/>
/// objects instead of one after another: a tagged table has an object for each structure element, thousands of small dictionaries that compress
/// well together and not at all one by one. The objects of a stream are held until it is full, so the memory is one stream's worth and not the
/// document's, and the cross-reference stream gives each object its stream and its index there. Without object streams, and for every object
/// written by <see cref="Write"/> or <see cref="WriteRaw"/>, the file is the one a writer without them makes.
/// </para>
/// </remarks>
internal sealed class PdfFile : IDisposable
{
    private const int CompressionThreshold = 64;

    /// <summary>
    /// The most objects in one object stream. More compress a little better and hold more in memory until the stream is written. Measured on the
    /// tagged 1,000-row table: 195 KB at 100, 190 KB at 200, 186 KB at 400, 184 KB at 1,000; at 400 the open stream of that table holds about
    /// 35 KB of structure elements.
    /// </summary>
    internal const int PackedObjectLimit = 400;

    /// <summary>
    /// The bytes of objects after which an object stream is written even when it holds fewer than <see cref="PackedObjectLimit"/> (long <c>/Alt</c>
    /// texts, say), so that what the open stream holds is bounded. It is checked after each object and an object is never split, so a stream holds at
    /// most this plus its last object. The buffer of <see cref="_packed"/> therefore can grow past the large object heap's threshold (85,000 bytes):
    /// a batch near the limit followed by an object of some 40 KB, or one larger object on its own (the <c>/K</c> of the <c>TBody</c> of a table of
    /// 10,000 rows is about 116 KB). That costs a larger buffer, kept for the rest of the save, and nothing else.
    /// </summary>
    internal const int PackedByteLimit = 48 * 1024;

    /// <summary>The level object streams are compressed at.</summary>
    private const CompressionLevel PackedCompression = CompressionLevel.SmallestSize;

    private readonly HashingStream _output;
    private readonly PdfCompression _compression;
    private readonly OffsetTable _offsets = new();
    private bool _finished;

    // Set when writing an object failed halfway: its bytes are in the file and cannot be taken back, so nothing more is written.
    private bool _faulted;

    /// <summary>Whether <see cref="WritePacked(int, ReadOnlySpan{byte})"/> puts objects in object streams: they were asked for, and the file is compressed.</summary>
    private readonly bool _objectStreams;

    /// <summary>The number of the object stream being filled, or 0 when none is open.</summary>
    private int _packedNumber;

    /// <summary>The numbers of the objects of the open object stream, and their offsets in <see cref="_packed"/>; <see cref="_packedCount"/> of each are used.</summary>
    private readonly int[] _packedNumbers;
    private readonly int[] _packedOffsets;
    private int _packedCount;

    /// <summary>
    /// The bytes of the objects of the open object stream, one after another; kept from one stream to the next. Usually under 64 KB, it grows to hold
    /// whatever object comes, past the large object heap's threshold for a large one (see <see cref="PackedByteLimit"/>).
    /// </summary>
    private readonly MemoryStream _packed = new();

    /// <summary>The pairs of numbers that begin an object stream (each object's number and offset); kept from one stream to the next.</summary>
    private readonly MemoryStream _packedHeader = new();

    /// <param name="output">Where the file is written.</param>
    /// <param name="compression">Whether streams are compressed.</param>
    /// <param name="objectStreams">
    /// Whether the objects written by <see cref="WritePacked(int, ReadOnlySpan{byte})"/> go into object streams. Only when <paramref name="compression"/> is
    /// <see cref="PdfCompression.Flate"/>: an uncompressed file stays one that a text editor can read, object by object.
    /// </param>
    internal PdfFile(Stream output, PdfCompression compression, bool objectStreams = false)
    {
        ArgumentNullException.ThrowIfNull(output);
        _output = new HashingStream(output);
        _compression = compression;
        _objectStreams = objectStreams && compression == PdfCompression.Flate;
        _packedNumbers = _objectStreams ? new int[PackedObjectLimit] : [];
        _packedOffsets = _objectStreams ? new int[PackedObjectLimit] : [];

        PdfSerializer.Ascii(_output, "%PDF-1.7\n");

        // A comment of four bytes above 127 tells transfer tools that the file is binary.
        _output.Write([(byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n']);
    }

    /// <summary>Reserves the number of an object to be written later, so that other objects can refer to it first.</summary>
    internal PdfReference Allocate() => new(AllocateNumber());

    /// <summary>Reserves the number of an object to be written later, as <see cref="Allocate"/> does, without making a reference.</summary>
    internal int AllocateNumber()
    {
        _offsets.Add(-1);
        return _offsets.Count - 1;
    }

    /// <summary>Writes an object under a number that <see cref="Allocate"/> returned.</summary>
    internal void Write(PdfReference reference, PdfObject value)
    {
        CheckWritable(reference.Number, nameof(reference));
        long start = _output.Position;
        try
        {
            PdfSerializer.Integer(_output, reference.Number);
            _output.Write(" 0 obj\n"u8);
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

    /// <summary>
    /// Writes an object, given as the bytes of its value (what the serializer would write for it), under a number that <see cref="AllocateNumber"/>
    /// returned: <c>n 0 obj</c>, the bytes and <c>endobj</c>, as <see cref="Write"/> frames an object.
    /// </summary>
    internal void WriteRaw(int number, ReadOnlySpan<byte> value)
    {
        CheckWritable(number, nameof(number));
        long start = _output.Position;
        try
        {
            PdfSerializer.Integer(_output, number);
            _output.Write(" 0 obj\n"u8);
            _output.Write(value);
            _output.Write("\nendobj\n"u8);
        }
        catch
        {
            _faulted = true;
            throw;
        }

        _offsets[number] = start;
    }

    /// <summary>
    /// Writes an object, given as the bytes of its value, as <see cref="WriteRaw"/> does; with object streams, into the object stream being filled
    /// instead, which is written when it is full or when the file is finished. The value is not a stream: a stream cannot be in an object stream.
    /// </summary>
    internal void WritePacked(int number, ReadOnlySpan<byte> value)
    {
        if (!_objectStreams)
        {
            WriteRaw(number, value);
            return;
        }

        CheckWritable(number, nameof(number));
        try
        {
            int offset = StartPacked();
            _packed.Write(value);
            EndPacked(number, offset);
        }
        catch
        {
            _faulted = true;
            throw;
        }
    }

    /// <summary>Writes an object that is not a stream as <see cref="Write"/> does; with object streams, into the object stream being filled, as <see cref="WritePacked(int, ReadOnlySpan{byte})"/> does.</summary>
    internal void WritePacked(PdfReference reference, PdfObject value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value is PdfStream)
        {
            throw new ArgumentException("A stream cannot be in an object stream.", nameof(value));
        }

        if (!_objectStreams)
        {
            Write(reference, value);
            return;
        }

        CheckWritable(reference.Number, nameof(reference));
        try
        {
            int offset = StartPacked();
            PdfSerializer.Write(value, _packed);
            EndPacked(reference.Number, offset);
        }
        catch
        {
            _faulted = true;
            throw;
        }
    }

    /// <summary>Opens an object stream if none is open, reserving its number, and returns the offset of the next object in it.</summary>
    private int StartPacked()
    {
        if (_packedNumber == 0)
        {
            _packedNumber = AllocateNumber();
        }
        else
        {
            // A line between two objects, which a reader does not need between a dictionary and what follows, but which keeps them apart to the eye.
            _packed.WriteByte((byte)'\n');
        }

        return (int)_packed.Length;
    }

    /// <summary>Records an object put in the open object stream, and writes the stream when it is full.</summary>
    private void EndPacked(int number, int offset)
    {
        _packedNumbers[_packedCount] = number;
        _packedOffsets[_packedCount] = offset;
        _offsets[number] = InObjectStream(_packedNumber, _packedCount);
        _packedCount++;
        if (_packedCount == PackedObjectLimit || _packed.Length >= PackedByteLimit)
        {
            FlushPacked();
        }
    }

    /// <summary>
    /// Writes the open object stream, if there is one: <c>/Type /ObjStm</c>, <c>/N</c> objects, <c>/First</c> the offset of the first one, and a body
    /// of <c>/N</c> pairs of an object number and its offset from <c>/First</c>, then the objects (ISO 32000-1, 7.5.7), compressed as any stream is.
    /// </summary>
    private void FlushPacked()
    {
        if (_packedNumber == 0)
        {
            return;
        }

        MemoryStream header = _packedHeader;
        header.SetLength(0);
        for (int i = 0; i < _packedCount; i++)
        {
            if (i > 0)
            {
                header.WriteByte((byte)' ');
            }

            PdfSerializer.Integer(header, _packedNumbers[i]);
            header.WriteByte((byte)' ');
            PdfSerializer.Integer(header, _packedOffsets[i]);
        }

        header.WriteByte((byte)'\n');
        PdfReference stream = new(_packedNumber);
        PdfDictionary dictionary = new PdfDictionary()
            .Set("Type", "ObjStm")
            .Set("N", _packedCount)
            .Set("First", (int)header.Length);
        _packedNumber = 0;
        _packedCount = 0;

        // Compressed here, the pairs of numbers and then the objects with no copy of them made, at the smallest size rather than the writer's
        // default: it takes about 3 percent off the object streams of a tagged table, which are a small part of the time of a save.
        ReadOnlySpan<byte> pairs = header.GetBuffer().AsSpan(0, (int)header.Length);
        ReadOnlySpan<byte> objects = _packed.GetBuffer().AsSpan(0, (int)_packed.Length);
        ReadOnlyMemory<byte> data = Deflate(pairs, objects, PackedCompression);
        if (data.Length < pairs.Length + objects.Length)
        {
            dictionary.Set("Filter", "FlateDecode");
        }
        else
        {
            byte[] plain = new byte[pairs.Length + objects.Length];
            pairs.CopyTo(plain);
            objects.CopyTo(plain.AsSpan(pairs.Length));
            data = plain;
        }

        _packed.SetLength(0);
        Write(stream, new PdfStream(dictionary, data));
    }

    /// <summary>
    /// The entry of the offset table of an object in an object stream, below -1 (-1 is an object not yet written, 0 and above the offset of one
    /// written in the file itself): the stream's number and the object's index in it.
    /// </summary>
    private static long InObjectStream(int stream, int index) => -2 - (((long)stream << 16) | (uint)index);

    private void CheckWritable(int number, string parameter)
    {
        if (_finished)
        {
            throw new InvalidOperationException("The file is finished.");
        }

        if (_faulted)
        {
            throw new InvalidOperationException("An earlier object failed halfway and is in the file as partial bytes: the file cannot be continued.");
        }

        if (number < 1 || number >= _offsets.Count)
        {
            throw new ArgumentOutOfRangeException(parameter, number, $"No object {number} was allocated (1 to {_offsets.Count - 1}).");
        }

        if (_offsets[number] != -1)
        {
            throw new InvalidOperationException($"Object {number} was written twice.");
        }
    }

    /// <summary>
    /// Writes a content stream (a stream with no entries of its own in its dictionary) from the pieces a <see cref="ContentStreamBuilder"/> holds it in, as <see cref="Write"/> writes a
    /// <see cref="PdfStream"/> of the same bytes: compressed when that makes it smaller, and the same bytes either way, without the pieces being joined first.
    /// </summary>
    internal void WriteContent(PdfReference reference, ContentStreamBuilder content)
    {
        CheckWritable(reference.Number, nameof(reference));
        long start = _output.Position;
        try
        {
            PdfSerializer.Integer(_output, reference.Number);
            _output.Write(" 0 obj\n"u8);
            PdfDictionary dictionary = new();
            int length = content.Length;
            if (_compression == PdfCompression.Flate && length >= CompressionThreshold)
            {
                ReadOnlyMemory<byte> deflated = Deflate(content, CompressionLevel.Optimal);
                if (deflated.Length < length)
                {
                    dictionary.Set("Filter", "FlateDecode").Set("Length", deflated.Length);
                    WriteStreamData(dictionary, deflated.Span);
                    PdfSerializer.Ascii(_output, "\nendobj\n");
                    _offsets[reference.Number] = start;
                    return;
                }
            }

            dictionary.Set("Length", length);
            PdfSerializer.WriteDictionary(dictionary, _output);
            PdfSerializer.Ascii(_output, "\nstream\n");
            for (int i = 0; i < content.SegmentCount; i++)
            {
                _output.Write(content.Segment(i));
            }

            PdfSerializer.Ascii(_output, "\nendstream");
            PdfSerializer.Ascii(_output, "\nendobj\n");
        }
        catch
        {
            _faulted = true;
            throw;
        }

        _offsets[reference.Number] = start;
    }

    /// <summary>Allocates a number and writes a content stream under it, as <see cref="WriteContent"/> does.</summary>
    internal PdfReference AddContent(ContentStreamBuilder content)
    {
        PdfReference reference = Allocate();
        WriteContent(reference, content);
        return reference;
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

        // The last object stream is written first: its objects are written, and its own number was allocated for it.
        FlushPacked();
        for (int number = 1; number < _offsets.Count; number++)
        {
            if (_offsets[number] == -1)
            {
                throw new InvalidOperationException($"Object {number} was allocated and never written.");
            }
        }

        byte[] id = _output.Hash();
        PdfReference xref = Allocate();
        long xrefOffset = _output.Position;
        _offsets[xref.Number] = xrefOffset;

        // Fields: type (1 byte); offset, next free or object stream (4 bytes, or 8 when the file is 4 GiB or more); generation or index in the
        // object stream (2 bytes: an object stream holds at most PackedObjectLimit objects).
        int offsetBytes = _offsets.Max() > uint.MaxValue ? 8 : 4;
        int entrySize = 1 + offsetBytes + 2;

        // With object streams, each row of the table begins with the byte of its predictor (written below).
        int lead = _objectStreams ? 1 : 0;
        int rowSize = lead + entrySize;
        byte[] table = new byte[_offsets.Count * rowSize];
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(lead + 1 + offsetBytes, 2), 65535);
        for (int number = 1; number < _offsets.Count; number++)
        {
            Span<byte> entry = table.AsSpan((number * rowSize) + lead, entrySize);
            long field = _offsets[number];
            entry[0] = 1;
            if (field < -1)
            {
                // Type 2, an object in an object stream: the stream's number, and the object's index in it in place of the generation.
                long packed = -2 - field;
                entry[0] = 2;
                field = packed >> 16;
                BinaryPrimitives.WriteUInt16BigEndian(entry[(1 + offsetBytes)..], (ushort)(packed & 0xFFFF));
            }

            if (offsetBytes == 4)
            {
                BinaryPrimitives.WriteUInt32BigEndian(entry[1..5], (uint)field);
            }
            else
            {
                BinaryPrimitives.WriteUInt64BigEndian(entry[1..9], (ulong)field);
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

        ReadOnlyMemory<byte> data = table;
        if (_objectStreams)
        {
            // A table with an entry for each structure element: each row is written as its difference from the row above (the PNG "Up" predictor,
            // ISO 32000-1, 7.4.4.4), which leaves mostly zeros (the next index in the same object stream) and compresses to a tenth of the plain
            // rows. In place, from the last row up, so that each row is taken from the row above while that one is still as it was.
            for (int row = _offsets.Count - 1; row >= 0; row--)
            {
                int at = row * rowSize;
                table[at] = 2;
                for (int i = 1; row > 0 && i < rowSize; i++)
                {
                    table[at + i] -= table[at - rowSize + i];
                }
            }

            data = Deflate(table, CompressionLevel.Optimal);
            dictionary
                .Set("Filter", "FlateDecode")
                .Set("DecodeParms", new PdfDictionary().Set("Predictor", 12).Set("Columns", entrySize));
        }

        _finished = true;
        PdfSerializer.Ascii(_output, xref.Number.ToString(System.Globalization.CultureInfo.InvariantCulture) + " 0 obj\n");
        WriteStreamBody(new PdfStream(dictionary, data));
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
            ReadOnlyMemory<byte> deflated = Deflate(data.Span, CompressionLevel.Optimal);
            if (deflated.Length < data.Length)
            {
                data = deflated;
                dictionary.Set("Filter", "FlateDecode");
            }
        }

        dictionary.Set("Length", data.Length);
        WriteStreamData(dictionary, data.Span);
    }

    /// <summary>The dictionary of a stream (its <c>/Length</c> set), and the stream's data between <c>stream</c> and <c>endstream</c>.</summary>
    private void WriteStreamData(PdfDictionary dictionary, ReadOnlySpan<byte> data)
    {
        PdfSerializer.WriteDictionary(dictionary, _output);
        PdfSerializer.Ascii(_output, "\nstream\n");
        _output.Write(data);
        PdfSerializer.Ascii(_output, "\nendstream");
    }

    /// <summary>
    /// The data compressed, in a buffer the file keeps for the next stream: the bytes are valid until the next call, and a stream's bytes are
    /// written before the next stream is compressed. A new buffer for every stream would cost each stream about three times its compressed size.
    /// </summary>
    private ReadOnlyMemory<byte> Deflate(ReadOnlySpan<byte> data, CompressionLevel level)
    {
        MemoryStream compressed = _deflated ??= new MemoryStream();
        compressed.SetLength(0);
        using (ZLibStream zlib = new(compressed, level, leaveOpen: true))
        {
            zlib.Write(data);
        }

        return compressed.GetBuffer().AsMemory(0, (int)compressed.Length);
    }

    /// <summary>Two pieces of data compressed as one, one after the other, into the buffer <see cref="Deflate(ReadOnlySpan{byte}, CompressionLevel)"/> uses.</summary>
    private ReadOnlyMemory<byte> Deflate(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second, CompressionLevel level)
    {
        MemoryStream compressed = _deflated ??= new MemoryStream();
        compressed.SetLength(0);
        using (ZLibStream zlib = new(compressed, level, leaveOpen: true))
        {
            zlib.Write(first);
            zlib.Write(second);
        }

        return compressed.GetBuffer().AsMemory(0, (int)compressed.Length);
    }

    /// <summary>
    /// The pieces of a content stream compressed as one, into the buffer <see cref="Deflate(ReadOnlySpan{byte}, CompressionLevel)"/> uses. The compressor is given the whole stream in one write, as it
    /// was before the stream was held in pieces: the runtime's zlib does not write the same bytes for the same data given in several writes, so a stream of several pieces is first copied into one
    /// buffer borrowed from the shared array pool (given back, cleared, at once), which a long page needs at about the size of its stream once in a process rather than at every save.
    /// </summary>
    private ReadOnlyMemory<byte> Deflate(ContentStreamBuilder content, CompressionLevel level)
    {
        if (content.SegmentCount == 1)
        {
            return Deflate(content.Segment(0), level);
        }

        byte[] whole = ArrayPool<byte>.Shared.Rent(content.Length);
        try
        {
            int at = 0;
            for (int i = 0; i < content.SegmentCount; i++)
            {
                ReadOnlySpan<byte> segment = content.Segment(i);
                segment.CopyTo(whole.AsSpan(at));
                at += segment.Length;
            }

            return Deflate(whole.AsSpan(0, at), level);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(whole, clearArray: true);
        }
    }

    private MemoryStream? _deflated;

    /// <summary>
    /// The offset of each object by number (-1 until it is written, below -1 for an object in an object stream; entry 0 is the head of the free list), in blocks of 256 that are never copied: a
    /// tagged document has an object for each structure element, thousands of them, and a list that grows by doubling copies them at each step.
    /// </summary>
    private sealed class OffsetTable
    {
        private const int BlockSize = 256;

        private readonly List<long[]> _blocks = [];

        internal OffsetTable() => Add(-1);

        internal int Count { get; private set; }

        internal long this[int number]
        {
            get => _blocks[number / BlockSize][number % BlockSize];
            set => _blocks[number / BlockSize][number % BlockSize] = value;
        }

        internal void Add(long offset)
        {
            if (Count % BlockSize == 0)
            {
                _blocks.Add(new long[BlockSize]);
            }

            _blocks[^1][Count % BlockSize] = offset;
            Count++;
        }

        internal long Max()
        {
            long max = long.MinValue;
            for (int number = 0; number < Count; number++)
            {
                max = Math.Max(max, this[number]);
            }

            return max;
        }
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
