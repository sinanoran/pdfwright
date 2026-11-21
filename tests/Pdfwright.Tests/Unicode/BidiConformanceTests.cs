using System.Globalization;
using Pdfwright.Unicode;

namespace Pdfwright.Tests.Unicode;

/// <summary>
/// The Unicode Consortium's own conformance files for UAX #9, run in full: every line of <c>BidiTest.txt</c> (classes, at every
/// paragraph level it names) and <c>BidiCharacterTest.txt</c> (real characters, brackets and isolates). The pass rate is 100 percent
/// of the rows, with no row skipped; one that fails is named.
/// </summary>
public class BidiConformanceTests
{
    /// <summary>A character of each class, to turn the class sequences of BidiTest.txt into text.</summary>
    private static readonly Dictionary<string, int> Representative = new()
    {
        ["L"] = 0x0061,
        ["R"] = 0x05D0,
        ["AL"] = 0x0627,
        ["EN"] = 0x0030,
        ["ES"] = 0x002B,
        ["ET"] = 0x0024,
        ["AN"] = 0x0660,
        ["CS"] = 0x002C,
        ["NSM"] = 0x0300,
        ["BN"] = 0x00AD,
        ["B"] = 0x2029,
        ["S"] = 0x0009,
        ["WS"] = 0x0020,
        ["ON"] = 0x0021,
        ["LRE"] = 0x202A,
        ["LRO"] = 0x202D,
        ["RLE"] = 0x202B,
        ["RLO"] = 0x202E,
        ["PDF"] = 0x202C,
        ["LRI"] = 0x2066,
        ["RLI"] = 0x2067,
        ["FSI"] = 0x2068,
        ["PDI"] = 0x2069,
    };

    private static string DataFile(string name) => Path.Combine(TestPaths.RepositoryRoot, "data", "unicode", "18.0.0", name);

    /// <summary>The levels of the characters that count (not those rule X9 removes) and their visual order, as the files state them.</summary>
    private static (string Levels, string Order) Describe(BidiResult result)
    {
        List<int> kept = [.. Enumerable.Range(0, result.Levels.Length).Where(i => !UnicodeBidi.IsRemovedByX9(result.Classes[i]))];
        string levels = string.Join(' ', Enumerable.Range(0, result.Levels.Length)
            .Select(i => UnicodeBidi.IsRemovedByX9(result.Classes[i]) ? "x" : result.Levels[i].ToString(CultureInfo.InvariantCulture)));

        // Reorder only what counts, as the files do: the removed characters have no place in the visual order.
        byte[] keptLevels = [.. kept.Select(i => result.Levels[i])];
        string order = string.Join(' ', UnicodeBidi.VisualOrder(keptLevels, 0, keptLevels.Length).Select(i => kept[i].ToString(CultureInfo.InvariantCulture)));
        return (levels, order);
    }

    [Fact]
    public void BidiTest_txt_passes_on_every_row()
    {
        int rows = 0;
        List<string> failures = [];
        string expectedLevels = string.Empty;
        string expectedOrder = string.Empty;
        foreach (string raw in File.ReadLines(DataFile("BidiTest.txt")))
        {
            if (raw.Length == 0 || raw[0] == '#')
            {
                continue;
            }

            if (raw.StartsWith("@Levels:", StringComparison.Ordinal))
            {
                expectedLevels = raw["@Levels:".Length..].Trim();
                continue;
            }

            if (raw.StartsWith("@Reorder:", StringComparison.Ordinal))
            {
                expectedOrder = raw["@Reorder:".Length..].Trim();
                continue;
            }

            string[] parts = raw.Split(';');
            string[] classes = parts[0].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            int bits = int.Parse(parts[1].Trim(), CultureInfo.InvariantCulture);
            int[] text = [.. classes.Select(c => Representative[c])];
            foreach ((int bit, int level) in new[] { (1, UnicodeBidi.AutoLevel), (2, 0), (4, 1) })
            {
                if ((bits & bit) == 0)
                {
                    continue;
                }

                rows++;
                (string levels, string order) = Describe(UnicodeBidi.Resolve(text, level));
                if (levels != expectedLevels || order != expectedOrder)
                {
                    failures.Add($"{parts[0].Trim()} at level {level}: levels '{levels}' (expected '{expectedLevels}'), order '{order}' (expected '{expectedOrder}')");
                }
            }
        }

        Assert.Equal(770_241, rows);
        Assert.True(failures.Count == 0, $"{failures.Count} of {rows} rows failed; the first: {string.Join(" | ", failures.Take(5))}");
    }

    [Fact]
    public void BidiCharacterTest_txt_passes_on_every_row()
    {
        int rows = 0;
        List<string> failures = [];
        foreach (string raw in File.ReadLines(DataFile("BidiCharacterTest.txt")))
        {
            if (raw.Length == 0 || raw[0] == '#')
            {
                continue;
            }

            string[] fields = raw.Split(';');
            int[] text = [.. fields[0].Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(h => int.Parse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture))];
            int direction = int.Parse(fields[1], CultureInfo.InvariantCulture);
            int level = direction == 2 ? UnicodeBidi.AutoLevel : direction;
            int expectedParagraph = int.Parse(fields[2], CultureInfo.InvariantCulture);

            rows++;
            BidiResult result = UnicodeBidi.Resolve(text, level);
            (string levels, string order) = Describe(result);
            if (result.ParagraphLevel != expectedParagraph || levels != fields[3].Trim() || order != fields[4].Trim())
            {
                failures.Add($"{fields[0].Trim()} dir {direction}: paragraph {result.ParagraphLevel} (expected {expectedParagraph}), levels '{levels}' (expected '{fields[3].Trim()}'), order '{order}' (expected '{fields[4].Trim()}')");
            }
        }

        Assert.Equal(91_707, rows);
        Assert.True(failures.Count == 0, $"{failures.Count} of {rows} rows failed; the first: {string.Join(" | ", failures.Take(5))}");
    }
}
