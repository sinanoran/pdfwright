using System.Text.RegularExpressions;

namespace Pdfwright.DocumentationTests;

/// <summary>
/// Keeps the code blocks of the documentation and the snippets that are compiled and run (<see cref="DocumentationSnippetTests"/>) the same text.
/// <list type="bullet">
/// <item>Every code fence of README.md and of every docs/*.md file (any indentation, whatever its info string: <c>csharp</c>, <c>cs</c>, <c>c#</c> or none) has the line
/// <c>&lt;!-- snippet: name --&gt;</c> directly above it. A block that is not C# that runs (a shell command, an output, a diagram) says <c>&lt;!-- snippet: none --&gt;</c> instead; a block
/// tagged as C# may say that only in a file that is a design note (<see cref="DesignNotes"/>).</item>
/// <item>The snippet of that name in the test file has to equal the block line for line, after the block's leading <c>using</c> lines are set aside and the snippet's indent is removed.</item>
/// <item>The leading <c>using</c> lines of the block and of the snippet (the lines <c>// using X;</c> at the top of it, because a using directive cannot stand inside a method) are the
/// same set, and the namespaces the snippet declares are ones the test file can see.</item>
/// <item>A line of the snippet may differ from its line in the document only where <see cref="Substitutions"/> lists that exact pair, with its reason.</item>
/// </list>
/// A block without a marker, a marker without a snippet, a snippet that no document shows and a pair that no snippet uses are all failures, so a change to a snippet or to a
/// document cannot pass until the other follows.
/// </summary>
public partial class DocumentationSyncTests
{
    private static readonly string Root = Pdfwright.Tests.TestPaths.RepositoryRoot;

    /// <summary>A snippet line that is not the document's line, and the line the document shows for it. Nothing else may differ.</summary>
    private sealed record Substitution(string CodeLine, string DocLine, string Reason);

    private static readonly Substitution[] Substitutions =
    [
        new("string fontDirectory = FontDirectory;", "string fontDirectory = \"fonts\"; // the folder that holds the .ttf files", "the test has its own font folder; a reader writes the folder that holds theirs"),
        new("using Stream output = new MemoryStream();", "using FileStream output = File.Create(\"report.pdf\");", "the test saves to memory; a reader saves to a file"),
    ];

    /// <summary>Documents whose C# blocks are sketches of a design and not code that runs, so they may say <c>snippet: none</c>.</summary>
    private static readonly string[] DesignNotes = ["docs/plan-0.1.md"];

    private sealed record CodeBlock(string File, int Line, string Info, string? Marker, string Code);

    private sealed record Snippet(string Name, List<string> Usings, string Code);

    private static IEnumerable<string> DocumentFiles()
    {
        yield return Path.Combine(Root, "README.md");
        foreach (string file in Directory.GetFiles(Path.Combine(Root, "docs"), "*.md").OrderBy(f => f, StringComparer.Ordinal))
        {
            yield return file;
        }
    }

    private static string SnippetSource() => File.ReadAllText(Path.Combine(Root, "tests", "Pdfwright.Tests", "Documentation", "DocumentationSnippetTests.cs")).Replace("\r\n", "\n", StringComparison.Ordinal);

    private static List<CodeBlock> CodeBlocks() => [.. DocumentFiles().SelectMany(f => ParseBlocks(Path.GetRelativePath(Root, f).Replace('\\', '/'), File.ReadAllText(f)))];

    /// <summary>Finds every code fence (three or more backticks or tildes, at any indent), the marker line above it and the lines it holds.</summary>
    private static List<CodeBlock> ParseBlocks(string file, string text)
    {
        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        List<CodeBlock> blocks = [];
        for (int i = 0; i < lines.Length; i++)
        {
            Match open = FencePattern().Match(lines[i]);
            if (!open.Success)
            {
                continue;
            }

            string fence = open.Groups[2].Value;
            int indent = open.Groups[1].Value.Length;
            Regex close = new(@"^[ \t>]*" + Regex.Escape(fence[0].ToString()) + "{" + fence.Length + @",}\s*$");
            Match marker = i > 0 ? MarkerPattern().Match(lines[i - 1]) : Match.Empty;
            int end = i + 1;
            while (end < lines.Length && !close.IsMatch(lines[end]))
            {
                end++;
            }

            IEnumerable<string> body = lines[(i + 1)..Math.Min(end, lines.Length)].Select(l => l.Length >= indent && l[..indent].Trim().Length == 0 ? l[indent..] : l.TrimStart());
            blocks.Add(new CodeBlock(file, i + 1, open.Groups[3].Value.Trim(), marker.Success ? marker.Groups[1].Value : null, string.Join('\n', body)));
            i = end;
        }

        return blocks;
    }

