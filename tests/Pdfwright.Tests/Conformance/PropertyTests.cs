using Pdfwright.Tests.Document;
using System.Text;
using System.Text.RegularExpressions;

namespace Pdfwright.Tests.Conformance;

/// <summary>
/// Seeded property tests (plan 8.7). Every seed is fixed and every size bounded, so a failure is reproducible and the whole class takes a few seconds:
/// random strings made of the alphabets of the corpus never throw and never put a glyph outside the page, a table keeps its rows whole and its header
/// on every page, and a document is the same bytes twice.
/// </summary>
public class PropertyTests
{
    private static readonly IReadOnlyList<PageCorpus> Corpora = PageCorpus.LoadAll();

    private static readonly string[] Punctuation = [".", ",", ";", ":", "(", ")", "\"", "-", "+", "%", "/", "‏", "‎", "‌"];

    /// <summary>A word of 1 to 10 characters of one alphabet; a mark or a vowel sign may land first, which is the point.</summary>
    private static string Word(Random random, string alphabet)
    {
        StringBuilder word = new();
        int length = random.Next(1, 11);
        for (int i = 0; i < length; i++)
        {
            _ = word.Append(alphabet[random.Next(alphabet.Length)]);
        }

        return word.ToString();
    }

    /// <summary>Words from several scripts in one paragraph: the same script for a while, then another, with numbers and punctuation between.</summary>
    internal static string MixedText(Random random, int words)
    {
        StringBuilder text = new();
        PageCorpus corpus = Corpora[random.Next(Corpora.Count)];
        for (int i = 0; i < words; i++)
        {
            if (random.Next(4) == 0)
            {
                corpus = Corpora[random.Next(Corpora.Count)];
            }

            string alphabet = corpus.Alphabet;
            _ = text.Append(random.Next(8) == 0 ? random.Next(0, 100000).ToString(System.Globalization.CultureInfo.InvariantCulture) : Word(random, alphabet));
            if (random.Next(5) == 0)
            {
                _ = text.Append(Punctuation[random.Next(Punctuation.Length)]);
            }

            _ = text.Append(' ');
        }

        return text.ToString();
    }

    private const double Margin = 20;

    private static PdfDocument RandomDocument(int seed, double width, double height)
    {
        Random random = new(seed);
        PdfDocument document = new(DocumentTestKit.Options(o =>
        {
            o.PageSize = new PageSize(Length.Pt(width), Length.Pt(height));
            o.Margins = Margins.Pt(20, 20, 20, 20);
            o.Compress = true;
            o.Direction = (TextDirection)random.Next(3);
        }));
        int paragraphs = random.Next(3, 7);
        for (int i = 0; i < paragraphs; i++)
        {
            string text = MixedText(random, random.Next(3, 30));
            document.Add(new Paragraph(text)
            {
                Direction = random.Next(3) == 0 ? (TextDirection)random.Next(3) : null,
                Alignment = (TextAlignment)random.Next(3),
                Style = new TextStyle { Size = Length.Pt(random.Next(6, 18)), Weight = random.Next(4) == 0 ? 700 : null, Italic = random.Next(5) == 0 ? true : null },
            });
        }

        if (random.Next(2) == 0)
        {
            document.Add(new Table(
                [new TableColumn(), new TableColumn(), new TableColumn()],
                new TableRow(MixedText(random, 1), MixedText(random, 1), MixedText(random, 1)),
                Enumerable.Range(0, random.Next(2, 12)).Select(_ => new TableRow(MixedText(random, random.Next(1, 6)), MixedText(random, random.Next(1, 6)), MixedText(random, random.Next(1, 6))))));
        }

        return document;
    }

