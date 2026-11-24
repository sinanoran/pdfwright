using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Tokens;

namespace Pdfwright.Tests.Pdf;

/// <summary>
/// Reads the structure tree of a tagged PDF back, through PdfPig's object access (not through this library), so the tests compare what the file
/// says with what was meant: the element tree in order, each element's kids (marked content on its own page, marked-content references to another
/// page, child elements), the parent tree and each page's <c>/StructParents</c>, and the <c>MCID</c>s of the content streams.
/// </summary>
/// <remarks>
/// The file may be uncompressed or compressed: a content stream with <c>/FlateDecode</c> is inflated here, and the structure elements of a compressed
/// tagged file, which are in object streams, are counted in the inflated object streams.
/// </remarks>
internal sealed partial class StructureTreeReader
{
    private readonly UglyToad.PdfPig.PdfDocument _document;
    private readonly Dictionary<long, int> _pageIndexByObject = [];

    private StructureTreeReader(UglyToad.PdfPig.PdfDocument document)
    {
        _document = document;
    }

    /// <summary>The <c>Document</c> element and everything under it.</summary>
    internal Element Root { get; private set; } = null!;

    /// <summary>Each page's <c>/StructParents</c>, by page index; <see langword="null"/> for a page without one.</summary>
    internal List<int?> StructParents { get; } = [];

    /// <summary>Each page's <c>/Tabs</c>, by page index.</summary>
    internal List<string?> Tabs { get; } = [];

    /// <summary>The marked content with an <c>MCID</c> of each page's content streams, in order: the tag and the number.</summary>
    internal List<List<(string Tag, int Mcid)>> ContentMcids { get; } = [];

    /// <summary>All of each page's content streams, one after another, as text.</summary>
    internal List<string> ContentText { get; } = [];

    /// <summary>Each page's content streams (header, body, footer) as text, one by one: marked content must balance in each of them.</summary>
    internal List<List<string>> ContentStreams { get; } = [];

    /// <summary>How many objects of the file are structure elements (<c>/Type /StructElem</c>), counted in the file's bytes, not through the tree.</summary>
    internal int StructElemObjects { get; private set; }

    /// <summary>The parent tree: key, then the object number of the element of each <c>MCID</c> of that page.</summary>
    internal SortedDictionary<int, List<long>> ParentTree { get; } = [];

    /// <summary>The keys of the parent tree in the order the <c>/Nums</c> array has them.</summary>
    internal List<int> ParentTreeKeyOrder { get; } = [];

    internal int ParentTreeNextKey { get; private set; }

    /// <summary>The catalog's <c>/MarkInfo /Marked</c>.</summary>
    internal bool Marked { get; private set; }

    /// <summary>A structure element as the file has it.</summary>
    internal sealed record Element(long Object, string Type, long? ParentObject, int? Page, string? Alt, string? Language, DictionaryToken? Attributes, List<Kid> Kids)
    {
        internal IEnumerable<Element> Children => Kids.OfType<ChildKid>().Select(k => k.Element);

        /// <summary>The element and every element under it, in the tree's order.</summary>
        internal IEnumerable<Element> Descendants() => Children.SelectMany(c => c.Descendants()).Prepend(this);

        /// <summary>The tree as one line: <c>Document[H1,Div[P,P]]</c>.</summary>
        internal string Outline() => Children.Any() ? $"{Type}[{string.Join(",", Children.Select(c => c.Outline()))}]" : Type;
    }

    internal abstract record Kid;

    /// <summary>Marked content: an <c>MCID</c> on a page, and whether it was written as a marked-content reference (<c>/Type /MCR</c>).</summary>
    internal sealed record ContentKid(int Page, int Mcid, bool Reference) : Kid;

    internal sealed record ChildKid(Element Element) : Kid;