    private static bool IsCSharp(string info) => info.Split(' ', 2)[0].ToLowerInvariant() is "cs" or "c#" or "csharp";

    private static Dictionary<string, Snippet> ParseSnippets(string source)
    {
        Dictionary<string, Snippet> snippets = [];
        foreach (Match m in SnippetPattern().Matches(source))
        {
            List<string> lines = [.. m.Groups[2].Value.Split('\n')];
            int indent = lines.Where(l => l.Trim().Length > 0).Min(l => l.Length - l.TrimStart().Length);
            lines = [.. lines.Select(l => l.Length >= indent ? l[indent..] : l.TrimStart())];

            List<string> usings = [];
            while (lines.Count > 0 && UsingCommentPattern().Match(lines[0]) is { Success: true } u)
            {
                usings.Add(u.Groups[1].Value);
                lines.RemoveAt(0);
            }

            Assert.True(snippets.TryAdd(m.Groups[1].Value, new Snippet(m.Groups[1].Value, usings, Normalize(string.Join('\n', lines)))), "Two snippets are called " + m.Groups[1].Value);
        }

        return snippets;
    }

    private static string Normalize(string code) => string.Join('\n', code.Split('\n').Select(l => l.TrimEnd())).Trim('\n');

    /// <summary>Splits the leading using directives (and the blank lines between them) from the rest of a block.</summary>
    private static (List<string> Usings, string Remainder) SplitUsings(string code)
    {
        List<string> lines = [.. code.Split('\n')];
        List<string> usings = [];
        while (lines.Count > 0 && (UsingPattern().IsMatch(lines[0].Trim()) || lines[0].Trim().Length == 0))
        {
            if (lines[0].Trim().Length > 0)
            {
                usings.Add(lines[0].Trim());
            }

            lines.RemoveAt(0);
        }

        return (usings, string.Join('\n', lines));
    }

    /// <summary>The problems of the markers: a fence with no marker, and a C# block that opts out in a document that is not a design note.</summary>
    private static List<string> MarkerProblems(IEnumerable<CodeBlock> blocks)
    {
        List<string> problems = [];
        foreach (CodeBlock block in blocks)
        {
            if (block.Marker is null)
            {
                problems.Add($"{block.File}:{block.Line}: a code block has no <!-- snippet: name --> line directly above it (<!-- snippet: none --> for a block that is not code that runs)");
            }
            else if (block.Marker == "none" && IsCSharp(block.Info) && !DesignNotes.Contains(block.File))
            {
                problems.Add($"{block.File}:{block.Line}: a C# block says snippet: none, which only a design note may");
            }
        }

        return problems;
    }

