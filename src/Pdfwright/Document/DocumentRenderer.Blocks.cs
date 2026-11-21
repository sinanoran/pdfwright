using Pdfwright.Graphics;
using Pdfwright.Pdf;
using Pdfwright.Text;

namespace Pdfwright.Document;

// The blocks that are not text and not a table: columns, callouts, drawings and images, and the header and footer drawn on each page. They are built into "items": a piece of the flow with a
// height (known when it is built, because everything is laid out at a width that is known) and a way to draw itself at a place. A paragraph is an item that can be split between its lines;
// every other item is whole.
internal sealed partial class DocumentRenderer
{
    private const double Epsilon = 1e-9;

    /// <summary>The least width, in points, that a column cell or the inside of a callout may have; less fails the save.</summary>
    private const double MinimumWidth = 4;

    private readonly List<PdfPageWriter> _pending = [];

    private abstract class Item
    {
        internal double Before { get; init; }

        internal double After { get; init; }
    }

    private sealed class TextItem : Item
    {
        internal required StyledLayout Styled { get; init; }

        internal bool KeepWithNext { get; init; }

        internal bool KeepTogether { get; init; }

        internal double Height => Styled.Layout.Lines.Sum(l => l.Pitch);
    }

    private sealed class FixedItem : Item
    {
        internal required string Kind { get; init; }

        internal required double Height { get; init; }

        /// <summary>Draws the item with its top left corner at (x from the left edge of the page, y down from the top of the content area).</summary>
        internal required Action<double, double> Draw { get; init; }
    }

    /// <summary>Items one under the other: between two of them the space after the first and the space before the second; the space before the first and the space after the last are not part of it.</summary>
    private sealed class Stack(List<Item> items)
    {
        internal List<Item> Items { get; } = items;

        internal double Height
        {
            get
            {
                double height = 0;
                for (int i = 0; i < Items.Count; i++)
                {
                    if (i > 0)
                    {
                        height += Items[i - 1].After + Items[i].Before;
                    }

                    height += HeightOf(Items[i]);
                }

                return height;
            }
        }
    }

    /// <summary>A callout laid out at a width: its content, and the room its border and padding take.</summary>
    private sealed class CalloutPlan
    {
        internal required Callout Block { get; init; }

        internal required Stack Stack { get; init; }

        internal required double Width { get; init; }

        internal required double Border { get; init; }

        internal required double InsetTop { get; init; }

        internal required double InsetBottom { get; init; }

        internal required double InsetLeft { get; init; }

        internal double Before { get; init; }

        internal double After { get; init; }

        internal double Height => InsetTop + Stack.Height + InsetBottom;
    }

    private static double HeightOf(Item item) => item switch
    {
        TextItem text => text.Height,
        FixedItem fixedItem => fixedItem.Height,
        _ => throw new InvalidOperationException("Unknown item."),
    };

    // ---- building

    private TextItem BuildTextItem(Block block, double width, TextDirection direction)
    {
        switch (block)
        {
            case Paragraph paragraph:
            {
                ResolvedStyle style = _base.Apply(paragraph.Style);
                StyledLayout layout = BlockText.Layout(paragraph.Runs, style, _fonts, width, paragraph.Alignment, paragraph.Direction ?? direction, paragraph.LineSpacing);
                NoteFamilies(layout);
                return new TextItem
                {
                    Styled = layout,
                    Before = Space(paragraph.SpaceBefore, 0),
                    After = Space(paragraph.SpaceAfter, style.Size * 0.6),
                    KeepTogether = paragraph.KeepTogether,
                };
            }

            case Heading heading:
            {
                ResolvedStyle style = _headings[heading.Level - 1];
                StyledLayout layout = BlockText.Layout(heading.Runs, style, _fonts, width, heading.Alignment, heading.Direction ?? direction, 1.0);
                NoteFamilies(layout);
                return new TextItem
                {
                    Styled = layout,
                    Before = Space(heading.SpaceBefore, style.Size),
                    After = Space(heading.SpaceAfter, style.Size * 0.4),
                    KeepWithNext = true,
                };
            }

            default:
                throw new InvalidOperationException("Not a text block.");
        }
    }

