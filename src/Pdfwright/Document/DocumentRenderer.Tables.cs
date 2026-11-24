using Pdfwright.Pdf;
using Pdfwright.Text;

namespace Pdfwright.Document;

/// <summary>The table part of the flow: column widths, rows laid out cell by cell, the page breaks between and inside rows, fills and borders.</summary>
internal sealed partial class DocumentRenderer
{
    /// <summary>How many rows are held to measure the automatic columns.</summary>
    internal const int AutoSampleRows = 100;

    /// <summary>
    /// The widths of the columns, in the order of the table's columns, inside <paramref name="available"/> points. Fixed columns take their length; the minimum of an automatic column is
    /// <paramref name="autoMin"/> and what it wants is <paramref name="autoMax"/>; a fraction column's minimum is <paramref name="fractionMin"/>. When the minimums do not fit the columns share the room by water-filling: a column whose minimum is within an equal share of what is left keeps it, and
    /// only the wider ones are cut down, to an equal share of what the others leave;
    /// otherwise the automatic columns grow towards their wants (in proportion to how much each wants when there is not room for all) and the fraction columns share the rest by weight.
    /// </summary>
    internal static double[] ColumnWidths(IReadOnlyList<TableColumn> columns, double available, double[] autoMin, double[] autoMax, double fractionMin)
    {
        int n = columns.Count;
        double[] min = new double[n];
        for (int i = 0; i < n; i++)
        {
            ColumnWidth width = columns[i].Width;
            min[i] = width.Kind switch
            {
                ColumnWidthKind.Fixed => width.Length.Points,
                ColumnWidthKind.Fraction => fractionMin,
                _ => autoMin[i],
            };
        }

        double total = min.Sum();
        double[] result = new double[n];
        if (total > available)
        {
            // Water-filling: the columns whose minimum is within the equal share of the room left keep it (until no more do), and the rest divide what remains equally.
            bool[] kept = new bool[n];
            double left = Math.Max(0, available);
            int open = n;
            bool changed = true;
            while (changed && open > 0)
            {
                changed = false;
                double share = left / open;
                for (int i = 0; i < n; i++)
                {
                    if (!kept[i] && min[i] <= share)
                    {
                        kept[i] = true;
                        result[i] = min[i];
                        left -= min[i];
                        open--;
                        changed = true;
                    }
                }
            }

            for (int i = 0; i < n; i++)
            {
                if (!kept[i])
                {
                    result[i] = left / open;
                }
            }

            return result;
        }

        double rest = available - total;
        double want = 0;
        for (int i = 0; i < n; i++)
        {
            if (columns[i].Width.Kind == ColumnWidthKind.Auto)
            {
                want += Math.Max(0, autoMax[i] - autoMin[i]);
            }
        }

        double grow = Math.Min(rest, want);
        rest -= grow;
        double weights = columns.Sum(c => c.Width.Kind == ColumnWidthKind.Fraction ? c.Width.Weight : 0);
        for (int i = 0; i < n; i++)
        {
            result[i] = min[i];
            switch (columns[i].Width.Kind)
            {
                case ColumnWidthKind.Auto when want > 0:
                    result[i] += Math.Max(0, autoMax[i] - autoMin[i]) * grow / want;
                    break;
                case ColumnWidthKind.Fraction:
                    result[i] += rest * columns[i].Width.Weight / weights;
                    break;
            }
        }

        return result;
    }

    private void PlaceTable(Table table, int blockIndex)
    {
        using TableRun run = new(this, table, blockIndex);
        run.Run();
    }

    /// <summary>The row source: rows are pulled once, in order; the first rows are held for the measuring of automatic columns and then handed on.</summary>
    private sealed class RowSource(IEnumerable<TableRow> rows) : IDisposable
    {
        private readonly List<TableRow> _sample = [];
        private IEnumerator<TableRow>? _enumerator;
        private int _next;
        private bool _ended;

        internal IReadOnlyList<TableRow> Sample => _sample;

        public void Dispose() => _enumerator?.Dispose();

        internal void Prefetch(int count)
        {
            while (_sample.Count < count && Pull() is { } row)
            {
                _sample.Add(row);
            }
        }

        /// <summary>Whether there is another row; it is pulled and held, not laid out.</summary>
        internal bool HasNext()
        {
            if (_next < _sample.Count)
            {
                return true;
            }

            if (Pull() is { } row)
            {
                _sample.Add(row);
                return true;
            }

            return false;
        }

