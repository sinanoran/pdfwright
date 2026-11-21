using System.Collections.ObjectModel;

namespace Pdfwright;

/// <summary>What a <see cref="PdfDocument"/> is made with: the fonts, the page, the default text style and the document's metadata. A value that cannot be used is refused when it is set, naming the property.</summary>
public sealed class PdfDocumentOptions
{
    private readonly FontCollection _fonts = null!;
    private readonly IReadOnlyList<TextStyle>? _headingStyles;
    private readonly PageSize _pageSize = PageSize.A4;
    private readonly Margins _margins = Margins.Mm(top: 20, right: 15, bottom: 20, left: 15);
    private readonly TextDirection _direction = TextDirection.Auto;
    private readonly DocumentLimits _limits = new();

    /// <summary>The fonts the document may use. Required; the collection must hold at least one font when the document is created.</summary>
    /// <exception cref="ArgumentNullException">The value is <see langword="null"/>.</exception>
    public required FontCollection Fonts
    {
        get => _fonts;
        init => _fonts = value ?? throw new ArgumentNullException(nameof(Fonts));
    }

    /// <summary>The style that applies to all text before a paragraph's or a run's own: family, size, weight, colour and language. Unset properties get the library's defaults (the first font of the collection, 10 points, regular, black).</summary>
    public TextStyle? DefaultStyle { get; init; }

    /// <summary>
    /// The style of headings level 1 to 6, as a list of exactly six styles (index 0 is level 1), or <see langword="null"/> for the default scale: bold at 2.0, 1.6, 1.3,
    /// 1.15, 1.0 and 1.0 times the default size. A style's unset properties are taken from the default style. The list is copied when it is set, so changing the caller's list afterwards has no effect.
    /// </summary>
    /// <exception cref="ArgumentException">The list does not hold exactly six styles, or one of them is <see langword="null"/>.</exception>
    public IReadOnlyList<TextStyle>? HeadingStyles
    {
        get => _headingStyles;
        init
        {
            if (value is null)
            {
                _headingStyles = null;
                return;
            }

            TextStyle[] copy = [.. value];
            if (copy.Length != 6)
            {
                throw new ArgumentException("HeadingStyles has exactly six styles, for levels 1 to 6.", nameof(HeadingStyles));
            }

            if (copy.Any(s => s is null))
            {
                throw new ArgumentException("A heading style is null.", nameof(HeadingStyles));
            }

            _headingStyles = new ReadOnlyCollection<TextStyle>(copy);
        }
    }

    /// <summary>The size of the pages. A4 by default.</summary>
    /// <exception cref="ArgumentException">The page size was never set (<c>default(PageSize)</c>).</exception>
    public PageSize PageSize
    {
        get => _pageSize;
        init => _pageSize = value == default ? throw new ArgumentException("The page size is not set: make one with new PageSize(width, height) or take PageSize.A4.", nameof(PageSize)) : value;
    }

    /// <summary>The margins. 20 mm at the top and bottom and 15 mm at the sides by default.</summary>
    /// <exception cref="ArgumentException">A margin is negative or not finite.</exception>
    public Margins Margins
    {
        get => _margins;
        init
        {
            if (!value.Top.IsFinite || !value.Right.IsFinite || !value.Bottom.IsFinite || !value.Left.IsFinite || value.Top < Length.Zero || value.Right < Length.Zero || value.Bottom < Length.Zero || value.Left < Length.Zero)
            {
                throw new ArgumentException("Margins are finite and not negative.", nameof(Margins));
            }

            _margins = value;
        }
    }

    /// <summary>The document's title, shown by readers in the window title.</summary>
    public string? Title { get; init; }

    /// <summary>The document's subject.</summary>
    public string? Subject { get; init; }

    /// <summary>The document's author.</summary>
    public string? Author { get; init; }

    /// <summary>The application that made the content (this library is recorded as the producer).</summary>
    public string? Creator { get; init; }

    /// <summary>The document's language as a BCP 47 tag, which a screen reader uses to choose its voice. It is also the language of text whose style sets none.</summary>
    public string? Language { get; init; }

    /// <summary>The base direction of paragraphs that set none of their own. <see cref="TextDirection.Auto"/> (the default) takes it from each paragraph's first strong character.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a <see cref="TextDirection"/>.</exception>
    public TextDirection Direction
    {
        get => _direction;
        init => _direction = Check.Defined(value, nameof(Direction));
    }

    /// <summary>
    /// Whether the output is a pure function of the document: the clock is never read, so two saves of the same document are the same bytes. With it, a file has the
    /// creation date <see cref="Creation"/> when that is set and no date at all when it is not; without it and without <see cref="Creation"/>, the date is the time of the save.
    /// The file's <c>/ID</c> is a hash of its content either way. See <see cref="Compress"/> for byte-identical files across .NET versions.
    /// </summary>
    public bool Deterministic { get; init; }

    /// <summary>
    /// The creation and modification date written to the metadata, whenever it is set (with <see cref="Deterministic"/> or without it). When it is not set, a deterministic
    /// document has no date and any other document has the time of the save.
    /// </summary>
    public DateTimeOffset? Creation { get; init; }

    /// <summary>
    /// Whether streams are compressed (Flate). On by default. The compressed bytes depend on the zlib of the .NET runtime, so a file saved with compression
    /// is the same bytes on the same runtime but may differ between runtimes; switch it off for golden files that must match everywhere.
    /// </summary>
    public bool Compress { get; init; } = true;