    private Stack BuildStack(IReadOnlyList<Block> blocks, double width, double maxHeight, TextDirection direction) =>
        new([.. blocks.Select(block => BuildItem(block, width, maxHeight, direction))]);

    /// <summary>Lays a block out at a width: <paramref name="maxHeight"/> is the most height a picture may take, what a page can hold at the place the block is.</summary>
    private Item BuildItem(Block block, double width, double maxHeight, TextDirection direction)
    {
        switch (block)
        {
            case Paragraph or Heading:
                return BuildTextItem(block, width, direction);

            case Spacer spacer:
                return new FixedItem { Kind = "spacer", Height = spacer.Height.Points, Draw = (_, _) => { } };

            case Drawing drawing:
                return BuildDrawing(drawing, width, maxHeight, direction);

            case Image image:
                return BuildImage(image, width, maxHeight, direction);

            case Columns columns:
                return BuildColumns(columns, width, maxHeight, direction);

            case Callout callout:
            {
                CalloutPlan plan = PlanCallout(callout, width, maxHeight, direction);
                return new FixedItem
                {
                    Kind = "callout",
                    Height = plan.Height,
                    Before = plan.Before,
                    After = plan.After,
                    Draw = (x, y) => DrawWholeCallout(plan, x, y),
                };
            }

            default:
                throw new InvalidOperationException($"A {block.GetType().Name} cannot be placed here (a header or a footer holds a paragraph, heading, columns, callout, drawing, image or spacer).");
        }
    }

    private double AfterDefault(Length? after) => Space(after, _base.Size * 0.6);

    private static bool IsRightToLeft(TextDirection direction) => direction == TextDirection.RightToLeft;

    /// <summary>Where a box of <paramref name="boxWidth"/> sits in a space of <paramref name="width"/>, from the space's left edge, for a logical alignment.</summary>
    private static double Offset(TextAlignment alignment, double width, double boxWidth, TextDirection direction)
    {
        double slack = Math.Max(0, width - boxWidth);
        bool rtl = IsRightToLeft(direction);
        return alignment switch
        {
            TextAlignment.Center => slack / 2,
            TextAlignment.End => rtl ? 0 : slack,
            _ => rtl ? slack : 0,
        };
    }

    private FixedItem BuildDrawing(Drawing drawing, double width, double maxHeight, TextDirection direction)
    {
        double w = drawing.Width.Points;
        double h = drawing.Height.Points;
        if (w > width + 1e-6)
        {
            throw new InvalidOperationException($"A drawing {w:0.##} pt wide does not fit in the {width:0.##} pt available (a drawing is never scaled).");
        }

        if (h > maxHeight + 1e-6)
        {
            throw new InvalidOperationException($"A drawing {h:0.##} pt high is taller than the {maxHeight:0.##} pt a page can hold here (a drawing is never split).");
        }

        double offset = Offset(TextAlignment.Start, width, w, direction);
        return new FixedItem
        {
            Kind = "drawing",
            Height = h,
            Before = Space(drawing.SpaceBefore, 0),
            After = AfterDefault(drawing.SpaceAfter),
            Draw = (x, y) => DrawDrawing(drawing, x + offset, y, w, h, direction),
        };
    }

    private FixedItem BuildImage(Image image, double width, double maxHeight, TextDirection direction)
    {
        double pixelsWide = image.Source.PixelWidth;
        double pixelsHigh = image.Source.PixelHeight;
        double w;
        double h;
        if (image.Width is { } givenWidth && image.Height is { } givenHeight)
        {
            w = givenWidth.Points;
            h = givenHeight.Points;
        }
        else if (image.Width is { } onlyWidth)
        {
            w = onlyWidth.Points;
            h = w * pixelsHigh / pixelsWide;
        }
        else if (image.Height is { } onlyHeight)
        {
            h = onlyHeight.Points;
            w = h * pixelsWide / pixelsHigh;
        }
        else
        {
            w = pixelsWide * 0.75;
            h = pixelsHigh * 0.75;
        }

        double scale = Math.Min(1, Math.Min(width / w, maxHeight / h));
        w *= scale;
        h *= scale;
        double offset = Offset(image.Alignment, width, w, direction);
        return new FixedItem
        {
            Kind = "image",
            Height = h,
            Before = Space(image.SpaceBefore, 0),
            After = AfterDefault(image.SpaceAfter),
            Draw = (x, y) => DrawPicture(image.Source, x + offset, y, w, h),
        };
    }