        internal TableRow? Next()
        {
            if (_next < _sample.Count)
            {
                TableRow row = _sample[_next];
                _sample[_next] = null!;
                _next++;
                if (_next == _sample.Count)
                {
                    _sample.Clear();
                    _next = 0;
                }

                return row;
            }

            return Pull();
        }

        private TableRow? Pull()
        {
            if (_ended)
            {
                return null;
            }

            _enumerator ??= rows.GetEnumerator();
            if (!_enumerator.MoveNext())
            {
                _ended = true;
                return null;
            }

            return _enumerator.Current ?? throw new InvalidOperationException("A row of the table is null.");
        }
    }

    private readonly record struct CellItem(StyledLayout Styled, int Line, double Top, double Bottom);

    private sealed class CellPlan
    {
        private List<CellItem>? _items;

        /// <summary>The lines of the cell. A cell nearly always is one paragraph of one or two lines, so the list is made when the first paragraph is laid out, with room for its lines (see <see cref="Reserve"/>), and no list grows from four; one that is read before that is an empty list.</summary>
        internal List<CellItem> Items => _items ??= [];

        /// <summary>Makes <see cref="Items"/> with room for <paramref name="lines"/> if it is not made yet, and makes room for that many more if it is.</summary>
        internal void Reserve(int lines)
        {
            if (_items is null)
            {
                _items = new List<CellItem>(lines);
            }
            else
            {
                Items.EnsureCapacity(Items.Count + lines);
            }
        }

        internal double PadTop { get; init; }

        internal double PadBottom { get; init; }

        internal double PadLeft { get; init; }

        internal Color? Fill { get; init; }

        /// <summary>The height of the lines from the item at <paramref name="from"/> to the item before <paramref name="to"/>, without padding.</summary>
        internal double Span(int from, int to) => Items[to - 1].Bottom - Items[from].Top;
    }

    private sealed class RowPlan
    {
        internal required CellPlan?[] Cells { get; init; }

        internal Color? RowFill { get; init; }

        internal bool IsHeader { get; init; }

        internal int BodyIndex { get; init; }

        /// <summary>The row's structure elements when the document is tagged, or <see langword="null"/>.</summary>
        internal RowTags? Tags { get; init; }