    internal static StructureTreeReader Read(byte[] pdf)
    {
        using UglyToad.PdfPig.PdfDocument document = UglyToad.PdfPig.PdfDocument.Open(pdf);
        StructureTreeReader reader = new(document);
        reader.ReadAll();

        // Every element's dictionary is in the bytes as written, or in an object stream, inflated here from the file's bytes (not through PdfPig).
        string file = Encoding.Latin1.GetString(pdf);
        reader.StructElemObjects = StructElemPattern().Count(file) + ObjectStreams(pdf).Sum(data => StructElemPattern().Count(Encoding.Latin1.GetString(data)));
        return reader;
    }

    /// <summary>The data of each object stream (<c>/Type /ObjStm</c>) of a file of this writer, inflated: the objects it holds, after their numbers.</summary>
    internal static List<byte[]> ObjectStreams(byte[] pdf)
    {
        string file = Encoding.Latin1.GetString(pdf);
        List<byte[]> streams = [];
        foreach (Match match in ObjectStreamPattern().Matches(file))
        {
            int start = match.Index + match.Length;
            int length = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            streams.Add(match.Value.Contains("/FlateDecode", StringComparison.Ordinal) ? Inflate(pdf.AsSpan(start, length)) : pdf.AsSpan(start, length).ToArray());
        }

        return streams;
    }

    /// <summary>Zlib data inflated.</summary>
    internal static byte[] Inflate(ReadOnlySpan<byte> data)
    {
        using MemoryStream input = new(data.ToArray());
        using System.IO.Compression.ZLibStream zlib = new(input, System.IO.Compression.CompressionMode.Decompress);
        using MemoryStream output = new();
        zlib.CopyTo(output);
        return output.ToArray();
    }

    private void ReadAll()
    {
        DictionaryToken catalog = _document.Structure.Catalog.CatalogDictionary;
        DictionaryToken pages = Resolve<DictionaryToken>(catalog.Data["Pages"]);
        List<IToken> kids = [.. Resolve<ArrayToken>(pages.Data["Kids"]).Data];
        for (int index = 0; index < kids.Count; index++)
        {
            _pageIndexByObject[((IndirectReferenceToken)kids[index]).Data.ObjectNumber] = index;
            DictionaryToken page = Resolve<DictionaryToken>(kids[index]);
            StructParents.Add(page.Data.TryGetValue("StructParents", out IToken? key) ? ((NumericToken)key).Int : null);
            Tabs.Add(page.Data.TryGetValue("Tabs", out IToken? tabs) ? ((NameToken)tabs).Data : null);
            ReadContent(page);
        }

        Marked = catalog.Data.TryGetValue("MarkInfo", out IToken? markInfo)
            && Resolve<DictionaryToken>(markInfo).Data.TryGetValue("Marked", out IToken? marked)
            && ((BooleanToken)marked).Data;

        DictionaryToken root = Resolve<DictionaryToken>(catalog.Data["StructTreeRoot"]);
        Assert.Equal("StructTreeRoot", ((NameToken)root.Data["Type"]).Data);
        IToken top = root.Data["K"];
        Root = ReadElement((IndirectReferenceToken)top, ((IndirectReferenceToken)catalog.Data["StructTreeRoot"]).Data.ObjectNumber);

        List<IToken> nums = [.. Resolve<ArrayToken>(Resolve<DictionaryToken>(root.Data["ParentTree"]).Data["Nums"]).Data];
        Assert.True(nums.Count % 2 == 0, "/Nums holds pairs");
        for (int i = 0; i < nums.Count; i += 2)
        {
            int key = ((NumericToken)nums[i]).Int;
            ParentTreeKeyOrder.Add(key);
            ParentTree.Add(key, [.. Resolve<ArrayToken>(nums[i + 1]).Data.Select(t => ((IndirectReferenceToken)t).Data.ObjectNumber)]);
        }

        ParentTreeNextKey = ((NumericToken)root.Data["ParentTreeNextKey"]).Int;
    }

