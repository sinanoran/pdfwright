namespace Pdfwright;

/// <summary>
/// How text looks. Every property may be left unset (<see langword="null"/>), and an unset property is taken from the style around it: a run's
/// style from its paragraph's, a paragraph's from the document's <see cref="PdfDocumentOptions.DefaultStyle"/>, and that from the library's defaults
/// (the first font of the collection, 10 points, weight 400, upright, black).
/// </summary>
public sealed record TextStyle
{
    /// <summary>The font family, such as <c>Noto Sans</c>, compared without regard to case with the families of the fonts in the collection. Characters the family does not cover fall back to the rest of the collection.</summary>
    public string? Family { get; init; }

    /// <summary>The font size.</summary>
    public Length? Size { get; init; }

    /// <summary>The weight from 1 to 1000 (400 is regular, 700 bold); the font whose weight is nearest is used. There is no synthetic bold.</summary>
    public int? Weight { get; init; }

    /// <summary>Whether the text is italic; a family without an italic font is drawn upright. There is no synthetic italic.</summary>
    public bool? Italic { get; init; }

    /// <summary>The colour of the glyphs.</summary>
    public Color? Color { get; init; }

    /// <summary>The language as a BCP 47 tag (<c>tr</c>, <c>ar</c>, <c>hi</c>): it selects language-specific forms in shaping.</summary>
    public string? Language { get; init; }
}