        internal bool IsDone(int[] cursor)
        {
            for (int i = 0; i < Cells.Length; i++)
            {
                if (Cells[i] is { } cell && cursor[i] < cell.Items.Count)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>The height of what is left of the row from the cursors: the tallest cell, padding included.</summary>
        internal double Remaining(int[] cursor)
        {
            double height = 0;
            for (int i = 0; i < Cells.Length; i++)
            {
                if (Cells[i] is { } cell && cursor[i] < cell.Items.Count)
                {
                    height = Math.Max(height, cell.PadTop + cell.Span(cursor[i], cell.Items.Count) + cell.PadBottom);
                }
            }

            return height;
        }

        /// <summary>The least that can be drawn of what is left: the tallest first line, padding included.</summary>
        internal double MinCut(int[] cursor)
        {
            double height = 0;
            for (int i = 0; i < Cells.Length; i++)
            {
                if (Cells[i] is { } cell && cursor[i] < cell.Items.Count)
                {
                    height = Math.Max(height, cell.PadTop + cell.Span(cursor[i], cursor[i] + 1) + cell.PadBottom);
                }
            }

            return height;
        }
    }

    /// <summary>
    /// The structure elements of a row of a tagged table: its <c>TR</c> and a <c>TH</c> or <c>TD</c> for each column, light until the row is first drawn.
    /// The row keeps its cells open together (<see cref="PdfStructureElement.KeepsChildrenOpen"/>): a row split between pages returns to each cell.
    /// </summary>
    private sealed class RowTags(PdfStructureElement row, PdfStructureElement?[] cells)
    {
        internal PdfStructureElement Row { get; } = row;

        /// <summary>The cells, one for each column of the table (none is null once the row is laid out), in logical order: the first column first, whichever side of the page it stands on.</summary>
        internal PdfStructureElement?[] Cells { get; } = cells;

        /// <summary>Whether the row and its cells are materialised: they are, all of them in order, before anything of the row is drawn.</summary>
        internal bool Opened { get; set; }
    }

    private sealed class TableRun : IDisposable
    {
        private readonly DocumentRenderer _r;
        private readonly Table _table;
        private readonly int _blockIndex;
        private readonly RowSource _source;
        private readonly TextDirection _direction;
        private readonly bool _rightToLeft;
        private readonly ResolvedStyle _tableStyle;
        private readonly ResolvedStyle _headerStyle;
        private double[] _widths = [];
        private double[] _left = [];

        // The columns in the order they stand on the page, left to right (the reverse of their order in a right-to-left table), made once with the positions: the fills and the borders of every row use it.
        private int[] _leftToRight = [];
        private double _tableLeft;
        private double _tableWidth;
        private RowPlan[] _header = [];
        private double _headerHeight;
        private bool _repeat;
        private bool _started;
        private bool _fragmentOpen;
        private bool _fresh;
        private bool _unrepeatedHeaderOnPage;
        private int _rowsInFragment;

        // Tagged, the table is a Table with a THead for the header rows and a TBody for the others (a table with no body row has its header rows
        // straight under Table, and _headTag is the Table); untagged, there are none.
        private PdfStructureElement? _tag;
        private PdfStructureElement? _headTag;
        private PdfStructureElement? _bodyTag;

        /// <summary>
        /// The elements of the body row placed last, written and closed, for the next body row to make again (<see cref="PdfStructureTree.Reuse"/>):
        /// a placed row's plan is dropped, and the file and the tree refer to its elements by number only, so a long table holds one row's elements
        /// and makes them once. A header row's are kept for the header drawn again on later pages, and are never given back.
        /// </summary>
        private RowTags? _spareTags;

        /// <summary>The cells of <see cref="_spareTags"/> while the next row is laid out, each taken when that row's cell at its place is made.</summary>
        private PdfStructureElement?[] _spareCells = [];

        internal TableRun(DocumentRenderer renderer, Table table, int blockIndex)
        {
            _r = renderer;
            _table = table;
            _blockIndex = blockIndex;
            _source = new RowSource(table.Rows);
            _direction = table.Direction ?? renderer._options.Direction;
            _rightToLeft = _direction == TextDirection.RightToLeft;
            _tableStyle = renderer._base.Apply(table.Style);
            _headerStyle = _tableStyle.Apply(table.HeaderStyle);
        }

        public void Dispose() => _source.Dispose();

        internal void Run()
        {
            Measure();

            // Tagged, the elements are light until the first row is drawn; a table that draws nothing is not in the structure.
            _tag = _r.Tag("Table");
            if (_tag is not null)
            {
                // Whether a body row will be drawn is known before the header's rows are made: the first row is pulled and held, not laid out, as the
                // loop below pulls it first (a row limit is 1 or more, so a first row is always drawn). A table of a header and no body has its header
                // rows straight under Table, with no THead: PDF/UA-1 (7.2) wants a THead followed by a TBody, and a Table may hold its rows as TR
                // without groups (ISO 32000-1, 14.8.4.3.4); its cells are still TH of their columns.
                PdfStructureTree structure = _r._writer.Structure!;
                bool body = _source.HasNext();
                _headTag = _table.Header.Count == 0 ? null : body ? structure.Create(_tag, "THead") : _tag;
                _bodyTag = body ? structure.Create(_tag, "TBody") : null;
            }

            _header = [.. _table.Header.Select(row => Layout(row, true, 0))];
            _headerHeight = _header.Sum(h => h.Remaining(new int[h.Cells.Length]));
            _repeat = _table.RepeatHeader && _header.Length > 0 && _headerHeight * 2 <= _r._contentHeight;

            RowPlan? pending = NextRow(0);
            bool drewAnything = false;
            double before = _table.SpaceBefore?.Points ?? 0;
            bool onPage = _r._page is not null && _r._hasContent;
            if (onPage)
            {
                // A table whose header does not fit with one row under it starts on the next page.
                double need = _header.Length > 0 && _headerHeight * 2 <= _r._contentHeight ? _headerHeight : 0;
                if (pending is not null)
                {
                    double capacity = _r._contentHeight - (_repeat ? _headerHeight : 0);
                    double whole = pending.Remaining(new int[pending.Cells.Length]);
                    need += whole <= capacity + 1e-9 ? whole : pending.MinCut(new int[pending.Cells.Length]);
                }

                if (_r._y + before + need > _r._contentHeight)
                {
                    _r.ClosePage();
                    onPage = false;
                }
                else
                {
                    _r._y += before;
                }
            }

            _fresh = !onPage;
            if (onPage)
            {
                EnsureFragment();
            }

            foreach (RowPlan header in _header)
            {
                PlaceRow(header);
                drewAnything = true;
            }

            int count = 0;
            int? tableMax = _table.MaxRows;
            int? limit = _r._options.Limits.MaxTableRows;
            bool tableFirst = tableMax is { } tm && (limit is not { } lm || tm <= lm);
            while (true)
            {
                _r._cancellation.ThrowIfCancellationRequested();
                if (EffectiveLimit() is { } stop && count == stop)
                {
                    // A row past the limit is only looked for: never laid out, checked or counted.
                    if (_source.HasNext())
                    {
                        if (tableFirst)
                        {
                            _r._truncated.Add(new TruncatedTable(_blockIndex, tableMax!.Value));
                        }
                        else
                        {
                            throw new PdfLimitExceededException(DocumentLimit.MaxTableRows, limit!.Value);
                        }
                    }

                    break;
                }

                if (pending is null)
                {
                    break;
                }

                PlaceRow(pending);
                drewAnything = true;
                count++;
                pending = NextRow(count);
            }

            if (drewAnything)
            {
                CloseFragment();
                _r._y = Math.Min(_r._y + (_table.SpaceAfter?.Points ?? (_r._base.Size * 0.6)), _r._contentHeight);
            }

            if (_tag is not null)
            {
                // The table is done (a truncated one too: the rows past the limit were never laid out, so they have no elements).
                _r._writer.Structure!.Close(_tag);
            }
        }

        /// <summary>The number of body rows drawn at most: the least of the table's own limit and the document's; <see langword="null"/> for none.</summary>
        private int? EffectiveLimit()
        {
            int? limit = _r._options.Limits.MaxTableRows;
            return (_table.MaxRows, limit) switch
            {
                ({ } a, { } b) => Math.Min(a, b),
                ({ } a, null) => a,
                (null, { } b) => b,
                _ => null,
            };
        }

        private RowPlan? NextRow(int bodyIndex)
        {
            if (EffectiveLimit() is { } stop && bodyIndex >= stop)
            {
                return null;
            }

            TableRow? row = _source.Next();
            if (row is null)
            {
                return null;
            }

            if (row.Cells.Count > _table.Columns.Count)
            {
                throw new InvalidOperationException($"Row {bodyIndex + 1} of the table has {row.Cells.Count} cells and the table has {_table.Columns.Count} columns.");
            }

            return Layout(row, false, bodyIndex);
        }

        /// <summary>Decides the widths of the columns, measuring the automatic ones from the header and the sample of the first rows.</summary>
        private void Measure()
        {
            IReadOnlyList<TableColumn> columns = _table.Columns;
            int n = columns.Count;
            double[] autoMin = new double[n];
            double[] autoMax = new double[n];
            double padding = _table.CellPadding.Left.Points + _table.CellPadding.Right.Points;
            if (columns.Any(c => c.Width.Kind == ColumnWidthKind.Auto))
            {
                int? drawn = EffectiveLimit();
                _source.Prefetch(drawn is { } d ? (int)Math.Min(AutoSampleRows, (long)d + 1) : AutoSampleRows);
                Array.Fill(autoMin, padding);
                Array.Fill(autoMax, padding);
                foreach (TableRow row in _table.Header)
                {
                    Sample(row, true, autoMin, autoMax);
                }

                // Only the rows that will be drawn are measured: the one read past the limit is there to know that the table goes on.
                foreach (TableRow row in _source.Sample.Take(drawn ?? int.MaxValue))
                {
                    Sample(row, false, autoMin, autoMax);
                }
            }

            double available = _r._contentWidth;
            _widths = ColumnWidths(columns, available, autoMin, autoMax, padding);
            _tableWidth = _widths.Sum();
            _tableLeft = _r._left + (_rightToLeft ? _r._contentWidth - _tableWidth : 0);
            _left = new double[n];
            double x = 0;
            for (int i = 0; i < n; i++)
            {
                _left[i] = _rightToLeft ? _tableLeft + _tableWidth - x - _widths[i] : _tableLeft + x;
                x += _widths[i];
            }

            _leftToRight = LeftToRight(_left, n);
        }

        /// <summary>
        /// The indices of the first <paramref name="count"/> columns ordered by their left edges, columns at the same place in the order of their indices: what a stable sort by the edge gives (the
        /// <c>OrderBy</c> this replaces, which for a row of a few columns cost an iterator, a sorter and their code, compiled at run time even in a ReadyToRun build).
        /// </summary>
        private static int[] LeftToRight(double[] left, int count)
        {
            int[] order = new int[count];
            for (int i = 0; i < count; i++)
            {
                // An insertion sort, stable: a column moves before those whose edge is greater only.
                int at = i;
                while (at > 0 && left[order[at - 1]].CompareTo(left[i]) > 0)
                {
                    order[at] = order[at - 1];
                    at--;
                }

                order[at] = i;
            }

            return order;
        }

        private void Sample(TableRow row, bool header, double[] autoMin, double[] autoMax)
        {
            for (int i = 0; i < row.Cells.Count && i < _table.Columns.Count; i++)
            {
                if (_table.Columns[i].Width.Kind != ColumnWidthKind.Auto)
                {
                    continue;
                }

                TableCell cell = row.Cells[i];
                Margins pad = cell.Padding ?? _table.CellPadding;
                double padding = pad.Left.Points + pad.Right.Points;
                ResolvedStyle style = (header ? _headerStyle : _tableStyle).Apply(cell.Style);
                double min = 0;
                double max = 0;
                foreach (Paragraph paragraph in cell.Paragraphs)
                {
                    ResolvedStyle paragraphStyle = style.Apply(paragraph.Style);
                    TextDirection direction = paragraph.Direction ?? _direction;
                    max = Math.Max(max, BlockText.Layout(paragraph.Runs, paragraphStyle, _r._fonts, null, TextAlignment.Start, direction, 1.0).Layout.Width);
                    foreach (TextRun textRun in paragraph.Runs)
                    {
                        // The narrowest a paragraph goes is its widest word: each word is measured alone, in the style of its run.
                        foreach (string word in textRun.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                        {
                            min = Math.Max(min, BlockText.Layout([new TextRun(word, textRun.Style)], paragraphStyle, _r._fonts, null, TextAlignment.Start, direction, 1.0).Layout.Width);
                        }
                    }
                }

                autoMin[i] = Math.Max(autoMin[i], Math.Min(min, max) + padding);
                autoMax[i] = Math.Max(autoMax[i], max + padding);
            }
        }

        /// <summary>Lays out the cells of a row at the widths of their columns.</summary>
        private RowPlan Layout(TableRow row, bool header, int bodyIndex)
        {
            int n = _table.Columns.Count;
            CellPlan?[] cells = new CellPlan?[n];
            PdfStructureElement? rowTag = null;
            PdfStructureElement?[]? cellTags = null;
            RowTags? spare = null;
            if (_tag is not null)
            {
                PdfStructureTree structure = _r._writer.Structure!;
                spare = header ? null : _spareTags;
                _spareTags = null;
                if (spare is not null)
                {
                    // The cells of the row placed before are set aside, to be made again at their places, and the row's array is filled anew.
                    rowTag = structure.Reuse(spare.Row, _bodyTag!, "TR");
                    cellTags = spare.Cells;
                    if (_spareCells.Length != n)
                    {
                        _spareCells = new PdfStructureElement?[n];
                    }

                    cellTags.AsSpan().CopyTo(_spareCells);
                    Array.Clear(cellTags);
                    spare.Opened = false;
                }
                else
                {
                    rowTag = structure.Create(header ? _headTag! : _bodyTag!, "TR");
                    cellTags = new PdfStructureElement?[n];
                }

                rowTag.KeepsChildrenOpen = true;
            }

            for (int i = 0; i < row.Cells.Count; i++)
            {
                TableCell cell = row.Cells[i];
                Margins pad = cell.Padding ?? _table.CellPadding;
                CellPlan plan = new()
                {
                    PadTop = pad.Top.Points,
                    PadBottom = pad.Bottom.Points,
                    PadLeft = pad.Left.Points,
                    Fill = cell.Fill,
                };
                double inner = Math.Max(1, _widths[i] - pad.Left.Points - pad.Right.Points);
                ResolvedStyle style = (header ? _headerStyle : _tableStyle).Apply(cell.Style);
                TextAlignment alignment = cell.Alignment ?? _table.Columns[i].Alignment;
                double y = 0;
                double previousAfter = 0;
                for (int k = 0; k < cell.Paragraphs.Count; k++)
                {
                    Paragraph paragraph = cell.Paragraphs[k];
                    ResolvedStyle paragraphStyle = style.Apply(paragraph.Style);
                    TextAlignment aligned = paragraph.Alignment != TextAlignment.Start ? paragraph.Alignment : alignment;
                    StyledLayout layout = BlockText.Layout(paragraph.Runs, paragraphStyle, _r._fonts, inner, aligned, paragraph.Direction ?? _direction, paragraph.LineSpacing);
                    _r.NoteFamilies(layout);
                    if (rowTag is not null)
                    {
                        // A cell of one paragraph has its text as the cell's own content, with the paragraph's language; a cell of several has a P for each.
                        // Blank text has no element (it is drawn as an artifact), and its cell stays: an empty cell is an empty TD.
                        if (cell.Paragraphs.Count == 1)
                        {
                            cellTags![i] = CellTag(rowTag, i, header, _r.ElementLanguage(paragraphStyle));
                            layout.Tag = string.IsNullOrWhiteSpace(layout.Layout.Text) ? null : cellTags[i];
                        }
                        else
                        {
                            cellTags![i] ??= CellTag(rowTag, i, header, null);
                            layout.Tag = _r.TextTag(cellTags[i], "P", layout, paragraphStyle);
                        }
                    }

                    if (k > 0)
                    {
                        y += previousAfter + (paragraph.SpaceBefore?.Points ?? 0);
                    }

                    plan.Reserve(layout.Layout.Lines.Count);
                    for (int line = 0; line < layout.Layout.Lines.Count; line++)
                    {
                        double pitch = layout.Layout.Lines[line].Pitch;
                        plan.Items.Add(new CellItem(layout, line, y, y + pitch));
                        y += pitch;
                    }

                    previousAfter = paragraph.SpaceAfter?.Points ?? (paragraphStyle.Size * 0.6);
                }

                plan.Reserve(0);
                cells[i] = plan;
            }

            RowTags? tags = null;
            if (rowTag is not null)
            {
                // A row with fewer cells than the table has columns: the missing ones are empty cells, so that every row of the structure has a cell
                // for each column and a cell's header is the one at its place (a table has no spans, and PDF/UA-1 needs a regular table to tell it).
                for (int i = row.Cells.Count; i < n; i++)
                {
                    cellTags![i] = CellTag(rowTag, i, header, null);
                }

                tags = spare ?? new RowTags(rowTag, cellTags!);
            }

            return new RowPlan { Cells = cells, RowFill = row.Fill, IsHeader = header, BodyIndex = bodyIndex, Tags = tags };
        }

        /// <summary>
        /// A light cell element under <paramref name="row"/> for the column at <paramref name="column"/>: a <c>TH</c> of its column (<c>/Scope /Column</c>)
        /// in a header row, a <c>TD</c> otherwise, made again from the cell of the row placed before at the same place when there is one.
        /// </summary>
        private PdfStructureElement CellTag(PdfStructureElement row, int column, bool header, string? language)
        {
            PdfStructureTree structure = _r._writer.Structure!;
            if (header)
            {
                return structure.Create(row, "TH", language: language, attributes: new PdfDictionary().Set("O", "Table").Set("Scope", "Column"));
            }

            if (column < _spareCells.Length && _spareCells[column] is { IsClosed: true } spare)
            {
                _spareCells[column] = null;
                return structure.Reuse(spare, row, "TD", language: language);
            }

            return structure.Create(row, "TD", language: language);
        }

        private void EnsureFragment()
        {
            if (_r._page is null)
            {
                _r.OpenPage();
                _fragmentOpen = false;
                _fresh = true;
            }

            if (_fragmentOpen)
            {
                return;
            }

            _fragmentOpen = true;
            _rowsInFragment = 0;
            if (_started && _repeat)
            {
                foreach (RowPlan header in _header)
                {
                    int[] cursor = new int[header.Cells.Length];
                    DrawFragment(header, cursor, double.PositiveInfinity, repeated: true);
                }
            }

            _started = true;
        }

        private void NewPage()
        {
            CloseFragment();
            _r.ClosePage();
            _unrepeatedHeaderOnPage = false;
        }

        /// <summary>Places one row, in one piece on this page, or on the next, or in pieces between lines when it is taller than a page.</summary>
        private void PlaceRow(RowPlan row)
        {
            PlaceRowPieces(row);
            if (row.Tags is { } tags)
            {
                // The row is placed: its TR and its cells are written now, so a table of any length holds one row's elements at a time, and a body
                // row's elements are made again for the next body row.
                _r._writer.Structure!.Close(tags.Row);
                if (!row.IsHeader && tags.Row.IsClosed)
                {
                    _spareTags = tags;
                }
            }
        }

        private void PlaceRowPieces(RowPlan row)
        {
            int[] cursor = new int[row.Cells.Length];
            while (true)
            {
                _r._cancellation.ThrowIfCancellationRequested();
                EnsureFragment();
                double remaining = _r._contentHeight - _r._y;
                double height = row.Remaining(cursor);
                if (height <= remaining + 1e-9)
                {
                    DrawFragment(row, cursor, remaining);
                    return;
                }

                double capacity = _r._contentHeight - (_repeat ? _headerHeight : 0);
                bool whole = cursor.All(c => c == 0);
                // Moving to the next page helps unless this page is already as empty as that one will be: a page that holds only a header which will not be repeated is not.
                bool moveHelps = !_fresh || _unrepeatedHeaderOnPage;
                if (moveHelps && ((whole && height <= capacity + 1e-9) || row.MinCut(cursor) > remaining + 1e-9))
                {
                    NewPage();
                    continue;
                }

                DrawFragment(row, cursor, remaining);
                NewPage();
                if (row.IsDone(cursor))
                {
                    return;
                }
            }
        }

        /// <summary>
        /// Draws the part of a row that fits in <paramref name="budget"/> from its cursors (at least one line of a cell when none fits), moves the cursors and the flow down, and returns nothing.
        /// <paramref name="repeated"/> is a header row drawn again at the top of a later page.
        /// </summary>
        private void DrawFragment(RowPlan row, int[] cursor, double budget, bool repeated = false)
        {
            int n = row.Cells.Length;
            int[] take = new int[n];
            double height = 0;
            if (row.Remaining(cursor) <= budget + 1e-9)
            {
                for (int i = 0; i < n; i++)
                {
                    take[i] = row.Cells[i] is { } cell ? cell.Items.Count - cursor[i] : 0;
                }

                height = row.Remaining(cursor);
            }
            else
            {
                bool any = false;
                for (int i = 0; i < n; i++)
                {
                    if (row.Cells[i] is not { } cell)
                    {
                        continue;
                    }

                    int start = cursor[i];
                    int end = start;
                    while (end < cell.Items.Count && cell.PadTop + cell.Span(start, end + 1) + cell.PadBottom <= budget + 1e-9)
                    {
                        end++;
                    }

                    take[i] = end - start;
                    any |= take[i] > 0;
                }

                if (!any)
                {
                    for (int i = 0; i < n; i++)
                    {
                        take[i] = row.Cells[i] is { } cell && cursor[i] < cell.Items.Count ? 1 : 0;
                    }
                }

                for (int i = 0; i < n; i++)
                {
                    if (take[i] > 0)
                    {
                        CellPlan cell = row.Cells[i]!;
                        height = Math.Max(height, cell.PadTop + cell.Span(cursor[i], cursor[i] + take[i]) + cell.PadBottom);
                    }
                }
            }

            double top = _r._y;

            // Tagged, the fills are an artifact drawn before the row's text and the borders one drawn after it, so that neither is inside a cell's marked
            // content. A header row drawn again on a later page is a pagination artifact, fills, text and borders, which a reader does not read twice:
            // its first drawing is the table's header in the structure, and its elements are closed, so its text is drawn without them.
            bool pagination = false;
            if (repeated && row.Tags is not null)
            {
                pagination = _r.BeginRepeatedArtifact();
            }
            else if (row.Tags is { Opened: false } tags)
            {
                // The row and all its cells are materialised in logical order before anything of the row is drawn: a cell whose first line does not fit on
                // this page, or an empty cell, still has its place among the others.
                PdfStructureTree structure = _r._writer.Structure!;
                structure.Open(tags.Row);
                foreach (PdfStructureElement? cellTag in tags.Cells)
                {
                    structure.Open(cellTag!);
                }

                tags.Opened = true;
            }

            DrawFills(row, top, height);
            for (int i = 0; i < n; i++)
            {
                if (row.Cells[i] is not { } cell || take[i] == 0)
                {
                    continue;
                }

                int first = cursor[i];
                double baseTop = cell.Items[first].Top;
                int j = first;
                while (j < first + take[i])
                {
                    StyledLayout styled = cell.Items[j].Styled;
                    int end = j;
                    while (end + 1 < first + take[i] && ReferenceEquals(cell.Items[end + 1].Styled, styled))
                    {
                        end++;
                    }

                    _r.Draw(styled, cell.Items[j].Line, end - j + 1, _left[i] + cell.PadLeft, top + cell.PadTop + cell.Items[j].Top - baseTop, pagination);
                    j = end + 1;
                }

                cursor[i] += take[i];
            }

            DrawBorders(top, height);
            _r.EndArtifact(pagination);
            _r._y = top + height;
            _r._hasContent = true;
            _rowsInFragment++;
            if (!row.IsHeader)
            {
                _fresh = false;
            }
            else if (!_repeat)
            {
                _unrepeatedHeaderOnPage = true;
            }
        }

        private double PdfY(double y) => _r._pageHeight - _r._top - y;

        private void DrawFills(RowPlan row, double top, double height)
        {
            if (height <= 0)
            {
                return;
            }

            int n = row.Cells.Length;
            Color? tableFill = row.IsHeader ? _table.HeaderFill : (row.BodyIndex % 2 == 1 ? _table.ZebraFill : null);
            Color? ColourOf(int column) => row.Cells[column]?.Fill ?? row.RowFill ?? tableFill;

            // Columns in the order they stand on the page, neighbours of one colour joined into one rectangle so that no seam shows between them.
            int[] order = n == _leftToRight.Length ? _leftToRight : LeftToRight(_left, n);
            ContentStreamBuilder content = _r._page!.Content;
            bool open = false;
            bool artifact = false;
            int s = 0;
            while (s < n)
            {
                Color? colour = ColourOf(order[s]);
                int e = s;
                while (e + 1 < n && ColourOf(order[e + 1]) == colour)
                {
                    e++;
                }

                if (colour is { } fill)
                {
                    if (!open)
                    {
                        // Tagged, the fills are an artifact; begun with the first, so that a row with none has no empty one.
                        artifact = _r.BeginArtifact();
                        content.SaveState();
                        open = true;
                    }

                    _ = fill.IsGray ? content.FillGray(fill.R) : content.FillRgb(fill.R, fill.G, fill.B);
                    double x0 = _left[order[s]];
                    double x1 = _left[order[e]] + _widths[order[e]];
                    content.Rectangle(x0, PdfY(top + height), x1 - x0, height).Fill();
                }

                s = e + 1;
            }

            if (open)
            {
                content.RestoreState();
                _r.EndArtifact(artifact);
            }
        }

        private void DrawBorders(double top, double height)
        {
            Borders b = _table.Borders;
            ContentStreamBuilder content = _r._page!.Content;
            BorderLine? above = _rowsInFragment == 0 ? b.OuterHorizontal : b.InnerHorizontal;
            bool vertical = b.OuterVertical is not null || b.InnerVertical is not null;
            if (above is null && b.OuterVertical is null && (b.InnerVertical is null || _widths.Length < 2))
            {
                return;
            }

            // Tagged, the lines are an artifact, drawn after the row's text.
            bool artifact = _r.BeginArtifact();
            double right = _tableLeft + _tableWidth;
            Line(content, above, _tableLeft, PdfY(top), right, PdfY(top));
            if (vertical)
            {
                int[] order = _widths.Length == _leftToRight.Length ? _leftToRight : LeftToRight(_left, _widths.Length);
                Line(content, b.OuterVertical, _tableLeft, PdfY(top), _tableLeft, PdfY(top + height));
                Line(content, b.OuterVertical, right, PdfY(top), right, PdfY(top + height));
                for (int k = 0; k + 1 < order.Length; k++)
                {
                    double x = _left[order[k]] + _widths[order[k]];
                    Line(content, b.InnerVertical, x, PdfY(top), x, PdfY(top + height));
                }
            }

            _r.EndArtifact(artifact);
        }

        private void CloseFragment()
        {
            if (_fragmentOpen && _rowsInFragment > 0 && _r._page is not null && _table.Borders.OuterHorizontal is not null)
            {
                bool artifact = _r.BeginArtifact();
                Line(_r._page.Content, _table.Borders.OuterHorizontal, _tableLeft, PdfY(_r._y), _tableLeft + _tableWidth, PdfY(_r._y));
                _r.EndArtifact(artifact);
            }

            _fragmentOpen = false;
            _rowsInFragment = 0;
        }

        private static void Line(ContentStreamBuilder content, BorderLine? line, double x0, double y0, double x1, double y1)
        {
            if (line is not { } l)
            {
                return;
            }

            content.SaveState();
            content.LineWidth(l.Width.Points);
            content.LineCap(2);
            _ = l.Color.IsGray ? content.StrokeGray(l.Color.R) : content.StrokeRgb(l.Color.R, l.Color.G, l.Color.B);
            content.MoveTo(x0, y0).LineTo(x1, y1).Stroke();
            content.RestoreState();
        }
    }
}