    /// <summary>Limits that stop a runaway document with a <see cref="PdfLimitExceededException"/>. None by default.</summary>
    /// <exception cref="ArgumentNullException">The value is <see langword="null"/>.</exception>
    public DocumentLimits Limits
    {
        get => _limits;
        init => _limits = value ?? throw new ArgumentNullException(nameof(Limits));
    }
}

/// <summary>Limits on the size of a document, checked as it is saved.</summary>
public sealed class DocumentLimits
{
    private readonly int? _maxPages;
    private readonly long? _maxBytes;

    /// <summary>The most pages a document may have, or <see langword="null"/> for no limit. Exceeding it throws <see cref="PdfLimitExceededException"/> before the extra page is started.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is less than 1.</exception>
    public int? MaxPages
    {
        get => _maxPages;
        init => _maxPages = value is < 1 ? throw new ArgumentOutOfRangeException(nameof(MaxPages), value, "A limit is 1 or more.") : value;
    }

    /// <summary>
    /// The most bytes the file may have, or <see langword="null"/> for no limit. The size is checked after every page, to stop early, and once more when the file is complete (the fonts are
    /// written last), so a file over the limit is never returned: <see cref="PdfLimitExceededException"/> is thrown and nothing at all has been written to the caller's stream.
    /// The file is built in memory before it is written to the stream.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is less than 1.</exception>
    public long? MaxBytes
    {
        get => _maxBytes;
        init => _maxBytes = value is < 1 ? throw new ArgumentOutOfRangeException(nameof(MaxBytes), value, "A limit is 1 or more.") : value;
    }
}

/// <summary>The limit of a <see cref="DocumentLimits"/> that a document hit.</summary>
public enum DocumentLimit
{
    /// <summary>The most pages (<see cref="DocumentLimits.MaxPages"/>).</summary>
    MaxPages,

    /// <summary>The most bytes (<see cref="DocumentLimits.MaxBytes"/>).</summary>
    MaxBytes,
}

/// <summary>
/// A document hit one of its <see cref="DocumentLimits"/> while it was being saved. Nothing has been written to the stream that was given to <see cref="PdfDocument.Save"/>
/// or <see cref="PdfDocument.SaveAsync"/>: the file is built in memory and written only when it is complete and within the limits.
/// </summary>
public sealed class PdfLimitExceededException : InvalidOperationException
{
    internal PdfLimitExceededException(DocumentLimit limit, long maximum)
        : base($"The document exceeded its limit {limit} of {maximum}.")
    {
        Limit = limit;
        Maximum = maximum;
    }

    /// <summary>The limit that was hit.</summary>
    public DocumentLimit Limit { get; }

    /// <summary>The value of the limit.</summary>
    public long Maximum { get; }
}

/// <summary>A character that no font of the collection has a glyph for. It is drawn as the <c>.notdef</c> glyph of the font chosen for it (usually an empty box).</summary>
public sealed record MissingGlyph
{
    internal MissingGlyph(int codePoint, string? language, int page)
    {
        CodePoint = codePoint;
        Language = language;
        Page = page;
    }

    /// <summary>The Unicode code point.</summary>
    public int CodePoint { get; }

    /// <summary>The language (BCP 47) of the text the character was in, or <see langword="null"/> when none was set.</summary>
    public string? Language { get; }

    /// <summary>The page it was drawn on, from 1.</summary>
    public int Page { get; }

    /// <summary>The character as text.</summary>
    public string Character => char.ConvertFromUtf32(CodePoint);

    /// <inheritdoc />
    public override string ToString() => $"U+{CodePoint:X4} (language {Language ?? "none"}, page {Page})";
}

/// <summary>What the last save of a document found: data to inspect, not exceptions. A report is made by a save; it is read, never made.</summary>
public sealed class DocumentDiagnostics
{
    internal DocumentDiagnostics(IReadOnlyList<MissingGlyph> missingGlyphs, IReadOnlyList<string> missingFamilies, int pageCount, long byteCount)
    {
        ArgumentNullException.ThrowIfNull(missingGlyphs);
        ArgumentNullException.ThrowIfNull(missingFamilies);
        MissingGlyphs = missingGlyphs;
        MissingFamilies = missingFamilies;
        PageCount = pageCount;
        ByteCount = byteCount;
    }

    /// <summary>An empty report: nothing has been saved.</summary>
    public static DocumentDiagnostics Empty { get; } = new([], [], 0, 0);

    /// <summary>
    /// The characters no font had a glyph for, each listed once for each language and page it occurred on (in order of first occurrence), at most 1,000 entries.
    /// </summary>
    public IReadOnlyList<MissingGlyph> MissingGlyphs { get; }

    /// <summary>
    /// The font families that a style asked for and the collection does not have, as they were written, each once in order of first use. Text in such a style is still drawn: it is set in
    /// the fonts of the collection in their order of preference (the first font when nothing else fits), which is rarely what was meant.
    /// </summary>
    public IReadOnlyList<string> MissingFamilies { get; }

    /// <summary>The number of pages of the document.</summary>
    public int PageCount { get; }

    /// <summary>The number of bytes of the file.</summary>
    public long ByteCount { get; }
}
