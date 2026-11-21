using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Pdfwright.GoldenDiff;

/// <summary>
/// <c>dotnet run --project tools/Pdfwright.GoldenDiff -- expected.pdf actual.pdf [--context N] [--max-lines N]</c>.
/// Exit code 0: the files are the same bytes. 1: they differ (the differences are printed). 2: usage or an unreadable file.
/// </summary>
internal static partial class Program
{
    private sealed record PdfObject(int Number, string Dictionary, byte[]? Stream);

    private static int Context = 2;

    private static int MaxLines = 120;

    private static int Main(string[] args)
    {
        List<string> files = [];
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--context" && i + 1 < args.Length && int.TryParse(args[++i], out int context))
            {
                Context = context;
            }
            else if (args[i] == "--max-lines" && i + 1 < args.Length && int.TryParse(args[++i], out int max))
            {
                MaxLines = max;
            }
            else
            {
                files.Add(args[i]);
            }
        }

        if (files.Count != 2)
        {
            Console.Error.WriteLine("Usage: Pdfwright.GoldenDiff <expected.pdf> <actual.pdf> [--context N] [--max-lines N]");
            return 2;
        }

        for (int i = 0; i < 2; i++)
        {
            if (!File.Exists(files[i]))
            {
                Console.Error.WriteLine($"Pdfwright.GoldenDiff: the {(i == 0 ? "expected" : "actual")} file '{files[i]}' does not exist.");
                return 2;
            }
        }

        byte[] expected = File.ReadAllBytes(files[0]);
        byte[] actual = File.ReadAllBytes(files[1]);
        Console.WriteLine($"expected: {files[0]}\n  {expected.Length} bytes, SHA-256 {Hash(expected)}");
        Console.WriteLine($"actual:   {files[1]}\n  {actual.Length} bytes, SHA-256 {Hash(actual)}");
        if (expected.AsSpan().SequenceEqual(actual))
        {
            Console.WriteLine("The files are identical.");
            return 0;
        }

        SortedDictionary<int, PdfObject> a = Parse(expected);
        SortedDictionary<int, PdfObject> b = Parse(actual);
        Console.WriteLine($"objects: {a.Count} expected, {b.Count} actual");
        int changed = 0;
        foreach (int number in a.Keys.Union(b.Keys).Order())
        {
            if (!b.TryGetValue(number, out PdfObject? right))
            {
                Console.WriteLine($"\nobject {number}: only in expected ({Describe(a[number])})");
                changed++;
            }
            else if (!a.TryGetValue(number, out PdfObject? left))
            {
                Console.WriteLine($"\nobject {number}: only in actual ({Describe(right)})");
                changed++;
            }
            else if (left.Dictionary != right.Dictionary || !(left.Stream ?? []).AsSpan().SequenceEqual(right.Stream ?? []))
            {
                changed++;
                Console.WriteLine($"\nobject {number}: changed");
                DiffText("dictionary", Split(left.Dictionary), Split(right.Dictionary));
                if (left.Stream is not null || right.Stream is not null)
                {
                    DiffStream(left, right);
                }
            }
        }

        if (changed == 0)
        {
            Console.WriteLine("\nNo object differs: the difference is in the bytes between objects (header, cross-reference table or trailer).");
            int at = FirstDifference(expected, actual);
            Console.WriteLine($"First differing byte: offset {at}.");
        }
        else
        {
            Console.WriteLine($"\n{changed} object(s) differ.");
        }

        return 1;
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string Describe(PdfObject o) => o.Stream is null ? Truncate(o.Dictionary) : $"{Truncate(o.Dictionary)} + stream of {o.Stream.Length} bytes";

    private static string Truncate(string s) => s.Length > 100 ? s[..100] + "..." : s;

    private static int FirstDifference(byte[] a, byte[] b)
    {
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++)
        {
            if (a[i] != b[i])
            {
                return i;
            }
        }