    [Fact]
    public void Random_mixed_script_text_never_throws_and_never_draws_outside_the_page()
    {
        int drawn = 0;
        for (int seed = 1; seed <= Seeds(60); seed++)
        {
            double width = seed % 3 == 0 ? 200 : seed % 3 == 1 ? 595 : 320;
            double height = seed % 2 == 0 ? 842 : 300;
            byte[] pdf;
            try
            {
                pdf = DocumentTestKit.Save(RandomDocument(seed, width, height));
            }
            catch (Exception exception)
            {
                throw new Xunit.Sdk.XunitException($"seed {seed} ({width} x {height}) threw {exception.GetType().Name}: {exception.Message}");
            }

            using UglyToad.PdfPig.PdfDocument read = UglyToad.PdfPig.PdfDocument.Open(pdf);
            foreach (UglyToad.PdfPig.Content.Page page in read.GetPages())
            {
                Assert.Equal(width, page.Width, 3);
                Assert.Equal(height, page.Height, 3);
                foreach (UglyToad.PdfPig.Content.Letter letter in page.Letters)
                {
                    drawn++;
                    UglyToad.PdfPig.Core.PdfRectangle box = letter.BoundingBox;
                    Assert.True(
                        box.Left >= -0.01 && box.Right <= width + 0.01 && box.Bottom >= -0.01 && box.Top <= height + 0.01
                        && letter.StartBaseLine.X >= -0.01 && letter.EndBaseLine.X <= width + 0.01,
                        $"seed {seed}, page {page.Number}: '{letter.Value}' at ({box.Left:0.##}, {box.Bottom:0.##})-({box.Right:0.##}, {box.Top:0.##}) is outside {width} x {height}");
                    // The text sits in the margins' box: a glyph may stand out of it by the descent of its line or a mark's overhang (half an em), not more, so a mark pushed
                    // an em below its base on the last line of a page is caught here although the page itself still contains it.
                    double overhang = 0.5 * letter.PointSize;
                    Assert.True(
                        box.Left >= Margin - overhang && box.Right <= width - Margin + overhang && box.Bottom >= Margin - overhang && box.Top <= height - Margin + overhang,
                        $"seed {seed}, page {page.Number}: '{letter.Value}' at ({box.Left:0.##}, {box.Bottom:0.##})-({box.Right:0.##}, {box.Top:0.##}) is more than {overhang:0.##} outside the margins of {Margin}");
                }
            }
        }

        Assert.True(drawn > 5000, $"only {drawn} glyphs were checked");
    }

    [Fact]
    public void Random_documents_are_the_same_bytes_twice()
    {
        for (int seed = 100; seed < 100 + Seeds(12); seed++)
        {
            byte[] first = DocumentTestKit.Save(RandomDocument(seed, 420, 400));
            byte[] second = DocumentTestKit.Save(RandomDocument(seed, 420, 400));

            Assert.True(first.AsSpan().SequenceEqual(second), $"seed {seed}: two runs differ");
        }
    }

    // ---- table pagination

    /// <summary>The number of seeds; <c>PDFWRIGHT_FUZZ_SEEDS</c> raises it for a longer run by hand (the default keeps the test at a few seconds).</summary>
    private static int Seeds(int normal) =>
        int.TryParse(Environment.GetEnvironmentVariable("PDFWRIGHT_FUZZ_SEEDS"), out int more) ? Math.Max(normal, more) : normal;

    private static readonly Regex Marker = new(@"[hr]\d+c\d+w\d+", RegexOptions.Compiled);

    /// <summary>A table of rows whose words are numbered r{row}c{column}w{n}, so a reader can say which row a word belongs to and how often it was drawn.</summary>
    private static (PdfDocument Document, bool[] Tall, int Columns, string[] Pages, int[][] Words) RandomTable(int seed, out byte[] pdf)
    {
        Random random = new(seed);
        int columns = random.Next(2, 5);
        int rowCount = random.Next(30, 90);
        bool[] tall = new bool[rowCount];
        int[][] wordCounts = new int[rowCount][];
        for (int r = 0; r < rowCount; r++)
        {
            tall[r] = r > 3 && random.Next(40) == 0;
        }

        string Cell(string prefix, int row, int column, int words) =>
            string.Join(' ', Enumerable.Range(0, words).Select(w => $"{prefix}{row}c{column}w{w}"));

        TableRow Row(int row)
        {
            TableCell[] cells = new TableCell[columns];
            wordCounts[row] = new int[columns];
            for (int c = 0; c < columns; c++)
            {
                // A tall row has far more text than a page of 300 points holds in a column of this width; the others stay far below it.
                int words = tall[row] ? (c == 0 ? 90 : 3) : random.Next(1, 7);
                wordCounts[row][c] = words;
                cells[c] = new TableCell(Cell("r", row, c, words));
            }

            return new TableRow(cells);
        }

        TableRow header = new(Enumerable.Range(0, columns).Select(c => new TableCell($"h0c{c}w0")).ToArray());
        List<TableRow> rows = [.. Enumerable.Range(0, rowCount).Select(Row)];
        PdfDocument document = new(DocumentTestKit.Options(o =>
        {
            o.PageSize = new PageSize(Length.Pt(300), Length.Pt(300));
            o.Margins = Margins.Pt(20, 20, 20, 20);
            o.Compress = true;
        }));
        document.Add(new Paragraph("Intro paragraph before the table."));
        document.Add(new Table(Enumerable.Range(0, columns).Select(_ => new TableColumn()), header, rows));
        pdf = DocumentTestKit.Save(document);
        return (document, tall, columns, DocumentTestKit.PageTexts(pdf), wordCounts);
    }