    /// <summary>The problems of a document against its snippets: a missing or a doubled snippet, different using lines, a different line, a snippet no document shows.</summary>
    private static List<string> SyncProblems(IEnumerable<CodeBlock> blocks, IReadOnlyDictionary<string, Snippet> snippets, IReadOnlyList<Substitution> allowed, HashSet<Substitution> used)
    {
        List<string> problems = [];
        HashSet<string> shown = [];

        foreach (CodeBlock block in blocks.Where(b => b.Marker is not null and not "none"))
        {
            string name = block.Marker!;
            if (!snippets.TryGetValue(name, out Snippet? snippet))
            {
                problems.Add($"{block.File}:{block.Line}: no snippet called {name}");
                continue;
            }

            if (!shown.Add(name))
            {
                problems.Add($"{block.File}:{block.Line}: the snippet {name} is shown twice");
            }

            (List<string> docUsings, string docRest) = SplitUsings(block.Code);
            foreach (string missing in docUsings.Except(snippet.Usings))
            {
                problems.Add($"{block.File}:{block.Line}: {name}: the document has `{missing}` and the snippet does not declare it (// {missing} at the top of the snippet)");
            }

            foreach (string extra in snippet.Usings.Except(docUsings))
            {
                problems.Add($"{block.File}:{block.Line}: {name}: the snippet declares `{extra}` and the document does not have it");
            }

            string[] expectedLines = [.. snippet.Code.Split('\n').Select(line =>
            {
                Substitution? pair = allowed.FirstOrDefault(s => s.CodeLine == line);
                if (pair is null)
                {
                    return line;
                }

                used.Add(pair);
                return pair.DocLine;
            })];
            string[] a = Normalize(docRest).Split('\n');
            if (!a.SequenceEqual(expectedLines))
            {
                int line = Enumerable.Range(0, Math.Max(a.Length, expectedLines.Length)).First(n => n >= a.Length || n >= expectedLines.Length || a[n] != expectedLines[n]);
                problems.Add($"{block.File}:{block.Line}: {name} differs from the snippet at its line {line + 1}: document `{(line < a.Length ? a[line] : "(ends)")}`, snippet `{(line < expectedLines.Length ? expectedLines[line] : "(ends)")}`");
            }
        }

        foreach (string name in snippets.Keys.Except(shown))
        {
            problems.Add($"the snippet {name} is in no document");
        }

        return problems;
    }