    private FixedItem BuildColumns(Columns columns, double width, double maxHeight, TextDirection direction)
    {
        TextDirection own = columns.Direction ?? direction;
        int count = columns.Cells.Count;
        double gap = columns.Gap.Points;
        double cell = (width - (gap * (count - 1))) / count;
        if (cell < MinimumWidth)
        {
            throw new InvalidOperationException($"The columns' gap of {gap:0.##} pt leaves {Math.Max(0, cell):0.##} pt for each of {count} cells in the {width:0.##} pt available: a cell is at least {MinimumWidth:0.##} pt wide (make the gap smaller).");
        }

        Stack[] stacks = [.. columns.Cells.Select(blocks => BuildStack(blocks, cell, maxHeight, own))];
        double height = stacks.Max(s => s.Height);
        bool rtl = IsRightToLeft(own);
        return new FixedItem
        {
            Kind = "columns",
            Height = height,
            Before = Space(columns.SpaceBefore, 0),
            After = AfterDefault(columns.SpaceAfter),
            Draw = (x, y) =>
            {
                for (int i = 0; i < count; i++)
                {
                    int slot = rtl ? count - 1 - i : i;
                    DrawStack(stacks[i], x + (slot * (cell + gap)), y);
                }
            },
        };
    }

    private CalloutPlan PlanCallout(Callout callout, double width, double maxHeight, TextDirection direction)
    {
        double border = callout.Border?.Width.Points ?? 0;
        Margins pad = callout.Padding;
        double top = border + pad.Top.Points;
        double bottom = border + pad.Bottom.Points;
        double left = border + pad.Left.Points;
        double right = border + pad.Right.Points;
        double inner = width - left - right;
        if (inner < MinimumWidth)
        {
            throw new InvalidOperationException($"The callout's border and padding take {left + right:0.##} pt of the {width:0.##} pt available and leave {Math.Max(0, inner):0.##} pt for its content: it is at least {MinimumWidth:0.##} pt wide (make the padding smaller).");
        }

        double innerMax = maxHeight - top - bottom;
        if (innerMax <= 0)
        {
            throw new InvalidOperationException($"The callout's border and padding take {top + bottom:0.##} pt of the {maxHeight:0.##} pt a page can hold here and leave nothing for its content (make the padding smaller).");
        }

        Stack stack = BuildStack(callout.Content, inner, innerMax, callout.Direction ?? direction);
        if (stack.Items.Count > 0)
        {
            Item head = stack.Items[0];
            double need = FirstUnit(head);
            if (head is TextItem && need > innerMax)
            {
                need = 0; // a line taller than a page is drawn anyway
            }

            if (top + bottom + need > maxHeight + 1e-6)
            {
                throw new InvalidOperationException($"The callout's border and padding take {top + bottom:0.##} pt and its first block {need:0.##} pt, more than the {maxHeight:0.##} pt a page can hold here (make the padding smaller).");
            }
        }

        return new CalloutPlan
        {
            Block = callout,
            Stack = stack,
            Width = width,
            Border = border,
            InsetTop = top,
            InsetBottom = bottom,
            InsetLeft = left,
            Before = Space(callout.SpaceBefore, 0),
            After = AfterDefault(callout.SpaceAfter),
        };
    }

    // ---- drawing

    private void DrawItem(Item item, double x, double y)
    {
        switch (item)
        {
            case TextItem text:
                Draw(text.Styled, 0, text.Styled.Layout.Lines.Count, x, y);
                break;
            case FixedItem fixedItem:
                fixedItem.Draw(x, y);
                break;
        }
    }

    private void DrawStack(Stack stack, double x, double y)
    {
        double at = y;
        for (int i = 0; i < stack.Items.Count; i++)
        {
            if (i > 0)
            {
                at += stack.Items[i - 1].After + stack.Items[i].Before;
            }

            DrawItem(stack.Items[i], x, at);
            at += HeightOf(stack.Items[i]);
        }
    }