    [Fact]
    public void A_paginated_table_keeps_every_row_once_whole_and_its_header_on_every_page()
    {
        int splitRows = 0;
        for (int seed = 1; seed <= Seeds(12); seed++)
        {
            (_, bool[] tall, int columns, string[] pages, int[][] words) = RandomTable(seed, out byte[] pdf);
            Assert.True(pages.Length >= 3, $"seed {seed}: {pages.Length} pages");

            // For each marker, the pages it was drawn on.
            Dictionary<string, List<int>> where = [];
            for (int p = 0; p < pages.Length; p++)
            {
                foreach (Match match in Marker.Matches(pages[p].Replace('\n', ' ')))
                {
                    if (!where.TryGetValue(match.Value, out List<int>? list))
                    {
                        where[match.Value] = list = [];
                    }

                    list.Add(p);
                }
            }

            // Every word of every cell is drawn exactly once (a row that is cut between lines must lose nothing and repeat nothing).
            for (int r = 0; r < tall.Length; r++)
            {
                for (int c = 0; c < columns; c++)
                {
                    for (int w = 0; w < words[r][c]; w++)
                    {
                        string word = $"r{r}c{c}w{w}";
                        int times = where.TryGetValue(word, out List<int>? seen) ? seen.Count : 0;
                        Assert.True(times == 1, $"seed {seed}: {word} was drawn {times} times");
                    }
                }
            }

            foreach ((string marker, List<int> list) in where.Where(w => w.Key[0] == 'r'))
            {
                Assert.True(list.Count == 1, $"seed {seed}: {marker} was drawn {list.Count} times");
            }

            // A row that fits a page is whole on one page; a row taller than a page is cut between lines, so it may be on several.
            for (int r = 0; r < tall.Length; r++)
            {
                int[] pagesOfRow = [.. where.Where(w => w.Key.StartsWith($"r{r}c", StringComparison.Ordinal)).SelectMany(w => w.Value).Distinct().Order()];
                if (!tall[r])
                {
                    Assert.True(pagesOfRow.Length == 1, $"seed {seed}: row {r} is on pages {string.Join(',', pagesOfRow.Select(p => p + 1))}");
                }
                else if (pagesOfRow.Length > 1)
                {
                    splitRows++;
                }
            }

            // Rows come in order, the first word of each on the same page as the one before it or a later one.
            int previous = 0;
            for (int r = 0; r < tall.Length; r++)
            {
                int page = where[$"r{r}c0w0"][0];
                Assert.True(page >= previous, $"seed {seed}: row {r} is on page {page + 1}, before the row above it");
                previous = page;
            }

            // The header is drawn on every page that holds rows of the table, once.
            int tableFirst = where["r0c0w0"][0];
            int tableLast = where.Where(w => w.Key[0] == 'r').SelectMany(w => w.Value).Max();
            for (int p = tableFirst; p <= tableLast; p++)
            {
                for (int c = 0; c < columns; c++)
                {
                    Assert.True(where.TryGetValue($"h0c{c}w0", out List<int>? list) && list.Count(x => x == p) == 1, $"seed {seed}: the header cell {c} is not on page {p + 1} exactly once");
                }
            }

            Assert.Equal((tableLast - tableFirst + 1) * columns, where.Where(w => w.Key[0] == 'h').Sum(w => w.Value.Count));
        }

        // The rows that are taller than a page really were cut (otherwise the test would not look at what it says it does).
        Assert.True(splitRows >= 3, $"{splitRows} rows were split");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Qpdf_accepts_a_paginated_table(int seed)
    {
        _ = RandomTable(seed, out byte[] pdf);

        DocumentTestKit.AssertValid(pdf, $"property-table-{seed}");
    }
}
