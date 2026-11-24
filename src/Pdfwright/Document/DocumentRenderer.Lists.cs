using System.Text;
using Pdfwright.Pdf;
using Pdfwright.Text;

namespace Pdfwright.Document;

// Lists. A list is laid out as a run of paragraphs (text items) one under the other, each inset from the start edge by the room of its marker; the first paragraph of an item has its marker, which is drawn with
// its first line (see Draw). So a list flows, splits between pages and sits in a callout or a column cell exactly as paragraphs do, and a marker is never apart from the first line of its item.
internal sealed partial class DocumentRenderer
{
    private const char BulletCharacter = (char)0x2022;

    /// <summary>The digit zero of the Arabic-Indic digits, U+0660.</summary>
    private const int ArabicIndicZero = 0x0660;

    /// <summary>The text of the marker of the item with the number <paramref name="number"/> (from 1).</summary>
    internal static string MarkerText(ListMarker marker, int number, char bullet = BulletCharacter)
    {
        switch (marker)
        {
            case ListMarker.Bullet:
                return bullet.ToString();
            case ListMarker.Decimal:
                return number.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".";
            case ListMarker.ArabicIndicDecimal:
                return string.Concat(number.ToString(System.Globalization.CultureInfo.InvariantCulture).Select(d => (char)(ArabicIndicZero + (d - '0')))) + ".";
            default:
            {
                char first = marker == ListMarker.LowerAlpha ? 'a' : 'A';
                StringBuilder letters = new();
                for (int n = number; n > 0; n = (n - 1) / 26)
                {
                    _ = letters.Insert(0, (char)(first + ((n - 1) % 26)));
                }

                return letters + ".";
            }
        }
    }

    /// <summary>The characters a bullet falls back to when no font covers U+2022, in order: the black circle, the bullet operator, the middle dot and the hyphen-minus.</summary>
    private static readonly char[] BulletFallbacks = ['●', '∙', '·', '-'];

    /// <summary>
    /// The bullet of a list set in <paramref name="style"/>: U+2022, or, when no font of the collection has it (the Arabic and Hebrew fonts do not), the first of <see cref="BulletFallbacks"/> that a font has.
    /// The fonts are looked at in the order the style chooses them, the style's family first.
    /// </summary>
    private char BulletFor(ResolvedStyle style)
    {
        int[] order = _fonts.Order(style.Family, style.Weight, style.Italic);
        bool Covered(char c) => order.Any(i => _fonts.Entries[i].Font.GlyphForCodePoint(c) != 0);
        if (Covered(BulletCharacter))
        {
            return BulletCharacter;
        }

        foreach (char candidate in BulletFallbacks)
        {
            if (Covered(candidate))
            {
                return candidate;
            }
        }

        return BulletCharacter;
    }

    /// <summary>
    /// Lays a list out at <paramref name="width"/> as paragraphs one under the other. <paramref name="parentInset"/> is how far the text of the item that holds the list is from the start edge (0 for a list that is not nested), and
    /// <paramref name="defaultLastAfter"/> the space after the last paragraph when the list sets none.
    /// </summary>
    private List<TextItem> BuildList(ItemList list, double width, TextDirection direction, double parentInset, double defaultLastAfter)
    {
        TextDirection own = list.Direction ?? direction;
        ResolvedStyle listStyle = _base.Apply(list.Style);
        double itemGap = Space(list.ItemSpacing, listStyle.Size * 0.3);
        double lastAfter = Space(list.SpaceAfter, defaultLastAfter);
        double markerInset = parentInset + list.Indent.Points;

        // The room of the markers: as wide as the widest, which is set in the direction the list has (the width of a number does not depend on it, and each marker is set again in its own item's direction).
        TextDirection measuring = own == TextDirection.RightToLeft ? TextDirection.RightToLeft : TextDirection.LeftToRight;
        double column = 0;
        for (int i = 0; i < list.Items.Count; i++)
        {
            ResolvedStyle style = MarkerStyle(list, listStyle, list.Items[i]);
            StyledLayout laid = BlockText.Layout([new TextRun(MarkerText(list.Marker, list.Start + i, BulletFor(style)))], style, _fonts, null, TextAlignment.Start, measuring, 1.0);
            column = Math.Max(column, laid.Layout.Width);
        }

        double inset = markerInset + column + list.MarkerGap.Points;
        double textWidth = width - inset;
        if (textWidth < MinimumWidth)
        {
            throw new InvalidOperationException($"The list's indent and markers take {inset:0.##} pt of the {width:0.##} pt available and leave {Math.Max(0, textWidth):0.##} pt for its text: it is at least {MinimumWidth:0.##} pt wide (make the indent or the marker gap smaller).");
        }

        // Tagged, the list is an L with an LI for each item, which holds the marker's Lbl and an LBody: a P for each paragraph of the item and the L of a
        // nested list, in the item's order. The paragraphs and the nested lists are built with the LBody as their parent; untagged, there is none.
        PdfStructureElement? outside = _tagParent;
        PdfStructureElement? listTag = Tag("L");
        if (listTag is not null)
        {
            listTag.Attributes = new PdfDictionary().Set("O", "List").Set("ListNumbering", ListNumbering(list.Marker));
        }

        List<TextItem> result = [];
        try
        {
            BuildListItems(list, width, textWidth, own, listStyle, itemGap, lastAfter, inset, markerInset, listTag, result);
        }
        finally
        {
            _tagParent = outside;
        }

        return result;
    }

