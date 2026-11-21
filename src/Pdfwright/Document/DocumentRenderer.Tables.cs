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
    /// <paramref name="autoMin"/> and what it wants is <paramref name="autoMax"/>; a fraction column's minimum is <paramref name="fractionMin"/>. When the minimums do not fit every column shrinks in the same proportion;
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
            double scale = available / total;
            for (int i = 0; i < n; i++)
            {
                result[i] = min[i] * scale;
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
        internal List<CellItem> Items { get; } = [];

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
        private double _tableLeft;
        private double _tableWidth;
        private RowPlan[] _header = [];
        private double _headerHeight;
        private bool _repeat;
        private bool _started;
        private bool _fragmentOpen;
        private bool _fresh;
        private int _rowsInFragment;

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
            while (true)
            {
                _r._cancellation.ThrowIfCancellationRequested();
                bool tableFirst = tableMax is { } tm && (limit is not { } lm || tm <= lm);
                if (tableFirst && count == tableMax)
                {
                    if (pending is not null)
                    {
                        _r._truncated.Add(new TruncatedTable(_blockIndex, tableMax!.Value));
                    }

                    break;
                }

                if (limit is { } max && count == max)
                {
                    if (pending is not null)
                    {
                        throw new PdfLimitExceededException(DocumentLimit.MaxTableRows, max);
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
        }

        private RowPlan? NextRow(int bodyIndex)
        {
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
                _source.Prefetch(AutoSampleRows);
                Array.Fill(autoMin, padding);
                Array.Fill(autoMax, padding);
                foreach (TableRow row in _table.Header)
                {
                    Sample(row, true, autoMin, autoMax);
                }

                foreach (TableRow row in _source.Sample)
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
                    if (k > 0)
                    {
                        y += previousAfter + (paragraph.SpaceBefore?.Points ?? 0);
                    }

                    for (int line = 0; line < layout.Layout.Lines.Count; line++)
                    {
                        double pitch = layout.Layout.Lines[line].Pitch;
                        plan.Items.Add(new CellItem(layout, line, y, y + pitch));
                        y += pitch;
                    }

                    previousAfter = paragraph.SpaceAfter?.Points ?? (paragraphStyle.Size * 0.6);
                }

                cells[i] = plan;
            }

            return new RowPlan { Cells = cells, RowFill = row.Fill, IsHeader = header, BodyIndex = bodyIndex };
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
                    DrawFragment(header, cursor, double.PositiveInfinity);
                }
            }

            _started = true;
        }

        private void NewPage()
        {
            CloseFragment();
            _r.ClosePage();
        }

        /// <summary>Places one row, in one piece on this page, or on the next, or in pieces between lines when it is taller than a page.</summary>
        private void PlaceRow(RowPlan row)
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
                if (!_fresh && ((whole && height <= capacity + 1e-9) || row.MinCut(cursor) > remaining + 1e-9))
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

        /// <summary>Draws the part of a row that fits in <paramref name="budget"/> from its cursors (at least one line of a cell when none fits), moves the cursors and the flow down, and returns nothing.</summary>
        private void DrawFragment(RowPlan row, int[] cursor, double budget)
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

                    _r.Draw(styled, cell.Items[j].Line, end - j + 1, _left[i] + cell.PadLeft, top + cell.PadTop + cell.Items[j].Top - baseTop);
                    j = end + 1;
                }

                cursor[i] += take[i];
            }

            DrawBorders(top, height);
            _r._y = top + height;
            _r._hasContent = true;
            _rowsInFragment++;
            if (!row.IsHeader)
            {
                _fresh = false;
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
            int[] order = [.. Enumerable.Range(0, n).OrderBy(i => _left[i])];
            ContentStreamBuilder content = _r._page!.Content;
            bool open = false;
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
            }
        }

        private void DrawBorders(double top, double height)
        {
            Borders b = _table.Borders;
            ContentStreamBuilder content = _r._page!.Content;
            bool first = _rowsInFragment == 0;
            double right = _tableLeft + _tableWidth;
            if (first)
            {
                Line(content, b.OuterHorizontal, _tableLeft, PdfY(top), right, PdfY(top));
            }
            else
            {
                Line(content, b.InnerHorizontal, _tableLeft, PdfY(top), right, PdfY(top));
            }

            if (b.OuterVertical is not null || b.InnerVertical is not null)
            {
                int[] order = [.. Enumerable.Range(0, _widths.Length).OrderBy(i => _left[i])];
                Line(content, b.OuterVertical, _tableLeft, PdfY(top), _tableLeft, PdfY(top + height));
                Line(content, b.OuterVertical, right, PdfY(top), right, PdfY(top + height));
                for (int k = 0; k + 1 < order.Length; k++)
                {
                    double x = _left[order[k]] + _widths[order[k]];
                    Line(content, b.InnerVertical, x, PdfY(top), x, PdfY(top + height));
                }
            }
        }

        private void CloseFragment()
        {
            if (_fragmentOpen && _rowsInFragment > 0 && _r._page is not null)
            {
                Line(_r._page.Content, _table.Borders.OuterHorizontal, _tableLeft, PdfY(_r._y), _tableLeft + _tableWidth, PdfY(_r._y));
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
