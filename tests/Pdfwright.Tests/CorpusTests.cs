using System.Text;
using HarfBuzzSharp;

namespace Pdfwright.Tests;

/// <summary>
/// The corpus is well formed and drawable: strict UTF-8, normalization form C, unique ids, listed fonts, and a
/// glyph in one of those fonts for every character. The read-back test of a generated file builds on this
/// (slice S1 onwards).
/// </summary>
public class CorpusTests
{
    internal sealed record Case(string File, int Line, string Id, string[] Fonts, string Text);

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    internal static List<Case> Load()
    {
        List<Case> cases = [];
        foreach (string path in Directory.GetFiles(TestPaths.CorpusDirectory, "*.txt").Order())
        {
            string content = StrictUtf8.GetString(File.ReadAllBytes(path));
            Assert.False(content.StartsWith('\uFEFF'), $"{Path.GetFileName(path)} starts with a byte-order mark");
            string[] lines = content.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].TrimEnd('\r');
                if (line.Length == 0 || line.StartsWith('#'))
                {
                    continue;
                }

                string[] parts = line.Split('|', 3);
                Assert.True(parts.Length == 3, $"{Path.GetFileName(path)}:{i + 1} is not 'id|fonts|text'");
                Assert.False(parts[0].Length == 0 || parts[2].Length == 0, $"{Path.GetFileName(path)}:{i + 1} has an empty id or text");
                cases.Add(new Case(Path.GetFileName(path), i + 1, parts[0], parts[1].Split('+'), parts[2]));
            }
        }

        return cases;
    }

    public static TheoryData<string> Ids()
    {
        TheoryData<string> data = [];
        foreach (Case c in Load())
        {
            data.Add(c.Id);
        }

        return data;
    }

    [Fact]
    public void The_corpus_covers_every_script_the_plan_names_and_ids_are_unique()
    {
        List<Case> cases = Load();

        Assert.Equal(cases.Count, cases.Select(c => c.Id).Distinct().Count());
        foreach (string prefix in new[] { "latin", "greek", "cyrillic", "arabic", "hebrew", "mixed", "devanagari", "bengali", "tamil", "thai" })
        {
            Assert.Contains(cases, c => c.Id.StartsWith(prefix, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Every_text_is_in_normalization_form_c()
    {
        foreach (Case c in Load())
        {
            Assert.True(
                c.Text.IsNormalized(NormalizationForm.FormC),
                $"{c.File}:{c.Line} '{c.Id}' is not in NFC: a case must be the string it looks like");
        }
    }

    [Fact]
    public void Every_listed_font_is_one_of_the_recorded_fonts()
    {
        foreach (Case c in Load())
        {
            foreach (string font in c.Fonts)
            {
                Assert.True(File.Exists(TestPaths.Font(font)), $"{c.File}:{c.Line} '{c.Id}' names {font}, which is not in Fonts/");
            }
        }
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public void Every_character_of_a_case_has_a_glyph_in_one_of_its_fonts(string id)
    {
        Case c = Load().Single(x => x.Id == id);
        List<Font> fonts = [];
        List<Face> faces = [];
        List<Blob> blobs = [];
        try
        {
            foreach (string name in c.Fonts)
            {
                Blob blob = Blob.FromFile(TestPaths.Font(name))!;
                Face face = new(blob, 0);
                blobs.Add(blob);
                faces.Add(face);
                fonts.Add(new Font(face));
            }

            List<string> missing = [];
            foreach (Rune rune in c.Text.EnumerateRunes())
            {
                // Spaces and the format characters of the Unicode bidi algorithm need no glyph of their own.
                if (Rune.IsWhiteSpace(rune) || rune.Value is 0x200E or 0x200F or 0x200C or 0x200D)
                {
                    continue;
                }

                if (!fonts.Any(f => f.TryGetNominalGlyph((uint)rune.Value, out _)))
                {
                    missing.Add($"U+{rune.Value:X4}");
                }
            }

            Assert.True(
                missing.Count == 0,
                $"'{id}' needs a glyph for {string.Join(", ", missing.Distinct())} that {string.Join(" + ", c.Fonts)} do not have");
        }
        finally
        {
            foreach (Font f in fonts)
            {
                f.Dispose();
            }

            foreach (Face f in faces)
            {
                f.Dispose();
            }

            foreach (Blob b in blobs)
            {
                b.Dispose();
            }
        }
    }
}
