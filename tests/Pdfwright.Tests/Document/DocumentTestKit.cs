using Pdfwright.Tests.Pdf;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace Pdfwright.Tests.Document;

/// <summary>Fonts, documents and readers for the tests of the public document layer.</summary>
internal static class DocumentTestKit
{
    internal static readonly string[] AllFonts =
    [
        "NotoSans-Regular.ttf", "NotoSans-Bold.ttf", "NotoSans-Italic.ttf", "NotoSansArabic-Regular.ttf", "NotoSansHebrew-Regular.ttf",
        "NotoSansDevanagari-Regular.ttf", "NotoSansBengali-Regular.ttf", "NotoSansTamil-Regular.ttf", "NotoSansThai-Regular.ttf",
    ];

    private static readonly Lazy<FontCollection> Shared = new(() => Collection(AllFonts));

    /// <summary>One collection of every test font, shared by the tests (the point of a collection is to be shared).</summary>
    internal static FontCollection Fonts => Shared.Value;

    internal static FontCollection Collection(params string[] files)
    {
        FontCollection fonts = new();
        foreach (string file in files)
        {
            fonts.Add(File.ReadAllBytes(TestPaths.Font(file)));
        }

        return fonts;
    }

    /// <summary>Options with every font, a deterministic file and no compression, so that a test can read the content.</summary>
    internal static PdfDocumentOptions Options(Action<OptionsBuilder>? change = null)
    {
        OptionsBuilder b = new();
        change?.Invoke(b);
        return new PdfDocumentOptions
        {
            Fonts = b.Fonts ?? Fonts,
            DefaultStyle = b.DefaultStyle ?? new TextStyle { Family = "Noto Sans", Size = Length.Pt(10) },
            PageSize = b.PageSize ?? PageSize.A4,
            Margins = b.Margins ?? Margins.Mm(top: 20, right: 15, bottom: 20, left: 15),
            Title = b.Title,
            Subject = b.Subject,
            Author = b.Author,
            Creator = b.Creator,
            Language = b.Language,
            Direction = b.Direction,
            Deterministic = b.Deterministic,
            Creation = b.Creation,
            Compress = b.Compress,
            Limits = b.Limits ?? new DocumentLimits(),
            HeadingStyles = b.HeadingStyles,
            Header = b.Header,
            Footer = b.Footer,
            HeaderOffset = b.HeaderOffset,
            FooterOffset = b.FooterOffset,
            Tagged = b.Tagged,
        };
    }

    /// <summary>The mutable side of <see cref="Options"/>: the real options are init-only.</summary>
    internal sealed class OptionsBuilder
    {
        internal FontCollection? Fonts { get; set; }

        internal TextStyle? DefaultStyle { get; set; }

        internal PageSize? PageSize { get; set; }

        internal Margins? Margins { get; set; }

        internal string? Title { get; set; }

        internal string? Subject { get; set; }

        internal string? Author { get; set; }

        internal string? Creator { get; set; }

        internal string? Language { get; set; }

        internal TextDirection Direction { get; set; }

        internal bool Deterministic { get; set; } = true;

        internal DateTimeOffset? Creation { get; set; }

        internal bool Compress { get; set; }

        internal DocumentLimits? Limits { get; set; }

        internal IReadOnlyList<TextStyle>? HeadingStyles { get; set; }

        internal Func<PageContext, Block?>? Header { get; set; }

        internal Func<PageContext, Block?>? Footer { get; set; }

        internal Length? HeaderOffset { get; set; }

        internal Length? FooterOffset { get; set; }

        /// <summary><see cref="PdfDocumentOptions.Tagged"/>, on by default as in the library: a test of untagged output sets it to <see langword="false"/>.</summary>
        internal bool Tagged { get; set; } = true;
    }

    internal static byte[] Save(PdfDocument document)
    {
        using MemoryStream stream = new();
        document.Save(stream);
        return stream.ToArray();
    }

    /// <summary>The text of each page as PdfPig reads it.</summary>
    internal static string[] PageTexts(byte[] pdf)
    {
        using UglyToad.PdfPig.PdfDocument read = UglyToad.PdfPig.PdfDocument.Open(pdf);
        return [.. read.GetPages().Select(p => p.Text)];
    }

    internal static Page[] Pages(UglyToad.PdfPig.PdfDocument read) => [.. read.GetPages()];

    /// <summary>The text with white space and the combining grapheme joiner left out, as the extraction tests of the text engine compare it.</summary>
    internal static string Strip(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c) && c != (char)0x034F));

    /// <summary>A paragraph of <paramref name="lines"/> lines or more in a column of A4 at 10 points: sentences that wrap.</summary>
    internal static string LongText(int words, string prefix = "word") =>
        string.Join(' ', Enumerable.Range(0, words).Select(i => $"{prefix}{i}"));

    internal static void AssertValid(byte[] pdf, string name) => PdfTestKit.AssertQpdfAccepts(pdf, name);
}
