namespace Pdfwright;

/// <summary>What a <see cref="PdfDocument"/> is made with: the fonts, the page, the default text style and the document's metadata.</summary>
public sealed class PdfDocumentOptions
{
    /// <summary>The fonts the document may use. Required; the collection must hold at least one font when the document is created.</summary>
    public required FontCollection Fonts { get; init; }

    /// <summary>The style that applies to all text before a paragraph's or a run's own: family, size, weight, colour and language. Unset properties get the library's defaults (the first font of the collection, 10 points, regular, black).</summary>
    public TextStyle? DefaultStyle { get; init; }

    /// <summary>
    /// The style of headings level 1 to 6, as a list of exactly six styles (index 0 is level 1), or <see langword="null"/> for the default scale: bold at 2.0, 1.6, 1.3,
    /// 1.15, 1.0 and 1.0 times the default size. A style's unset properties are taken from the default style.
    /// </summary>
    public IReadOnlyList<TextStyle>? HeadingStyles { get; init; }

    /// <summary>The size of the pages. A4 by default.</summary>
    public PageSize PageSize { get; init; } = PageSize.A4;

    /// <summary>The margins. 20 mm at the top and bottom and 15 mm at the sides by default.</summary>
    public Margins Margins { get; init; } = Margins.Mm(top: 20, right: 15, bottom: 20, left: 15);

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
    public TextDirection Direction { get; init; } = TextDirection.Auto;

    /// <summary>
    /// Whether the output is a pure function of the document: no clock is read, so two saves of the same document are the same bytes. With it, the
    /// creation date is <see cref="Creation"/> or, when that is not set, absent from the file; without it the date is the time of the save.
    /// The file's <c>/ID</c> is a hash of its content either way. See <see cref="Compress"/> for byte-identical files across .NET versions.
    /// </summary>
    public bool Deterministic { get; init; }

    /// <summary>The creation and modification date written to the metadata; used only with <see cref="Deterministic"/> (without it, the time of the save is written).</summary>
    public DateTimeOffset? Creation { get; init; }

    /// <summary>
    /// Whether streams are compressed (Flate). On by default. The compressed bytes depend on the zlib of the .NET runtime, so a file saved with compression
    /// is the same bytes on the same runtime but may differ between runtimes; switch it off for golden files that must match everywhere.
    /// </summary>
    public bool Compress { get; init; } = true;

    /// <summary>Limits that stop a runaway document with a <see cref="PdfLimitExceededException"/>. None by default.</summary>
    public DocumentLimits Limits { get; init; } = new();
}

/// <summary>Limits on the size of a document, checked as it is saved.</summary>
public sealed class DocumentLimits
{
    /// <summary>The most pages a document may have, or <see langword="null"/> for no limit. Exceeding it throws <see cref="PdfLimitExceededException"/> before the extra page is started.</summary>
    public int? MaxPages { get; init; }

    /// <summary>The most bytes the file may have, or <see langword="null"/> for no limit. It is checked after every page, so the file may exceed it by one page when the exception is thrown.</summary>
    public long? MaxBytes { get; init; }
}

/// <summary>A document hit one of its <see cref="DocumentLimits"/> while it was being saved. What had been written to the stream is an incomplete file.</summary>
public sealed class PdfLimitExceededException : InvalidOperationException
{
    /// <summary>Makes the exception for a limit.</summary>
    /// <param name="limit">The name of the limit: <c>MaxPages</c> or <c>MaxBytes</c>.</param>
    /// <param name="maximum">The limit's value.</param>
    public PdfLimitExceededException(string limit, long maximum)
        : base($"The document exceeded its limit {limit} of {maximum}.")
    {
        Limit = limit;
        Maximum = maximum;
    }

    /// <summary>The name of the limit that was hit: <c>MaxPages</c> or <c>MaxBytes</c>.</summary>
    public string Limit { get; }

    /// <summary>The value of the limit.</summary>
    public long Maximum { get; }
}

/// <summary>A character that no font of the collection has a glyph for. It is drawn as the <c>.notdef</c> glyph of the font chosen for it (usually an empty box).</summary>
public sealed record MissingGlyph
{
    /// <summary>Makes the report of a missing glyph.</summary>
    /// <param name="codePoint">The Unicode code point.</param>
    /// <param name="language">The language of the text it was in, or <see langword="null"/>.</param>
    /// <param name="page">The page it was drawn on, from 1.</param>
    public MissingGlyph(int codePoint, string? language, int page)
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

/// <summary>What the last save of a document found: data to inspect, not exceptions.</summary>
public sealed class DocumentDiagnostics
{
    /// <summary>Makes a report.</summary>
    /// <param name="missingGlyphs">The missing glyphs.</param>
    /// <param name="pageCount">The number of pages.</param>
    /// <param name="byteCount">The number of bytes written.</param>
    public DocumentDiagnostics(IReadOnlyList<MissingGlyph> missingGlyphs, int pageCount, long byteCount)
    {
        ArgumentNullException.ThrowIfNull(missingGlyphs);
        MissingGlyphs = missingGlyphs;
        PageCount = pageCount;
        ByteCount = byteCount;
    }

    /// <summary>An empty report: nothing has been saved.</summary>
    public static DocumentDiagnostics Empty { get; } = new([], 0, 0);

    /// <summary>
    /// The characters no font had a glyph for, each listed once for each language and page it occurred on (in order of first occurrence), at most 1,000 entries.
    /// </summary>
    public IReadOnlyList<MissingGlyph> MissingGlyphs { get; }

    /// <summary>The number of pages of the document.</summary>
    public int PageCount { get; }

    /// <summary>The number of bytes of the file.</summary>
    public long ByteCount { get; }
}
