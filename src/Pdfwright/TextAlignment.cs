namespace Pdfwright;

/// <summary>
/// Where the lines of a paragraph sit in the width they are given. The names are logical, never left and right, so that the same setting
/// works for a right-to-left paragraph: <see cref="Start"/> is the left edge in a left-to-right paragraph and the right edge in a right-to-left one.
/// There is no justification and no hyphenation in 0.1.
/// </summary>
public enum TextAlignment
{
    /// <summary>The edge where the paragraph's text begins.</summary>
    Start,

    /// <summary>In the middle of the width.</summary>
    Center,

    /// <summary>The edge where the paragraph's text ends.</summary>
    End,
}