    private Element ReadElement(IndirectReferenceToken reference, long expectedParent)
    {
        DictionaryToken dictionary = Resolve<DictionaryToken>(reference);
        Assert.Equal("StructElem", ((NameToken)dictionary.Data["Type"]).Data);
        long parent = ((IndirectReferenceToken)dictionary.Data["P"]).Data.ObjectNumber;
        Assert.Equal(expectedParent, parent);
        int? page = dictionary.Data.TryGetValue("Pg", out IToken? pg) ? PageOf(pg) : null;
        Element element = new(
            reference.Data.ObjectNumber,
            ((NameToken)dictionary.Data["S"]).Data,
            parent,
            page,
            dictionary.Data.TryGetValue("Alt", out IToken? alt) ? Text(alt) : null,
            dictionary.Data.TryGetValue("Lang", out IToken? lang) ? Text(lang) : null,
            dictionary.Data.TryGetValue("A", out IToken? a) ? Resolve<DictionaryToken>(a) : null,
            []);

        if (dictionary.Data.TryGetValue("K", out IToken? k))
        {
            IEnumerable<IToken> items = k is ArrayToken array ? array.Data : [k];
            foreach (IToken item in items)
            {
                switch (item)
                {
                    case NumericToken mcid:
                        Assert.True(page is not null, $"element {element.Type} {element.Object} has an MCID and no /Pg");
                        element.Kids.Add(new ContentKid(page!.Value, mcid.Int, Reference: false));
                        break;
                    case IndirectReferenceToken child:
                        element.Kids.Add(new ChildKid(ReadElement(child, element.Object)));
                        break;
                    case DictionaryToken mcr:
                        Assert.Equal("MCR", ((NameToken)mcr.Data["Type"]).Data);
                        element.Kids.Add(new ContentKid(PageOf(mcr.Data["Pg"]), ((NumericToken)mcr.Data["MCID"]).Int, Reference: true));
                        break;
                    default:
                        Assert.Fail($"an unexpected kid {item} in element {element.Type}");
                        break;
                }
            }
        }

        return element;
    }

