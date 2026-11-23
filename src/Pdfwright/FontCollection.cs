using System.Collections.Concurrent;
using Pdfwright.Fonts;
using Pdfwright.Text;

namespace Pdfwright;

/// <summary>
/// The fonts a document may use: supplied by the application, never looked up in the operating system. A collection parses each font once
/// when it is added (family, weight and style are read from the font itself) and can be shared by any number of documents on any number of
/// threads; adding to it while documents are being saved is safe, and a save sees the fonts the collection held when the save began.
/// </summary>
/// <remarks>
/// Fonts must be TrueType (<c>glyf</c> outlines); OpenType fonts with CFF outlines are refused. Fonts whose licence forbids embedding
/// (OS/2 <c>fsType</c> restricted, no subsetting, or bitmap only) are refused with <see cref="FontNotEmbeddableException"/>.
/// A font is chosen for each run by family, then weight and style, then by coverage: when the first font cannot draw a character, the
/// rest of the collection is tried (the fonts of the same family first), so a Latin family and an Arabic family can share a paragraph.
/// A collection holds a native copy of every font for the shaping engine, which is released by <see cref="Dispose"/>: it is meant to live long (for the life of the application, or of
/// a service), shared by every document, and not to be made for each document. After it is disposed, adding a font, measuring with it and saving a document made with it throw
/// <see cref="ObjectDisposedException"/>; do not dispose it while a document is being saved with it.
/// </remarks>
public sealed class FontCollection : IDisposable
{
    private readonly object _gate = new();
    private Snapshot _snapshot = new([]);
    private bool _disposed;

    /// <summary>The number of fonts in the collection.</summary>
    public int Count => _snapshot.Entries.Length;

