using System.Text.RegularExpressions;

namespace Pdfwright.DocumentationTests;

/// <summary>
/// Keeps the code blocks of the documentation and the snippets that are compiled and run (<see cref="DocumentationSnippetTests"/>) the same text. Every <c>```csharp</c> block of
/// README.md and docs/guide.md has the line <c>&lt;!-- snippet: name --&gt;</c> directly above it, and the snippet of that name in the test file has to equal the block:
/// line for line, after the block's leading <c>using</c> lines are set aside (the test file has its own) and the snippet's indent is removed. A snippet line that carries
/// <c>// docs: text</c> is compared as <c>text</c>, which is how a line that points at a test folder reads as the line a reader writes. A block without a marker, a marker without a
/// snippet and a snippet that no document shows are all failures, so a change to a snippet or to a document cannot pass until the other follows.
/// </summary>
public partial class DocumentationSyncTests
{
    private static readonly string Root = Pdfwright.Tests.TestPaths.RepositoryRoot;

    private static IEnumerable<string> DocumentFiles()
    {
        yield return Path.Combine(Root, "README.md");
        yield return Path.Combine(Root, "docs", "guide.md");
    }

    private sealed record CodeBlock(string File, int Line, string? Name, string Code);

    private static List<CodeBlock> CodeBlocks()
    {
        List<CodeBlock> blocks = [];
        foreach (string file in DocumentFiles())
        {
            string[] lines = File.ReadAllText(file).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (!lines[i].StartsWith("```csharp", StringComparison.Ordinal))
                {
                    continue;
                }

                Match marker = i > 0 ? MarkerPattern().Match(lines[i - 1]) : Match.Empty;
                int end = i + 1;
                while (end < lines.Length && !lines[end].StartsWith("```", StringComparison.Ordinal))
                {
                    end++;
                }

                blocks.Add(new CodeBlock(Path.GetRelativePath(Root, file), i + 1, marker.Success ? marker.Groups[1].Value : null, string.Join('\n', lines[(i + 1)..end])));
                i = end;
            }
        }

        return blocks;
    }

    private static Dictionary<string, string> Snippets()
    {
        string source = File.ReadAllText(Path.Combine(Root, "tests", "Pdfwright.Tests", "Documentation", "DocumentationSnippetTests.cs")).Replace("\r\n", "\n", StringComparison.Ordinal);
        Dictionary<string, string> snippets = [];
        foreach (Match m in SnippetPattern().Matches(source))
        {
            string[] lines = m.Groups[2].Value.Split('\n');
            int indent = lines.Where(l => l.Trim().Length > 0).Min(l => l.Length - l.TrimStart().Length);
            IEnumerable<string> dedented = lines.Select(l => l.Length >= indent ? l[indent..] : l.TrimStart()).Select(Substitute);
            Assert.True(snippets.TryAdd(m.Groups[1].Value, Normalize(string.Join('\n', dedented))), "Two snippets are called " + m.Groups[1].Value);
        }

        return snippets;
    }

    private static string Substitute(string line)
    {
        int at = line.IndexOf("// docs: ", StringComparison.Ordinal);
        return at < 0 ? line : line[(at + "// docs: ".Length)..];
    }

    private static string Normalize(string code) => string.Join('\n', code.Split('\n').Select(l => l.TrimEnd())).Trim('\n');

    private static string WithoutLeadingUsings(string code)
    {
        List<string> lines = [.. code.Split('\n')];
        while (lines.Count > 0 && (lines[0].StartsWith("using ", StringComparison.Ordinal) && lines[0].TrimEnd().EndsWith(';') && !lines[0].Contains('(', StringComparison.Ordinal) || lines[0].Trim().Length == 0))
        {
            lines.RemoveAt(0);
        }

        return string.Join('\n', lines);
    }

    [Fact]
    public void Every_code_block_of_the_documentation_has_a_snippet()
    {
        List<CodeBlock> blocks = CodeBlocks();

        string[] unmarked = [.. blocks.Where(b => b.Name is null).Select(b => $"{b.File}:{b.Line}")];

        Assert.True(blocks.Count >= 10, "The documentation has code blocks.");
        Assert.True(unmarked.Length == 0, "A code CodeBlock has no <!-- snippet: name --> line above it: " + string.Join(", ", unmarked));
    }

    [Fact]
    public void Every_code_block_is_its_snippet_character_for_character()
    {
        Dictionary<string, string> snippets = Snippets();
        List<string> problems = [];
        HashSet<string> shown = [];

        foreach (CodeBlock CodeBlock in CodeBlocks().Where(b => b.Name is not null))
        {
            string name = CodeBlock.Name!;
            if (!snippets.TryGetValue(name, out string? expected))
            {
                problems.Add($"{CodeBlock.File}:{CodeBlock.Line}: no snippet called {name}");
                continue;
            }

            if (!shown.Add(name))
            {
                problems.Add($"{CodeBlock.File}:{CodeBlock.Line}: the snippet {name} is shown twice");
            }

            string actual = Normalize(WithoutLeadingUsings(CodeBlock.Code));
            if (actual != expected)
            {
                string[] a = actual.Split('\n');
                string[] e = expected.Split('\n');
                int line = Enumerable.Range(0, Math.Max(a.Length, e.Length)).First(n => n >= a.Length || n >= e.Length || a[n] != e[n]);
                problems.Add($"{CodeBlock.File}:{CodeBlock.Line}: {name} differs from the snippet at its line {line + 1}: document `{(line < a.Length ? a[line] : "(ends)")}`, snippet `{(line < e.Length ? e[line] : "(ends)")}`");
            }
        }

        foreach (string name in snippets.Keys.Except(shown))
        {
            problems.Add($"the snippet {name} is in no document");
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void The_matching_rules_see_a_difference_a_using_line_and_a_substitution()
    {
        Assert.Equal("a;\nb;", Normalize(WithoutLeadingUsings("using X;\nusing Y;\n\na;\nb;  ")));
        Assert.Equal("using var x = new();", Normalize(WithoutLeadingUsings("using var x = new();")));
        Assert.Equal("string d = \"fonts\";", Substitute("string d = Test.Dir; // docs: string d = \"fonts\";"));
    }

    [GeneratedRegex(@"^<!-- snippet: ([a-z0-9-]+) -->\s*$")]
    private static partial Regex MarkerPattern();

    [GeneratedRegex(@"^[ ]*// <snippet ([a-z0-9-]+)>\n(.*?)\n[ ]*// </snippet>", RegexOptions.Multiline | RegexOptions.Singleline)]
    private static partial Regex SnippetPattern();
}