    private void DrawWholeCallout(CalloutPlan plan, double x, double y)
    {
        DrawCalloutBox(plan, x, y, plan.Height);
        DrawStack(plan.Stack, x + plan.InsetLeft, y + plan.InsetTop);
    }

    private void DrawCalloutBox(CalloutPlan plan, double x, double y, double height)
    {
        Callout callout = plan.Block;
        if (callout.Fill is null && callout.Border is null)
        {
            return;
        }

        // A radius is cut to half the shorter side of what it is drawn on, so that the corners stay circular (a box is a pill at the most) and the same for the fill and the border.
        double radius = Math.Min(callout.CornerRadius.Points, Math.Min(plan.Width, height) / 2);
        using Canvas canvas = new(_page!, x, _pageHeight - _top - y, plan.Width, height);
        canvas.ResetFillColor();
        if (callout.Fill is { } fill)
        {
            canvas.Fill(new PathBuilder().RoundedRectangle(0, 0, plan.Width, height, radius, radius).Build(), new Brush(fill));
        }

        if (callout.Border is { } line)
        {
            double w = line.Width.Points;
            double inset = w / 2;
            double boxWidth = Math.Max(0, plan.Width - w);
            double boxHeight = Math.Max(0, height - w);
            double r = Math.Min(Math.Max(0, radius - inset), Math.Min(boxWidth, boxHeight) / 2);
            canvas.Stroke(new PathBuilder().RoundedRectangle(inset, inset, boxWidth, boxHeight, r, r).Build(), new Pen(line.Color, w));
        }
    }

    private DrawingServices Services(int pageNumber, TextDirection direction) => new(
        _fonts,
        _base,
        direction,
        (codePoint, language) => AddMissing(codePoint, language, pageNumber),
        NoteFamily);

    private void DrawDrawing(Drawing drawing, double x, double y, double width, double height, TextDirection direction)
    {
        PdfPageWriter page = _page!;
        DrawingServices services = Services(page.Index + 1, direction);
        using Canvas inner = new(page, x, _pageHeight - _top - y, width, height) { OnMissingGlyph = services.MissingGlyph };
        inner.ResetFillColor();
        inner.Clip(new PathBuilder().Rectangle(0, 0, width, height).Build());
        DrawingCanvas canvas = new(inner, width, height, services);
        try
        {
            drawing.Draw(canvas);
        }
        finally
        {
            canvas.Finish();
        }
    }

    private void DrawPicture(PdfImage picture, double x, double y, double width, double height)
    {
        using Canvas canvas = new(_page!, x, _pageHeight - _top - y, width, height);
        canvas.DrawImage(picture.Source, 0, 0, width, height);
    }

    // ---- placing

    private Prepared PrepareFlowBlock(Block block)
    {
        switch (block)
        {
            case Callout callout:
            {
                CalloutPlan plan = PlanCallout(callout, _contentWidth, _contentHeight, _options.Direction);
                double first = plan.Stack.Items.Count == 0 ? 0 : FirstUnit(plan.Stack.Items[0]);
                return new Prepared { Callout = plan, Before = plan.Before, After = plan.After, FirstUnit = plan.InsetTop + first, WholeHeight = callout.KeepTogether && plan.Height <= _contentHeight ? plan.Height : null };
            }

            case Drawing or Image or Columns:
            {
                FixedItem item = (FixedItem)BuildItem(block, _contentWidth, _contentHeight, _options.Direction);
                return new Prepared { Fixed = item, Before = item.Before, After = item.After, FirstUnit = item.Height };
            }

            default:
                return new Prepared();
        }
    }

    private static double FirstUnit(Item item) => item is TextItem { Styled.Layout.Lines.Count: > 0 } text ? text.Styled.Layout.Lines[0].Pitch : item is FixedItem fixedItem ? fixedItem.Height : 0;

    /// <summary>Places a block that is never split: on the page when it fits, else on the next; one taller than a page cannot be laid out.</summary>
    private void PlaceFixed(Prepared block)
    {
        FixedItem item = block.Fixed!;
        if (item.Height > _contentHeight + 1e-6)
        {
            throw new InvalidOperationException($"A {item.Kind} {item.Height:0.##} pt high is taller than the {_contentHeight:0.##} pt a page's content area can hold, and it is never split.");
        }

        if (_page is null)
        {
            OpenPage();
        }

        double before = _hasContent ? block.Before : 0;
        if (_hasContent && _y + before + item.Height > _contentHeight + Epsilon)
        {
            ClosePage();
            OpenPage();
            before = 0;
        }

        _y += before;
        item.Draw(_left, _y);
        _y += item.Height;
        _hasContent = true;
        _y = Math.Min(_y + block.After, _contentHeight);
    }

