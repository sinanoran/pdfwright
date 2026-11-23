namespace Pdfwright.Benchmarks.Scenarios;

/// <summary>
/// The data of the standard table scenario: a header of six columns and <c>rows</c> rows of
/// <c>Row {r} cell {c} text</c>, the text the Playwright baseline script draws too, so that the engines
/// are compared on the same table.
/// </summary>
internal static class TableData
{
    internal const int Columns = 6;

    internal static IReadOnlyList<string> Header { get; } = Enumerable.Range(1, Columns).Select(c => $"Col {c}").ToArray();

    internal static IEnumerable<string[]> Rows(int count)
    {
        for (int r = 0; r < count; r++)
        {
            string[] cells = new string[Columns];
            for (int c = 0; c < Columns; c++)
            {
                cells[c] = $"Row {r} cell {c + 1} text";
            }

            yield return cells;
        }
    }

    /// <summary>The header of the translated tables: six cells of <c>{word} {c}</c>.</summary>
    internal static IReadOnlyList<string> LocalizedHeader(string word) => Enumerable.Range(1, Columns).Select(c => $"{word} {c}").ToArray();

    /// <summary>The same rows as <see cref="Rows"/> in another language: <c>{row} {r} {cell} {c} {text}</c>, with Latin digits.</summary>
    internal static IEnumerable<string[]> LocalizedRows(int count, string row, string cell, string text)
    {
        for (int r = 0; r < count; r++)
        {
            string[] cells = new string[Columns];
            for (int c = 0; c < Columns; c++)
            {
                cells[c] = $"{row} {r} {cell} {c + 1} {text}";
            }

            yield return cells;
        }
    }
}