    private void ReadContent(DictionaryToken page)
    {
        IToken contents = page.Data["Contents"];
        IEnumerable<IToken> streams = contents is ArrayToken array ? array.Data : (Resolve<IToken>(contents) is ArrayToken resolved ? resolved.Data : [contents]);
        StringBuilder text = new();
        List<string> each = [];
        foreach (IToken stream in streams)
        {
            StreamToken token = Resolve<StreamToken>(stream);
            byte[] data = token.Data.ToArray();
            if (token.StreamDictionary.Data.TryGetValue("Filter", out IToken? filter))
            {
                Assert.Equal("FlateDecode", Assert.IsType<NameToken>(filter).Data);
                Assert.False(token.StreamDictionary.Data.ContainsKey("DecodeParms"), "a content stream is deflated with no predictor");
                data = Inflate(data);
            }

            each.Add(Encoding.Latin1.GetString(data));
            text.Append(each[^1]);
        }

        ContentStreams.Add(each);
        ContentText.Add(text.ToString());
        ContentMcids.Add([.. McidPattern().Matches(text.ToString()).Select(m => (m.Groups[1].Value, int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture)))]);
    }

    private int PageOf(IToken token)
    {
        long number = ((IndirectReferenceToken)token).Data.ObjectNumber;
        Assert.True(_pageIndexByObject.TryGetValue(number, out int index), $"/Pg {number} is not a page of the page tree");
        return index;
    }

    private static string Text(IToken token) => token switch
    {
        StringToken s => s.Data,
        HexToken h => h.Data,
        _ => throw new InvalidOperationException($"{token} is not a string"),
    };

    private T Resolve<T>(IToken token)
        where T : IToken
    {
        while (token is IndirectReferenceToken reference)
        {
            token = _document.Structure.GetObject(reference.Data).Data;
        }

        return (T)token;
    }

    /// <summary>
    /// Asserts what any tagged file of this writer must hold, whatever its content: every <c>MCID</c> of every page is drawn once in the page's
    /// content and is in the tree once, under the element the parent tree names for it, with the element's type as its tag; the parent tree has one
    /// entry per page, under the page's <c>/StructParents</c>; <c>/ParentTreeNextKey</c> is past every key; every page has <c>/Tabs /S</c>; each
    /// structure element is one object, written once, and reached once from the root; marked content balances in every content stream.
    /// </summary>
    internal void AssertConsistent()
    {
        Assert.True(Marked, "/MarkInfo <</Marked true>>");
        Assert.Equal("Document", Root.Type);
        Assert.Equal(ParentTreeKeyOrder.Order(), ParentTreeKeyOrder);

        List<long> elementObjects = [.. Root.Descendants().Select(e => e.Object)];
        Assert.Equal(elementObjects.Count, elementObjects.Distinct().Count());
        Assert.Equal(StructElemObjects, elementObjects.Count);

        for (int page = 0; page < ContentStreams.Count; page++)
        {
            for (int stream = 0; stream < ContentStreams[page].Count; stream++)
            {
                int depth = 0;
                foreach (Match op in MarkedContentOperatorPattern().Matches(ContentStreams[page][stream]))
                {
                    depth += op.Groups[1].Value == "EMC" ? -1 : 1;
                    Assert.True(depth >= 0, $"an EMC without its BDC or BMC in stream {stream + 1} of page {page + 1}");
                }

                Assert.True(depth == 0, $"{depth} marked-content sequence(s) left open in stream {stream + 1} of page {page + 1}");
                AssertEverythingMarked(ContentStreams[page][stream], $"stream {stream + 1} of page {page + 1}");
            }
        }

        Dictionary<(int Page, int Mcid), Element> owners = [];
        foreach (Element element in Root.Descendants())
        {
            foreach (ContentKid content in element.Kids.OfType<ContentKid>())
            {
                Assert.True(owners.TryAdd((content.Page, content.Mcid), element), $"MCID {content.Mcid} of page {content.Page + 1} is in the tree twice");
                Assert.Equal(content.Page != element.Page, content.Reference);
            }
        }

        Assert.Equal(StructParents.Count, ParentTree.Count);
        for (int page = 0; page < StructParents.Count; page++)
        {
            Assert.Equal("S", Tabs[page]);
            int key = Assert.IsType<int>(StructParents[page]);
            Assert.True(ParentTree.TryGetValue(key, out List<long>? entry), $"page {page + 1}'s /StructParents {key} is not a key of the parent tree");
            Assert.True(key < ParentTreeNextKey, $"/ParentTreeNextKey {ParentTreeNextKey} is not past key {key}");

            List<(string Tag, int Mcid)> drawn = ContentMcids[page];
            Assert.Equal(Enumerable.Range(0, drawn.Count), drawn.Select(d => d.Mcid).Order());
            Assert.Equal(drawn.Count, entry!.Count);
            foreach ((string tag, int mcid) in drawn)
            {
                Assert.True(owners.TryGetValue((page, mcid), out Element? owner), $"MCID {mcid} of page {page + 1} is in no element");
                Assert.Equal(owner!.Object, entry[mcid]);
                Assert.Equal(owner.Type, tag);
            }

            Assert.Equal(drawn.Count, owners.Keys.Count(k => k.Page == page));
        }
    }

    /// <summary>The operators that paint or show something (ISO 32000-1, tables 51, 57, 60, 92 and 107): what PDF/UA-1 wants inside marked content of the structure or an artifact.</summary>
    private static readonly HashSet<string> PaintingOperators = ["Tj", "TJ", "'", "\"", "f", "F", "f*", "B", "B*", "b", "b*", "S", "s", "Do", "sh", "BI"];

    private enum Marking
    {
        Structure,
        Artifact,
        Other,
    }

    /// <summary>
    /// The core rule of PDF/UA-1 (ISO 14289-1, 7.1): every operator that paints or shows is inside a sequence with an <c>MCID</c> or inside an artifact,
    /// and no sequence with an <c>MCID</c> is inside an artifact (nor inside another one). A form XObject drawn by <c>Do</c> counts as the <c>Do</c>.
    /// The stream is tokenised (strings, names, dictionaries and arrays read as tokens, an inline image skipped to its <c>EI</c>), not matched line by line.
    /// </summary>
    private static void AssertEverythingMarked(string content, string where)
    {
        List<Marking> open = [];
        List<string> operands = [];
        int i = 0;
        while (i < content.Length)
        {
            char c = content[i];
            if (char.IsWhiteSpace(c) || c is '[' or ']' or '{' or '}')
            {
                i++;
            }
            else if (c == '%')
            {
                while (i < content.Length && content[i] is not ('\n' or '\r'))
                {
                    i++;
                }
            }
            else if (c == '(')
            {
                int depth = 0;
                do
                {
                    if (content[i] == '\\')
                    {
                        i++;
                    }
                    else if (content[i] == '(')
                    {
                        depth++;
                    }
                    else if (content[i] == ')')
                    {
                        depth--;
                    }

                    i++;
                }
                while (depth > 0 && i < content.Length);
                operands.Add("(string)");
            }
            else if (c is '<' or '>' && i + 1 < content.Length && content[i + 1] == c)
            {
                operands.Add(c == '<' ? "<<" : ">>");
                i += 2;
            }
            else if (c == '<')
            {
                i = content.IndexOf('>', i) + 1;
                operands.Add("<hex>");
            }
            else
            {
                int start = i;
                i++;
                while (i < content.Length && !char.IsWhiteSpace(content[i]) && content[i] is not ('(' or ')' or '<' or '>' or '[' or ']' or '{' or '}' or '/' or '%'))
                {
                    i++;
                }

                string token = content[start..i];
                if (c is '/' or (>= '0' and <= '9') or '-' or '+' or '.' || token is "true" or "false" or "null")
                {
                    operands.Add(token);
                    continue;
                }

                if (token is "BDC" or "BMC")
                {
                    Marking marking = operands.FirstOrDefault(o => o.StartsWith('/')) == "/Artifact" ? Marking.Artifact : operands.Contains("/MCID") ? Marking.Structure : Marking.Other;
                    if (marking == Marking.Structure)
                    {
                        Assert.False(open.Contains(Marking.Artifact), $"marked content with an MCID inside an artifact in {where}");
                        Assert.False(open.Contains(Marking.Structure), $"marked content with an MCID inside another in {where}");
                    }

                    open.Add(marking);
                }
                else if (token == "EMC")
                {
                    Assert.NotEmpty(open);
                    open.RemoveAt(open.Count - 1);
                }
                else if (PaintingOperators.Contains(token))
                {
                    Assert.True(
                        open.Contains(Marking.Structure) || open.Contains(Marking.Artifact),
                        $"'{token}' paints outside marked content of the structure and outside an artifact in {where}");
                    if (token == "BI")
                    {
                        // An inline image: its data runs to EI, which is skipped whole.
                        int end = content.IndexOf("EI", i, StringComparison.Ordinal);
                        i = end < 0 ? content.Length : end + 2;
                    }
                }

                operands.Clear();
            }
        }
    }

    [GeneratedRegex(@"/([A-Za-z0-9_.\-]+) <</MCID (\d+)>> BDC")]
    private static partial Regex McidPattern();

    // The operators end their line in what this writer makes, and glyphs are hexadecimal strings, so an operator name is never text drawn.
    [GeneratedRegex(@"(?<=[\s>]|^)(BDC|BMC|EMC)\n")]
    private static partial Regex MarkedContentOperatorPattern();

    [GeneratedRegex(@"/Type /StructElem\b")]
    private static partial Regex StructElemPattern();

    // An object stream's dictionary as the serializer writes it, up to the start of its data.
    [GeneratedRegex(@"<<\n/Type /ObjStm\n[^>]*?/Length (\d+)\n>>\nstream\n")]
    private static partial Regex ObjectStreamPattern();
}