    private readonly record struct Slice(int Index, int From, int Count, double Bottom);

    /// <summary>
    /// Places a callout, split between pages when it does not fit: each page gets a piece with its own box. A piece takes lines of paragraphs and whole blocks, with the border and padding at its top and bottom, and
    /// the callout goes on at the top of the next page. A heading is not left at the end of a piece. A callout that asks for it, and fits on a page, moves whole to the next page instead of being split.
    /// </summary>
    private void PlaceCallout(Prepared prepared)
    {
        CalloutPlan plan = prepared.Callout!;
        List<Item> items = plan.Stack.Items;
        double wholeHeight = plan.Height;
        if (plan.Block.KeepTogether && _page is not null && _hasContent && wholeHeight <= _contentHeight && _y + plan.Before + wholeHeight > _contentHeight + Epsilon)
        {
            ClosePage();
        }

        double pageRoom = _contentHeight - plan.InsetTop - plan.InsetBottom;
        int item = 0;
        int line = 0;
        bool first = true;
        while (item < items.Count)
        {
            _cancellation.ThrowIfCancellationRequested();
            if (_page is null)
            {
                OpenPage();
            }

            double before = first && _hasContent ? plan.Before : 0;
            double room = _contentHeight - _y - before - plan.InsetTop - plan.InsetBottom;
            List<Slice> piece = [];
            int i = item;
            int l = line;
            double used = 0;
            bool stopped = false;
            while (i < items.Count && !stopped)
            {
                Item it = items[i];
                double gap = piece.Count > 0 ? items[i - 1].After + it.Before : 0;
                if (it is TextItem text)
                {
                    IReadOnlyList<LaidOutLine> lines = text.Styled.Layout.Lines;
                    if (text.KeepTogether && l == 0 && piece.Count > 0 && text.Height <= pageRoom + Epsilon && gap + text.Height > room - used + Epsilon)
                    {
                        break; // it fits on a page of its own: the piece ends before it
                    }

                    int taken = 0;
                    double height = 0;
                    while (l + taken < lines.Count && gap + height + lines[l + taken].Pitch <= room - used + Epsilon)
                    {
                        height += lines[l + taken].Pitch;
                        taken++;
                    }

                    if (taken == 0)
                    {
                        break;
                    }

                    used += gap + height;
                    piece.Add(new Slice(i, l, taken, used));
                    if (l + taken < lines.Count)
                    {
                        l += taken;
                        stopped = true; // the piece ends inside the paragraph
                    }
                    else
                    {
                        i++;
                        l = 0;
                    }
                }
                else
                {
                    FixedItem fixedItem = (FixedItem)it;
                    if (gap + fixedItem.Height > room - used + Epsilon)
                    {
                        break;
                    }

                    used += gap + fixedItem.Height;
                    piece.Add(new Slice(i, 0, -1, used));
                    i++;
                    l = 0;
                }
            }

            if (piece.Count == 0)
            {
                if (_hasContent)
                {
                    ClosePage();
                    continue;
                }

                // At the top of an empty page and still nothing fits: a line taller than the page is drawn anyway, and a whole block that is taller than the page cannot be laid out.
                if (items[item] is TextItem tall)
                {
                    piece.Add(new Slice(item, line, 1, tall.Styled.Layout.Lines[line].Pitch));
                    used = tall.Styled.Layout.Lines[line].Pitch;
                    i = item;
                    l = line + 1;
                    if (l >= tall.Styled.Layout.Lines.Count)
                    {
                        i++;
                        l = 0;
                    }
                }
                else
                {
                    FixedItem big = (FixedItem)items[item];
                    throw new InvalidOperationException($"A {big.Kind} {big.Height:0.##} pt high is taller than the {pageRoom:0.##} pt a page can hold inside the callout, and it is never split.");
                }
            }
            else if (i < items.Count && piece.Count > 1 && piece[^1] is { Count: > 0, From: 0 } last && items[last.Index] is TextItem { KeepWithNext: true } heading && last.Count == heading.Styled.Layout.Lines.Count)
            {
                // A heading that ends the piece goes to the next piece with what it heads.
                piece.RemoveAt(piece.Count - 1);
                used = piece[^1].Bottom;
                i = last.Index;
                l = 0;
            }

            double boxHeight = plan.InsetTop + used + plan.InsetBottom;
            double top = _y + before;
            DrawCalloutBox(plan, _left, top, boxHeight);
            double at = top + plan.InsetTop;
            for (int s = 0; s < piece.Count; s++)
            {
                Slice slice = piece[s];
                Item it = items[slice.Index];
                if (s > 0)
                {
                    at += items[slice.Index - 1].After + it.Before;
                }

                if (it is TextItem text)
                {
                    Draw(text.Styled, slice.From, slice.Count, _left + plan.InsetLeft, at);
                    at += text.Styled.Layout.Lines.Skip(slice.From).Take(slice.Count).Sum(x => x.Pitch);
                }
                else
                {
                    FixedItem fixedItem = (FixedItem)it;
                    fixedItem.Draw(_left + plan.InsetLeft, at);
                    at += fixedItem.Height;
                }
            }

            _y = top + boxHeight;
            _hasContent = true;
            item = i;
            line = l;
            first = false;
            if (item < items.Count)
            {
                ClosePage();
            }
        }

        _y = Math.Min(_y + plan.After, _contentHeight);
    }

