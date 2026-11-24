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
/// <remarks>The content streams are read as they are, so the file is written with <c>PdfCompression.None</c>.</remarks>
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
        return reader;
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
        foreach (IToken stream in streams)
        {
            StreamToken token = Resolve<StreamToken>(stream);
            Assert.False(token.StreamDictionary.Data.ContainsKey("Filter"), "the reader takes uncompressed content streams");
            text.Append(Encoding.Latin1.GetString(token.Data.Span));
        }

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
    /// entry per page, under the page's <c>/StructParents</c>; <c>/ParentTreeNextKey</c> is past every key; every page has <c>/Tabs /S</c>.
    /// </summary>
    internal void AssertConsistent()
    {
        Assert.True(Marked, "/MarkInfo <</Marked true>>");
        Assert.Equal("Document", Root.Type);
        Assert.Equal(ParentTreeKeyOrder.Order(), ParentTreeKeyOrder);

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

    [GeneratedRegex(@"/([A-Za-z0-9_.\-]+) <</MCID (\d+)>> BDC")]
    private static partial Regex McidPattern();
}
