namespace Pdfwright;

/// <summary>The base direction a paragraph is laid out in.</summary>
public enum TextDirection
{
    /// <summary>Taken from the first strong character of the text (or, for a block, from the document's setting).</summary>
    Auto,

    /// <summary>Left to right.</summary>
    LeftToRight,

    /// <summary>Right to left.</summary>
    RightToLeft,
}
