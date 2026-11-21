using System.Text;
using Pdfwright.Tests.Document;

namespace Pdfwright.Tests.Conformance;

/// <summary>
/// The page corpus of slice S6a (<c>Corpus/pages/*.txt</c>): for each script group, a short page and a long page with a table, built
/// through the public API only, together with the strings the document was given in the order they were given.
/// </summary>
internal sealed class PageCorpus
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    internal sealed record Item(string Page, string Kind, string? Language, string[] Cells);

    /// <summary>A built document, the strings it holds in flow order and the header cells that a later page of the table repeats.</summary>
    internal sealed record Built(PdfDocument Document, List<string> Expected, string[] Header, bool HasTable);

    internal string Group { get; private init; } = string.Empty;

    internal string Family { get; private init; } = string.Empty;

    internal TextDirection Direction { get; private init; }

    internal int Rows { get; private init; }

    internal List<Item> Items { get; } = [];

    internal static IReadOnlyList<PageCorpus> LoadAll()
    {
        string directory = Path.Combine(TestPaths.CorpusDirectory, "pages");
        return [.. Directory.GetFiles(directory, "*.txt").Order(StringComparer.Ordinal).Select(Load)];
    }

    internal static PageCorpus Load(string path)
    {
        string content = StrictUtf8.GetString(File.ReadAllBytes(path));
        Assert.False(content.StartsWith('﻿'), $"{Path.GetFileName(path)} starts with a byte-order mark");
        string name = Path.GetFileName(path);
        Assert.True(content.IsNormalized(NormalizationForm.FormC), $"{name} is not in NFC");

        string group = string.Empty;
        string family = string.Empty;
        TextDirection direction = TextDirection.Auto;
        int rows = 0;
        string? language = null;
        List<Item> items = [];
        string[] lines = content.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith('@'))
            {
                int space = line.IndexOf(' ');
                string key = space < 0 ? line : line[..space];
                string value = space < 0 ? string.Empty : line[(space + 1)..];
                switch (key)
                {
                    case "@group": group = value; break;
                    case "@family": family = value; break;
                    case "@direction": direction = value switch { "rtl" => TextDirection.RightToLeft, "ltr" => TextDirection.LeftToRight, "auto" => TextDirection.Auto, _ => throw new InvalidDataException($"{name}:{i + 1} direction") }; break;
                    case "@rows": rows = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture); break;
                    case "@language": language = value == "-" ? null : value; break;
                    default: throw new InvalidDataException($"{name}:{i + 1} unknown directive {key}");
                }

                continue;
            }

            string[] parts = line.Split('|');
            Assert.True(parts.Length >= 3, $"{name}:{i + 1} is not 'page|kind|text'");
            Assert.Contains(parts[0], new[] { "short", "long" });
            Assert.Contains(parts[1], new[] { "heading", "heading2", "para", "head", "row" });
            items.Add(new Item(parts[0], parts[1], language, parts[2..]));
        }

        Assert.False(string.IsNullOrEmpty(group) || string.IsNullOrEmpty(family), $"{name} has no @group or @family");
        PageCorpus corpus = new() { Group = group, Family = family, Direction = direction, Rows = rows };
        corpus.Items.AddRange(items);
        return corpus;
    }

    /// <summary>The distinct characters of every text of the file that are not white space: the alphabet of the property tests.</summary>
    internal string Alphabet => string.Concat(Items.SelectMany(i => i.Cells).SelectMany(t => t).Where(c => !char.IsWhiteSpace(c)).Distinct().Order());

    private static TextStyle? Style(string? language) => language is null ? null : new TextStyle { Language = language };

    /// <summary>Builds the short page or the long page. <paramref name="change"/> may set options (compression, determinism, page size).</summary>
    internal Built Build(bool isLong, Action<DocumentTestKit.OptionsBuilder>? change = null)
    {
        string page = isLong ? "long" : "short";
        List<Item> mine = [.. Items.Where(i => i.Page == page)];
        string? first = mine.FirstOrDefault()?.Language;
        PdfDocument document = new(DocumentTestKit.Options(o =>
        {
            o.Direction = Direction;
            o.Language = first;
            o.Title = $"{Group} {page} page";
            o.DefaultStyle = new TextStyle { Family = Family, Size = Length.Pt(10), Language = first };
            o.Compress = true;
            o.Deterministic = true;
            o.Creation = new DateTimeOffset(2026, 11, 21, 9, 30, 0, TimeSpan.Zero);
            change?.Invoke(o);
        }));

        List<string> expected = [];
        string[] header = [];
        bool hasTable = false;
        foreach (Item item in mine.Where(i => i.Kind is "heading" or "heading2" or "para"))
        {
            string text = item.Cells[0];
            expected.Add(text);
            _ = item.Kind switch
            {
                "para" => document.Add(new Paragraph(text) { Style = Style(item.Language) }),
                _ => document.Add(new Heading(item.Kind == "heading" ? 1 : 2, [new TextRun(text, Style(item.Language))])),
            };
        }

        Item? head = mine.FirstOrDefault(i => i.Kind == "head");
        if (head is not null)
        {
            hasTable = true;
            header = head.Cells;
            List<Item> rows = [.. mine.Where(i => i.Kind == "row")];
            int count = Math.Max(Rows, rows.Count);
            TableRow Row(Item item) => new(item.Cells.Select(c => new TableCell(c) { Style = Style(item.Language) }).ToArray());
            TableRow headRow = new(head.Cells.Select(c => new TableCell(c) { Style = Style(head.Language) }).ToArray());
            expected.AddRange(head.Cells);
            for (int r = 0; r < count; r++)
            {
                expected.AddRange(rows[r % rows.Count].Cells);
            }

            document.Add(new Table(
                [.. head.Cells.Select(_ => new TableColumn())],
                headRow,
                Enumerable.Range(0, count).Select(r => Row(rows[r % rows.Count])))
            {
                Borders = Borders.Horizontal,
                HeaderFill = Color.Gray(0.9),
                SpaceBefore = Length.Pt(8),
            });
        }

        return new Built(document, expected, header, hasTable);
    }
}