    [Fact]
    public void Every_code_fence_of_the_documentation_has_a_marker()
    {
        List<CodeBlock> blocks = CodeBlocks();

        Assert.True(blocks.Count >= 10, "The documentation has code blocks.");
        Assert.True(blocks.Any(b => b.Marker == "none"), "The shell blocks say snippet: none.");
        List<string> problems = MarkerProblems(blocks);
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void Every_code_block_is_its_snippet_character_for_character()
    {
        HashSet<Substitution> used = [];
        List<string> problems = SyncProblems(CodeBlocks(), ParseSnippets(SnippetSource()), Substitutions, used);

        foreach (Substitution stale in Substitutions.Except(used))
        {
            problems.Add($"the pair for `{stale.CodeLine}` is used by no snippet");
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void The_namespaces_a_snippet_declares_are_ones_the_test_file_can_see()
    {
        string source = SnippetSource();
        HashSet<string> visible = [.. UsingLinePattern().Matches(source).Select(m => m.Groups[1].Value), "Pdfwright", "System", "System.IO", "System.Linq", "System.Collections.Generic", "System.Threading", "System.Threading.Tasks", "System.Net.Http"];

        string[] unseen = [.. ParseSnippets(source).Values.SelectMany(s => s.Usings.Select(u => (s.Name, Namespace: u["using ".Length..^1]))).Where(x => !visible.Contains(x.Namespace)).Select(x => $"{x.Name}: {x.Namespace}")];

        Assert.True(unseen.Length == 0, "A snippet declares a using that the test file does not have: " + string.Join(", ", unseen));
    }

    [Theory]
    [InlineData("```cs")]
    [InlineData("```c#")]
    [InlineData("```csharp")]
    [InlineData("```CSharp title=x")]
    [InlineData("```")]
    [InlineData("   ```csharp")]
    [InlineData("> ```")]
    [InlineData("~~~csharp")]
    public void A_code_block_appended_to_a_document_without_a_marker_is_a_problem(string fence)
    {
        string closing = fence.TrimStart(' ', '-')[..3] == "~~~" ? "~~~" : "```";
        string text = "# Title\n\nText.\n\n" + fence + "\nint x = 1;\n" + closing + "\n";

        List<CodeBlock> blocks = ParseBlocks("docs/new.md", text);

        CodeBlock block = Assert.Single(blocks);
        Assert.Equal("int x = 1;", block.Code);
        Assert.Single(MarkerProblems(blocks));
    }

    [Fact]
    public void An_opt_out_is_for_a_block_that_is_not_csharp_and_a_design_note_may_keep_its_sketches()
    {
        Assert.Empty(MarkerProblems(ParseBlocks("README.md", "<!-- snippet: none -->\n```bash\ndotnet build\n```\n")));
        Assert.Empty(MarkerProblems(ParseBlocks("README.md", "<!-- snippet: none -->\n```\nsome output\n```\n")));
        Assert.Single(MarkerProblems(ParseBlocks("README.md", "<!-- snippet: none -->\n```csharp\nint x = 1;\n```\n")));
        Assert.Single(MarkerProblems(ParseBlocks("docs/guide.md", "<!-- snippet: none -->\n```c#\nint x = 1;\n```\n")));
        Assert.Empty(MarkerProblems(ParseBlocks("docs/plan-0.1.md", "<!-- snippet: none -->\n```csharp\nint x = 1;\n```\n")));
    }

    [Fact]
    public void A_using_line_that_one_side_has_and_the_other_has_not_is_a_problem_in_both_directions()
    {
        Dictionary<string, Snippet> snippets = new() { ["s"] = new Snippet("s", ["using System.Globalization;"], "int x = 1;") };

        List<string> deleted = SyncProblems(ParseBlocks("README.md", "<!-- snippet: s -->\n```csharp\nint x = 1;\n```\n"), snippets, [], []);
        List<string> added = SyncProblems(ParseBlocks("README.md", "<!-- snippet: s -->\n```csharp\nusing System.Globalization;\nusing System.Text;\n\nint x = 1;\n```\n"), snippets, [], []);
        List<string> same = SyncProblems(ParseBlocks("README.md", "<!-- snippet: s -->\n```csharp\nusing System.Globalization;\n\nint x = 1;\n```\n"), snippets, [], []);

        Assert.Contains("the snippet declares `using System.Globalization;` and the document does not have it", Assert.Single(deleted));
        Assert.Contains("the document has `using System.Text;` and the snippet does not declare it", Assert.Single(added));
        Assert.Empty(same);
    }

    [Fact]
    public void A_line_differs_from_the_snippet_only_where_a_listed_pair_says_so()
    {
        Dictionary<string, Snippet> snippets = new() { ["s"] = new Snippet("s", [], "Fill = Color.Gray(0.9),\nstring d = Dir;") };
        Substitution pair = new("string d = Dir;", "string d = \"fonts\";", "the test has its own folder");

        List<CodeBlock> shaded = ParseBlocks("README.md", "<!-- snippet: s -->\n```csharp\nHeaderShading = Color.Gray(0.9),\nstring d = \"fonts\";\n```\n");
        HashSet<Substitution> used = [];

        Assert.Contains("differs from the snippet at its line 1", Assert.Single(SyncProblems(shaded, snippets, [pair], used)));
        Assert.Contains(pair, used);
        Assert.Single(SyncProblems(ParseBlocks("README.md", "<!-- snippet: s -->\n```csharp\nFill = Color.Gray(0.9),\nstring d = \"fonts\";\n```\n"), snippets, [], []));
        Assert.Empty(SyncProblems(ParseBlocks("README.md", "<!-- snippet: s -->\n```csharp\nFill = Color.Gray(0.9),\nstring d = \"fonts\";\n```\n"), snippets, [pair], []));
    }

    [Fact]
    public void A_using_declaration_is_not_a_using_directive()
    {
        (List<string> usings, string rest) = SplitUsings("using X;\nusing Y.Z;\n\nusing var a = b;\nusing Stream s = new();");

        Assert.Equal(["using X;", "using Y.Z;"], usings);
        Assert.Equal("using var a = b;\nusing Stream s = new();", rest);
        Assert.Equal("a;\nb;", Normalize("a;  \nb;\n\n"));
    }

    [GeneratedRegex(@"^([ \t>]*)(`{3,}|~{3,})(.*)$")]
    private static partial Regex FencePattern();

    [GeneratedRegex(@"^\s*<!-- snippet: ([a-z0-9-]+) -->\s*$")]
    private static partial Regex MarkerPattern();

    [GeneratedRegex(@"^[ ]*// <snippet ([a-z0-9-]+)>\n(.*?)\n[ ]*// </snippet>", RegexOptions.Multiline | RegexOptions.Singleline)]
    private static partial Regex SnippetPattern();

    [GeneratedRegex(@"^// (using [A-Za-z_][A-Za-z0-9_.]*;)$")]
    private static partial Regex UsingCommentPattern();

    [GeneratedRegex(@"^using [A-Za-z_][A-Za-z0-9_.]*;$")]
    private static partial Regex UsingPattern();

    [GeneratedRegex(@"^using ([A-Za-z_][A-Za-z0-9_.]*);", RegexOptions.Multiline)]
    private static partial Regex UsingLinePattern();
}