    /// <summary>The paragraphs of the items of a list, for <see cref="BuildList"/>; <paramref name="listTag"/> is the list's <c>L</c>, or <see langword="null"/> untagged.</summary>
    private void BuildListItems(ItemList list, double width, double textWidth, TextDirection own, ResolvedStyle listStyle, double itemGap, double lastAfter, double inset, double markerInset, PdfStructureElement? listTag, List<TextItem> result)
    {
        for (int i = 0; i < list.Items.Count; i++)
        {
            ListItem item = list.Items[i];
            bool lastItem = i == list.Items.Count - 1;
            PdfStructureElement? entry = null;
            if (listTag is not null)
            {
                entry = _writer.Structure!.Create(listTag, "LI");
                _tagParent = _writer.Structure.Create(entry, "LBody");
            }

            int firstOfItem = result.Count;
            for (int j = 0; j < item.Content.Count; j++)
            {
                bool last = lastItem && j == item.Content.Count - 1;
                if (item.Content[j] is ItemList nested)
                {
                    List<TextItem> inner = BuildList(nested, width, own, inset, last ? lastAfter : itemGap);
                    result.AddRange(inner);
                    continue;
                }

                Paragraph paragraph = (Paragraph)item.Content[j];
                ResolvedStyle style = listStyle.Apply(paragraph.Style);
                StyledLayout text = BlockText.Layout(paragraph.Runs, style, _fonts, textWidth, paragraph.Alignment, paragraph.Direction ?? own, paragraph.LineSpacing);
                bool rtl = text.Layout.IsRightToLeft;
                StyledLayout? marker = null;
                if (j == 0)
                {
                    ResolvedStyle markerStyle = MarkerStyle(list, listStyle, item);
                    marker = BlockText.Layout(
                        [new TextRun(MarkerText(list.Marker, list.Start + i, BulletFor(markerStyle)))],
                        markerStyle,
                        _fonts,
                        null,
                        TextAlignment.Start,
                        rtl ? TextDirection.RightToLeft : TextDirection.LeftToRight,
                        1.0);
                    NoteFamilies(marker);

                    // The marker is the item's Lbl, drawn before the paragraph's own marked content and outside it (see Draw).
                    marker.Tag = TextTag(entry, "Lbl", marker, markerStyle);
                }

                // A marker larger than the text: the first line is made as tall as the marker needs, so that the marker does not reach into the paragraph above (the baseline moves down by the excess ascent).
                ParagraphLayout laidOut = text.Layout;
                if (marker is not null)
                {
                    LaidOutLine textLine = laidOut.Lines[0];
                    LaidOutLine markerLine = marker.Layout.Lines[0];
                    laidOut = laidOut.WithFirstLineGrown(markerLine.Ascent - textLine.Ascent, markerLine.Descent - textLine.Descent);
                }

                NoteFamilies(text);
                StyledLayout placed = new()
                {
                    Layout = laidOut,
                    Styles = text.Styles,
                    Spans = text.Spans,
                    List = new ListPlacement { ContainerWidth = width, Inset = inset, MarkerInset = markerInset, RightToLeft = rtl, Marker = marker },
                    Tag = TextTag("P", text, style),
                };
                result.Add(new TextItem
                {
                    Styled = placed,
                    // The space before the first paragraph and after the last is the larger of the list's and the paragraph's own.
                    Before = result.Count == 0 ? Math.Max(Space(list.SpaceBefore, 0), Space(paragraph.SpaceBefore, 0)) : Space(paragraph.SpaceBefore, 0),
                    After = last ? Math.Max(lastAfter, Space(paragraph.SpaceAfter, 0)) : Space(paragraph.SpaceAfter, itemGap),
                    KeepTogether = paragraph.KeepTogether,
                });
            }

            if (entry is not null && !HasTaggedText(result, firstOfItem))
            {
                // An item with nothing to read (its paragraphs blank, its nested list too): a label alone would be a bare bullet read aloud, so the
                // marker is an artifact and the item has no LI. A list whose items are all so has no L either: its elements are never drawn.
                result[firstOfItem].Styled.List!.Marker!.Tag = null;
            }
        }
    }

    /// <summary>Whether a paragraph from <paramref name="from"/> on has an element (a nested list's paragraphs included).</summary>
    private static bool HasTaggedText(List<TextItem> items, int from)
    {
        for (int k = from; k < items.Count; k++)
        {
            if (items[k].Styled.Tag is not null)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The <c>/ListNumbering</c> of a list's <c>L</c> (ISO 32000-1, table 347): a disc for a bullet, the kind of number or letter otherwise. The
    /// Arabic-Indic numbers are decimal numbers in other digits, and the attribute has no value of its own for them.
    /// </summary>
    private static string ListNumbering(ListMarker marker) => marker switch
    {
        ListMarker.Bullet => "Disc",
        ListMarker.LowerAlpha => "LowerAlpha",
        ListMarker.UpperAlpha => "UpperAlpha",
        _ => "Decimal",
    };

    /// <summary>The style of the marker of an item: the list's, the item's first paragraph's, then the list's marker style over them.</summary>
    private static ResolvedStyle MarkerStyle(ItemList list, ResolvedStyle listStyle, ListItem item) =>
        listStyle.Apply(((Paragraph)item.Content[0]).Style).Apply(list.MarkerStyle);

    /// <summary>Places the paragraphs of a list one after the other; a paragraph is split between pages by its lines, and its marker is drawn with its first line.</summary>
    private void PlaceList(Prepared list, Prepared? next)
    {
        List<TextItem> items = list.ListItems!;
        Prepared[] parts = [.. items.Select(PreparedOf)];
        for (int i = 0; i < parts.Length; i++)
        {
            PlaceText(parts[i], i + 1 < parts.Length ? parts[i + 1] : next);
        }
    }
}