    /// <summary>Releases the native copies of the fonts. The collection cannot be used afterwards; calling this again does nothing.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (FontEntry entry in _snapshot.Entries)
            {
                entry.Shaping.Dispose();
            }
        }
    }

    /// <summary>The distinct font families in the collection, in the order their first font was added.</summary>
    public IReadOnlyList<string> Families => [.. _snapshot.Entries.Select(e => e.Family).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>Adds a font from the bytes of a TrueType file.</summary>
    /// <param name="fontData">The font file. It is copied; the array may be changed afterwards.</param>
    /// <returns>This collection, so that calls can be chained.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="fontData"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The data is not a usable TrueType font (including a font with CFF outlines).</exception>
    /// <exception cref="FontNotEmbeddableException">The font's licence does not allow embedding a subset.</exception>
    public FontCollection Add(byte[] fontData)
    {
        ArgumentNullException.ThrowIfNull(fontData);
        return Add(new ReadOnlyMemory<byte>(fontData));
    }

    /// <summary>Adds a font from the bytes of a TrueType file.</summary>
    /// <param name="fontData">The font file. It is copied.</param>
    /// <returns>This collection, so that calls can be chained.</returns>
    /// <exception cref="ArgumentException">The data is not a usable TrueType font (including a font with CFF outlines).</exception>
    /// <exception cref="FontNotEmbeddableException">The font's licence does not allow embedding a subset.</exception>
    public FontCollection Add(ReadOnlyMemory<byte> fontData)
    {
        TrueTypeFont font;
        try
        {
            font = TrueTypeFont.Parse(fontData.ToArray());
        }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException)
        {
            throw new ArgumentException("The data is not a usable TrueType font: " + error.Message, nameof(fontData), error);
        }

        if (!font.MayEmbedSubset)
        {
            throw new FontNotEmbeddableException(font.FamilyName, font.EmbeddingPermission, nameof(fontData));
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            foreach (FontEntry existing in _snapshot.Entries)
            {
                if (existing.Font.Bytes.SequenceEqual(font.Bytes))
                {
                    return this; // the same file again: kept once
                }
            }

            ShapingFont shaping = new(font);
            _snapshot = new Snapshot([.. _snapshot.Entries, new FontEntry(font, shaping, font.FamilyName, font.WeightClass, font.IsItalic)]);
        }

        return this;
    }

    /// <summary>The fonts as they are now: an immutable view that a document works from for the whole of a save.</summary>
    internal Snapshot Current
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _snapshot;
        }
    }

    /// <summary>A parsed font and what the collection knows about it.</summary>
    internal sealed record FontEntry(TrueTypeFont Font, ShapingFont Shaping, string Family, int Weight, bool Italic);

    /// <summary>An immutable list of fonts with the order of preference for each style asked for, worked out once.</summary>
    internal sealed class Snapshot(FontEntry[] entries)
    {
        private readonly ConcurrentDictionary<(string Family, int Weight, bool Italic), int[]> _orders = new();
        private readonly ConcurrentDictionary<(string Family, int Weight, bool Italic), int[]> _ordersBySpelling = new();

        /// <summary>The most spellings of a family that are remembered as they were written.</summary>
        private const int MaxSpellings = 256;

        private int _spellings;

        /// <summary>The most styles whose <see cref="RunStyle"/> is remembered; a document uses a handful, so the limit only keeps an endless variety of styles from growing the table.</summary>
        private const int MaxRunStyles = 256;

        private readonly ConcurrentDictionary<Document.ResolvedStyle, RunStyle> _runStyles = new();

        private int _runStyleCount;

        internal FontEntry[] Entries { get; } = entries;

        internal IReadOnlyList<ShapingFont> Shaping { get; } = [.. entries.Select(e => e.Shaping)];

        /// <summary>
        /// The indices of the fonts in the order they are tried for text of a style: the fonts of the family first, then all the others, each group
        /// ordered by the style (an upright font for upright text, an italic one for italic), then by the distance of its weight from the weight asked for, then by
        /// the order they were added in.
        /// </summary>
        internal int[] Order(string family, int weight, bool italic)
        {
            // The family as it was spelled is looked up first, which costs nothing: the upper-case key of the second table is a new string for every call.
            if (_ordersBySpelling.TryGetValue((family, weight, italic), out int[]? known))
            {
                return known;
            }

            int[] order = _orders.GetOrAdd((family.ToUpperInvariant(), weight, italic), key =>
                [.. Enumerable.Range(0, Entries.Length)
                    .OrderBy(i => string.Equals(Entries[i].Family, family, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                    .ThenBy(i => Entries[i].Italic == italic ? 0 : 1)
                    .ThenBy(i => Math.Abs(Entries[i].Weight - weight))
                    .ThenBy(i => i)]);
            // The count is kept apart because ConcurrentDictionary.Count takes every lock of the table, which would make each miss past the limit wait for all the threads. Threads that read it
            // together may each add one more, so the table can pass the limit by about the number of threads, which does not matter: the limit only keeps an endless variety of spellings from growing it.
            if (Volatile.Read(ref _spellings) < MaxSpellings && _ordersBySpelling.TryAdd((family, weight, italic), order))
            {
                _ = Interlocked.Increment(ref _spellings);
            }

            return order;
        }

        /// <summary>
        /// What the shaper and the line builder need of a resolved style: the order of fonts, the size, the language and the colour. A <see cref="RunStyle"/> is never changed after it is made (its order is the shared array of
        /// <see cref="Order"/>), so the cells of a table that share a style share the one object instead of each building its own. Safe for many threads; the table is bounded like the one of <see cref="Order"/>.
        /// </summary>
        internal RunStyle RunStyleOf(Document.ResolvedStyle style)
        {
            if (_runStyles.TryGetValue(style, out RunStyle? known))
            {
                return known;
            }

            RunStyle made = new()
            {
                FontOrder = Order(style.Family, style.Weight, style.Italic),
                Size = style.Size,
                Language = style.Language,
                Color = style.Color,
            };
            if (Volatile.Read(ref _runStyleCount) < MaxRunStyles && _runStyles.TryAdd(style, made))
            {
                _ = Interlocked.Increment(ref _runStyleCount);
            }

            return made;
        }
    }
}

/// <summary>A font was refused because its licence does not allow embedding it in a document (the OS/2 <c>fsType</c> restricted-license, no-subsetting or bitmap-only bits).</summary>
public sealed class FontNotEmbeddableException : ArgumentException
{
    internal FontNotEmbeddableException(string family, int embeddingPermission, string paramName)
        : base($"The font '{family}' may not be embedded: its OS/2 fsType is 0x{embeddingPermission:X4}, which forbids it (restricted license, no subsetting, or bitmap only).", paramName)
    {
        Family = family;
        EmbeddingPermission = embeddingPermission;
    }

    /// <summary>The family name of the refused font.</summary>
    public string Family { get; }

    /// <summary>The font's OS/2 <c>fsType</c> value.</summary>
    public int EmbeddingPermission { get; }
}