        return n;
    }

    [GeneratedRegex(@"(?<=\n|^)(\d+) (\d+) obj\b")]
    private static partial Regex ObjectStart();

    [GeneratedRegex(@"/Length (\d+)(?! \d+ R)")]
    private static partial Regex DirectLength();

    private static SortedDictionary<int, PdfObject> Parse(byte[] file)
    {
        string text = Encoding.Latin1.GetString(file);
        SortedDictionary<int, PdfObject> objects = [];
        int position = 0;
        while (true)
        {
            Match start = ObjectStart().Match(text, position);
            if (!start.Success)
            {
                return objects;
            }

            int body = start.Index + start.Length;
            int end = text.IndexOf("endobj", body, StringComparison.Ordinal);
            int streamAt = text.IndexOf("stream", body, StringComparison.Ordinal);
            byte[]? stream = null;
            string dictionary;
            if (streamAt >= 0 && (end < 0 || streamAt < end) && (streamAt < 3 || string.CompareOrdinal(text, streamAt - 3, "end", 0, 3) != 0))
            {
                dictionary = text[body..streamAt].Trim();
                int data = streamAt + "stream".Length;
                if (data < text.Length && text[data] == '\r')
                {
                    data++;
                }

                if (data < text.Length && text[data] == '\n')
                {
                    data++;
                }

                Match length = DirectLength().Match(dictionary);
                int dataEnd = length.Success ? data + int.Parse(length.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : text.IndexOf("endstream", data, StringComparison.Ordinal);
                dataEnd = Math.Clamp(dataEnd, data, file.Length);
                stream = file[data..dataEnd];
                end = text.IndexOf("endobj", dataEnd, StringComparison.Ordinal);
            }
            else
            {
                dictionary = text[body..(end < 0 ? text.Length : end)].Trim();
            }

            objects[int.Parse(start.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)] = new PdfObject(int.Parse(start.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), dictionary, stream);
            if (end < 0)
            {
                return objects;
            }

            position = end + "endobj".Length;
        }
    }

    /// <summary>
    /// A dictionary as one entry per line: a line starts at each name, and a name alone on a line is a key whose value is the name that follows it
    /// (<c>/Type</c> and <c>/Font</c>), so the two are put back on one line.
    /// </summary>
    private static List<string> Split(string dictionary)
    {
        string[] pieces = Regex.Replace(dictionary, @"\s*(?=/[A-Za-z])", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        List<string> lines = [];
        for (int i = 0; i < pieces.Length; i++)
        {
            bool bare = Regex.IsMatch(pieces[i], @"^/[^\s/]+$");
            if (bare && i + 1 < pieces.Length && Regex.IsMatch(pieces[i + 1], @"^/[^\s/]+$"))
            {
                lines.Add(pieces[i] + " " + pieces[++i]);
            }
            else
            {
                lines.Add(pieces[i]);
            }
        }

        return lines;
    }

    private static void DiffStream(PdfObject left, PdfObject right)
    {
        byte[] a = Decode(left);
        byte[] b = Decode(right);
        if (IsText(a) && IsText(b))
        {
            DiffText("decoded stream", Operations(Encoding.Latin1.GetString(a)), Operations(Encoding.Latin1.GetString(b)));
            return;
        }

        int at = FirstDifference(a, b);
        Console.WriteLine($"  binary stream: {a.Length} bytes expected (SHA-256 {Hash(a)[..16]}), {b.Length} actual (SHA-256 {Hash(b)[..16]}), first difference at offset {at}");
        string Around(byte[] x) => Convert.ToHexString(x.AsSpan(Math.Max(0, at - 4), Math.Min(16, x.Length - Math.Max(0, at - 4))));
        Console.WriteLine($"    expected ...{Around(a)}\n    actual   ...{Around(b)}");
    }

    private static byte[] Decode(PdfObject o)
    {
        if (o.Stream is null)
        {
            return [];
        }

        if (!o.Dictionary.Contains("/FlateDecode", StringComparison.Ordinal))
        {
            return o.Stream;
        }

        try
        {
            using ZLibStream zlib = new(new MemoryStream(o.Stream), CompressionMode.Decompress);
            using MemoryStream result = new();
            zlib.CopyTo(result);
            return result.ToArray();
        }
        catch (InvalidDataException)
        {
            return o.Stream;
        }
    }

    private static bool IsText(byte[] bytes)
    {
        int sample = Math.Min(bytes.Length, 4096);
        int odd = 0;
        for (int i = 0; i < sample; i++)
        {
            byte c = bytes[i];
            if (c is < 32 and not (9 or 10 or 13) or > 126)
            {
                odd++;
            }
        }

        return sample > 0 && odd * 50 < sample;
    }

    /// <summary>A content stream as one operation per line: the operands and the operator that ends them, with strings, hex strings and arrays kept whole.</summary>
    private static List<string> Operations(string content)
    {
        List<string> lines = [];
        StringBuilder current = new();
        int i = 0;
        while (i < content.Length)
        {
            char c = content[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            int start = i;
            if (c == '[')
            {
                int depth = 0;
                while (i < content.Length)
                {
                    if (content[i] == '(')
                    {
                        i = SkipLiteral(content, i);
                        continue;
                    }

                    depth += content[i] == '[' ? 1 : content[i] == ']' ? -1 : 0;
                    i++;
                    if (depth == 0)
                    {
                        break;
                    }
                }
            }
            else if (c == '(')
            {
                i = SkipLiteral(content, i);
            }
            else if (c == '<' && i + 1 < content.Length && content[i + 1] != '<')
            {
                i = content.IndexOf('>', i) + 1;
                i = i <= 0 ? content.Length : i;
            }
            else
            {
                while (i < content.Length && !char.IsWhiteSpace(content[i]) && content[i] != '[' && content[i] != '(')
                {
                    i++;
                }
            }

            string token = content[start..i];
            if (current.Length > 0)
            {
                _ = current.Append(' ');
            }

            _ = current.Append(token);
            if (char.IsLetter(token[0]) || token[0] is '\'' or '"' || token is "T*")
            {
                lines.Add(current.ToString());
                _ = current.Clear();
            }
        }

        if (current.Length > 0)
        {
            lines.Add(current.ToString());
        }

        return lines;
    }

    private static int SkipLiteral(string s, int i)
    {
        int depth = 0;
        while (i < s.Length)
        {
            if (s[i] == '\\')
            {
                i += 2;
                continue;
            }

            depth += s[i] == '(' ? 1 : s[i] == ')' ? -1 : 0;
            i++;
            if (depth == 0)
            {
                break;
            }
        }

        return i;
    }

    /// <summary>A line diff by longest common subsequence, printed with a few lines of context; the output is cut at <see cref="MaxLines"/>.</summary>
    private static void DiffText(string what, List<string> a, List<string> b)
    {
        if (a.SequenceEqual(b))
        {
            return;
        }

        Console.WriteLine($"  {what}: {a.Count} lines expected, {b.Count} actual");
        List<(char Mark, string Line)> script = Script(a, b);
        bool[] show = new bool[script.Count];
        for (int i = 0; i < script.Count; i++)
        {
            if (script[i].Mark != ' ')
            {
                for (int j = Math.Max(0, i - Context); j <= Math.Min(script.Count - 1, i + Context); j++)
                {
                    show[j] = true;
                }
            }
        }

        int printed = 0;
        bool gap = false;
        for (int i = 0; i < script.Count; i++)
        {
            if (!show[i])
            {
                gap = true;
                continue;
            }

            if (printed >= MaxLines)
            {
                Console.WriteLine("    ... (more differences; raise --max-lines)");
                return;
            }

            if (gap && printed > 0)
            {
                Console.WriteLine("    ...");
            }

            gap = false;
            Console.WriteLine($"    {script[i].Mark} {Truncate(script[i].Line, 240)}");
            printed++;
        }
    }

    private static string Truncate(string s, int length) => s.Length > length ? s[..length] + "..." : s;

    private static List<(char Mark, string Line)> Script(List<string> a, List<string> b)
    {
        // Trim what the two share at the ends; the table is only built for the middle, which keeps a one-line change in a long stream cheap.
        int head = 0;
        while (head < a.Count && head < b.Count && a[head] == b[head])
        {
            head++;
        }

        int tail = 0;
        while (tail < a.Count - head && tail < b.Count - head && a[^(tail + 1)] == b[^(tail + 1)])
        {
            tail++;
        }

        List<string> x = a[head..(a.Count - tail)];
        List<string> y = b[head..(b.Count - tail)];
        List<(char, string)> result = [.. a.Take(head).Select(l => (' ', l))];
        if ((long)x.Count * y.Count > 16_000_000)
        {
            result.AddRange(x.Select(l => ('-', l)));
            result.AddRange(y.Select(l => ('+', l)));
        }
        else
        {
            int[,] length = new int[x.Count + 1, y.Count + 1];
            for (int i = x.Count - 1; i >= 0; i--)
            {
                for (int j = y.Count - 1; j >= 0; j--)
                {
                    length[i, j] = x[i] == y[j] ? length[i + 1, j + 1] + 1 : Math.Max(length[i + 1, j], length[i, j + 1]);
                }
            }

            int p = 0;
            int q = 0;
            while (p < x.Count || q < y.Count)
            {
                if (p < x.Count && q < y.Count && x[p] == y[q])
                {
                    result.Add((' ', x[p]));
                    p++;
                    q++;
                }
                else if (q >= y.Count || (p < x.Count && length[p + 1, q] >= length[p, q + 1]))
                {
                    result.Add(('-', x[p++]));
                }
                else
                {
                    result.Add(('+', y[q++]));
                }
            }
        }

        result.AddRange(a.Skip(a.Count - tail).Select(l => (' ', l)));
        return result;
    }
}
