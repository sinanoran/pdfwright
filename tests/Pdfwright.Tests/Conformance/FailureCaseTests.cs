using Pdfwright.Tests.Document;
using System.Buffers.Binary;
using System.Globalization;

namespace Pdfwright.Tests.Conformance;

/// <summary>
/// The failure cases of plan 8.3 for every script: a glyph that no font has (the diagnostics name the character, the language and the page), a font
/// that may not be embedded (<see cref="FontNotEmbeddableException"/>) and a family that is not in the collection (<c>MissingFamilies</c>).
/// </summary>
public class FailureCaseTests
{
    /// <summary>The groups whose script is in a font of its own: with only Noto Sans in the collection, their text has glyphs no font has.</summary>
    public static TheoryData<string> ScriptGroups() =>
        [.. new[] { "arabic", "hebrew", "mixed", "devanagari", "bengali", "tamil", "thai" }];

    public static TheoryData<string> Fonts() => [.. DocumentTestKit.AllFonts];

    /// <summary>The distinct code points of <paramref name="text"/> that the font has no glyph for, read from its cmap with HarfBuzz and not with the library.</summary>
    private static int[] Lacking(string text, string file)
    {
        using HarfBuzzSharp.Blob blob = HarfBuzzSharp.Blob.FromFile(TestPaths.Font(file))!;
        using HarfBuzzSharp.Face face = new(blob, 0);
        using HarfBuzzSharp.Font font = new(face);
        return [.. text.EnumerateRunes().Select(r => r.Value).Distinct().Where(cp => !char.IsWhiteSpace(char.ConvertFromUtf32(cp), 0) && (!font.TryGetGlyph((uint)cp, out uint glyph) || glyph == 0)).Order()];
    }

    private static PageCorpus Corpus(string group) => PageCorpus.LoadAll().Single(c => c.Group == group);

    [Theory]
    [MemberData(nameof(ScriptGroups))]
    public void A_glyph_that_no_font_has_is_reported_with_its_character_language_and_page(string group)
    {
        PageCorpus corpus = Corpus(group);
        PageCorpus.Item item = corpus.Items.First(i => i.Page == "short" && i.Kind == "para");
        string text = item.Cells[0];
        using FontCollection latinOnly = DocumentTestKit.Collection("NotoSans-Regular.ttf");
        PdfDocument document = new(DocumentTestKit.Options(o =>
        {
            o.Fonts = latinOnly;
            o.DefaultStyle = new TextStyle { Family = "Noto Sans", Size = Length.Pt(10) };
        }));
        document.Add(new Paragraph("Page one is all Latin."));
        document.Add(new PageBreak());
        document.Add(new Paragraph(text) { Style = new TextStyle { Language = item.Language } });

        byte[] pdf = DocumentTestKit.Save(document);

        IReadOnlyList<MissingGlyph> missing = document.Diagnostics.MissingGlyphs;
        Assert.NotEmpty(missing);
        Assert.All(missing, m =>
        {
            Assert.Equal(2, m.Page);
            Assert.Equal(item.Language, m.Language);
            Assert.Equal(char.ConvertFromUtf32(m.CodePoint), m.Character);
            Assert.Contains(m.Character, text, StringComparison.Ordinal);
        });
        Assert.Equal(missing.Count, missing.Select(m => m.CodePoint).Distinct().Count());

        // Exactly the characters of the text that the only font of the collection has no glyph for: not the first one, not a subset, not more.
        Assert.Equal(string.Join(" ", Lacking(text, "NotoSans-Regular.ttf")), string.Join(" ", missing.Select(m => m.CodePoint).Order()));
        Assert.Equal(2, document.Diagnostics.PageCount);
        Assert.Empty(document.Diagnostics.MissingFamilies);
        DocumentTestKit.AssertValid(pdf, $"failure-glyph-{group}");
    }

    [Theory]
    [MemberData(nameof(Fonts))]
    public void A_font_that_may_not_be_embedded_is_refused_and_names_its_family(string file)
    {
        foreach (ushort fsType in new ushort[] { 0x0002, 0x0200 })
        {
            byte[] bytes = File.ReadAllBytes(TestPaths.Font(file));
            SetFsType(bytes, fsType);
            using FontCollection fonts = new();

            FontNotEmbeddableException error = Assert.Throws<FontNotEmbeddableException>(() => fonts.Add(bytes));

            Assert.Equal(fsType, error.EmbeddingPermission);
            Assert.StartsWith("Noto Sans", error.Family, StringComparison.Ordinal);
            Assert.Contains(error.Family, error.Message, StringComparison.Ordinal);
            Assert.Equal(0, fonts.Count);
        }
    }

    [Theory]
    [MemberData(nameof(ScriptGroups))]
    public void A_family_that_is_not_in_the_collection_is_listed_and_the_text_is_drawn_by_the_others(string group)
    {
        PageCorpus corpus = Corpus(group);
        PageCorpus.Item item = corpus.Items.First(i => i.Page == "short" && i.Kind == "para");
        PdfDocument document = new(DocumentTestKit.Options(o => o.DefaultStyle = new TextStyle { Family = "Absent Sans", Size = Length.Pt(10), Language = item.Language }));
        document.Add(new Paragraph(item.Cells[0]));

        byte[] pdf = DocumentTestKit.Save(document);

        Assert.Equal(["Absent Sans"], document.Diagnostics.MissingFamilies);
        Assert.Empty(document.Diagnostics.MissingGlyphs);
        Extraction.Result read = Extraction.Match(string.Join(Extraction.Line, ScriptConformanceTests.PageTexts(pdf)), [item.Cells[0]], []);
        Assert.True(read.Failures.Count == 0 && read.Trailing.Length == 0, string.Join(Environment.NewLine, read.Failures) + read.Trailing);
        DocumentTestKit.AssertValid(pdf, $"failure-family-{group}");
    }

    private static void SetFsType(byte[] font, ushort value)
    {
        int count = BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(4, 2));
        for (int i = 0; i < count; i++)
        {
            int record = 12 + (i * 16);
            if (System.Text.Encoding.ASCII.GetString(font, record, 4) == "OS/2")
            {
                int offset = (int)BinaryPrimitives.ReadUInt32BigEndian(font.AsSpan(record + 8, 4));
                BinaryPrimitives.WriteUInt16BigEndian(font.AsSpan(offset + 8, 2), value);
                return;
            }
        }

        throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"no OS/2 table in a font of {font.Length} bytes"));
    }
}
