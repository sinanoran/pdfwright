using Pdfwright.Unicode;
using Pdfwright.UnicodeGen;

namespace Pdfwright.Tests.Unicode;

/// <summary>Script itemization (UAX #24): the runs that are each shaped with one script tag.</summary>
public class ScriptItemizerTests
{
    private static List<(int Start, int Length, string Script)> Runs(string text)
    {
        int[] codePoints = UnicodeText.CodePoints(text, out _);
        return [.. ScriptItemizer.Itemize(codePoints).Select(r => (r.Start, r.Length, ScriptData.CodeOf(r.Script)))];
    }

    [Fact]
    public void The_generated_script_tables_are_what_the_generator_makes_of_the_data()
    {
        string committed = File.ReadAllText(Path.Combine(TestPaths.RepositoryRoot, "src", "Pdfwright", "Unicode", "ScriptTables.g.cs")).Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Equal(ScriptDataGenerator.Generate(Path.Combine(TestPaths.RepositoryRoot, "data", "unicode", "18.0.0")), committed);
    }

    [Theory]
    [InlineData(0x0041, "Latn")]
    [InlineData(0x0416, "Cyrl")]
    [InlineData(0x03A9, "Grek")]
    [InlineData(0x0627, "Arab")]
    [InlineData(0x05D0, "Hebr")]
    [InlineData(0x0915, "Deva")]
    [InlineData(0x0995, "Beng")]
    [InlineData(0x0B95, "Taml")]
    [InlineData(0x0E01, "Thai")]
    [InlineData(0x4E00, "Hani")]
    [InlineData(0x3042, "Hira")]
    [InlineData(0x0020, "Zyyy")]
    [InlineData(0x0301, "Zinh")]
    [InlineData(0x0378, "Zzzz")]
    public void A_character_has_the_script_the_data_gives_it(int codePoint, string code) =>
        Assert.Equal(code, ScriptData.CodeOf(ScriptData.ScriptOf(codePoint)));

    [Fact]
    public void Text_in_one_script_is_one_run_with_its_spaces_digits_and_punctuation()
    {
        Assert.Equal([(0, 12, "Latn")], Runs("Hello, world"));
        Assert.Equal([(0, 5, "Latn")], Runs("abc12"));
    }

    [Fact]
    public void A_change_of_script_starts_a_run_and_the_space_before_it_stays_with_the_run_it_follows()
    {
        Assert.Equal([(0, 6, "Latn"), (6, 6, "Cyrl")], Runs("Hello Привет"));
    }

    [Fact]
    public void Text_that_starts_with_digits_or_punctuation_takes_the_script_of_what_follows()
    {
        Assert.Equal([(0, 8, "Latn")], Runs("123 abc."));
        Assert.Equal([(0, 4, "Arab")], Runs("1 مر"));
        Assert.Equal([(0, 3, "Zyyy")], Runs("1 2"));
    }

    [Fact]
    public void A_combining_mark_is_in_the_run_of_its_base()
    {
        Assert.Equal([(0, 2, "Latn")], Runs("á"));
        Assert.Equal([(0, 7, "Latn"), (7, 2, "Grek")], Runs("abc de ά"));
    }

    [Fact]
    public void A_closing_bracket_takes_the_script_of_its_opening_bracket()
    {
        // "(" and ")" are in the Latin run around an Arabic word, which is a run of its own.
        string text = "a (مرح) b";

        Assert.Equal([(0, 3, "Latn"), (3, 3, "Arab"), (6, 3, "Latn")], Runs(text));
    }

    [Fact]
    public void A_bracket_pair_inside_one_script_stays_in_it()
    {
        Assert.Equal([(0, 9, "Arab")], Runs("مر (حب) ا"));
    }

    [Fact]
    public void Devanagari_with_its_danda_is_one_run_because_the_danda_is_used_with_Devanagari()
    {
        Assert.Equal([(0, 8, "Deva")], Runs("नमस्ते। "));
    }

    [Fact]
    public void Japanese_text_is_a_run_per_script_with_the_prolonged_sound_mark_in_the_katakana_run()
    {
        // U+30FC is used with Hiragana and Katakana: it stays in the run it follows.
        List<(int Start, int Length, string Script)> runs = Runs("カーあー漢");

        Assert.Equal([(0, 2, "Kana"), (2, 2, "Hira"), (4, 1, "Hani")], runs);
    }

    [Fact]
    public void A_bracket_opened_before_the_first_script_belongs_to_that_script_and_its_closer_returns_to_it()
    {
        // "(" is pending until the Cyrillic letter; ")" after a Latin word is in the Cyrillic script, as its opener is.
        Assert.Equal([(0, 3, "Cyrl"), (3, 1, "Latn"), (4, 1, "Cyrl")], Runs("(б a)"));
    }

    [Fact]
    public void A_character_used_with_a_script_stays_in_that_run_through_its_script_extensions()
    {
        // Arabic-Indic digits are script Arabic and also used with Thaana: after a Thaana letter they stay in the Thaana run.
        Assert.Equal([(0, 5, "Thaa")], Runs("ހށ ١٢"));
        Assert.Equal([(0, 2, "Latn"), (2, 2, "Arab")], Runs("a ١٢")); // in Latin text they start an Arabic run
    }

    [Fact]
    public void Unassigned_and_private_use_characters_join_the_run_they_are_in()
    {
        Assert.Equal([(0, 10, "Latn")], Runs("Save  now"));
        Assert.Equal([(0, 5, "Latn")], Runs("ab͸cd"));
    }

    [Fact]
    public void Nothing_in_gives_nothing_out_and_the_runs_cover_the_whole_text()
    {
        Assert.Empty(ScriptItemizer.Itemize([]));

        const string Text = "Hello Привет مرحبا 123 नमस्ते (x) สวัสดี";
        int[] codePoints = UnicodeText.CodePoints(Text, out _);
        List<(int Start, int Length, int Script)> runs = ScriptItemizer.Itemize(codePoints);

        Assert.Equal(0, runs[0].Start);
        for (int i = 1; i < runs.Count; i++)
        {
            Assert.Equal(runs[i - 1].Start + runs[i - 1].Length, runs[i].Start);
            Assert.NotEqual(runs[i - 1].Script, runs[i].Script);
        }

        Assert.Equal(codePoints.Length, runs[^1].Start + runs[^1].Length);
    }

    [Fact]
    public void The_script_tag_of_a_known_script_can_be_looked_up_and_an_unknown_one_is_refused()
    {
        Assert.Equal("Arab", ScriptData.CodeOf(ScriptData.IdOf("Arab")));
        Assert.Throws<ArgumentException>(() => ScriptData.IdOf("Xxxx"));
    }
}