    // ---- header and footer

    /// <summary>
    /// Draws the header and the footer of every page, after the whole body has been laid out: the number of pages is known, so a footer can say "page 2 of 7", and the body was laid out once, so a streamed table was read once.
    /// Each page's overlay is a second content stream, written here.
    /// </summary>
    private void DrawOverlays()
    {
        if (_pending.Count == 0)
        {
            return;
        }

        int count = _pending.Count;
        double headerOffset = _options.HeaderOffset?.Points ?? _options.Margins.Top.Points / 2;
        double footerOffset = _options.FooterOffset?.Points ?? _options.Margins.Bottom.Points / 2;
        for (int n = 0; n < count; n++)
        {
            _cancellation.ThrowIfCancellationRequested();
            PdfPageWriter page = _pending[n];
            _page = page;
            PageContext context = new(n + 1, count, _options.PageSize);
            if (_options.Header?.Invoke(context) is { } header)
            {
                double room = _options.Margins.Top.Points - headerOffset;
                page.UseHeader();
                Item item = BuildOverlay(header, room, "header", n + 1);
                CheckFits(item, room, "header", "top", n + 1);
                DrawItem(item, _left, headerOffset - _top);
            }

            if (_options.Footer?.Invoke(context) is { } footer)
            {
                double room = _options.Margins.Bottom.Points - footerOffset;
                page.UseFooter();
                Item item = BuildOverlay(footer, room, "footer", n + 1);
                double height = CheckFits(item, room, "footer", "bottom", n + 1);
                DrawItem(item, _left, _pageHeight - footerOffset - height - _top);
            }

            _page = null;
            page.Finish();
            _pending[n] = null!;
        }

        _pending.Clear();
    }

    /// <summary>Lays out the header or the footer of a page; a block that cannot be laid out there fails with the page and which of the two it was (the original exception is the inner one).</summary>
    private Item BuildOverlay(Block block, double room, string what, int pageNumber)
    {
        try
        {
            return BuildItem(block, _contentWidth, room, _options.Direction);
        }
        catch (InvalidOperationException error)
        {
            throw new InvalidOperationException($"The {what} of page {pageNumber} cannot be laid out: {error.Message}", error);
        }
    }

    private static double CheckFits(Item item, double room, string what, string margin, int pageNumber)
    {
        double height = HeightOf(item);
        if (height > room + 1e-6)
        {
            throw new InvalidOperationException($"The {what} of page {pageNumber} is {height:0.##} pt high, and the {margin} margin leaves {Math.Max(0, room):0.##} pt for it (the margin less the {what} offset): make the margin or the {what} smaller.");
        }

        return height;
    }
}
