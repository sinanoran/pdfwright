using System.Text;
using System.Text.RegularExpressions;

namespace Pdfwright.Tests;

/// <summary>
/// The corpus text was corrected after a language review (the sentences are what a speaker would write). Each case
/// exists to exercise something in the engine, and a correction must not take that away: these tests state, for the
/// corrected lines, the property each one is there for.
/// </summary>
public class CorpusLanguageReviewTests
{
    private static string Line(string id) => CorpusTests.Load().Single(c => c.Id == id).Text;

    private static string Page(string group) => new UTF8Encoding(false, true).GetString(File.ReadAllBytes(Path.Combine(TestPaths.CorpusDirectory, "pages", group + ".txt")));

    [Fact]
    public void The_lam_alef_line_still_has_the_hamza_above_hamza_below_and_madda_forms()
    {
        foreach (string text in new[] { Line("arabic-lam-alef-hamza"), Page("arabic") })
        {
            Assert.Contains("لأ", text, StringComparison.Ordinal);
            Assert.Contains("لإ", text, StringComparison.Ordinal);
            Assert.Contains("لآ", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_arabic_page_still_has_shadda_dagger_alef_and_tatweel()
    {
        string page = Page("arabic");
        Assert.Contains("ّ", page, StringComparison.Ordinal);
        Assert.Contains("ٰ", page, StringComparison.Ordinal);
        Assert.Contains("ـ", page, StringComparison.Ordinal);
        Assert.Contains("ّ", Line("arabic-shadda-dagger-alef"), StringComparison.Ordinal);
        Assert.Contains("ٰ", Line("arabic-shadda-dagger-alef"), StringComparison.Ordinal);
    }

    [Fact]
    public void The_hebrew_points_line_still_has_dagesh_and_a_shin_dot_and_the_latin_phrase_stays_between_hebrew_words()
    {
        foreach (string text in new[] { Line("hebrew-points"), Page("hebrew") })
        {
            Assert.Contains("ּ", text, StringComparison.Ordinal);
            Assert.Contains("ׁ", text, StringComparison.Ordinal);
        }

        Regex hebrewLatinHebrew = new(@"[א-ת][־ ]Quarterly Report [א-ת]");
        Assert.Matches(hebrewLatinHebrew, Line("mixed-he-latin-phrase"));
        Assert.Matches(hebrewLatinHebrew, Page("mixed"));
    }

    [Fact]
    public void The_ukrainian_text_still_has_the_letter_ge_with_upturn_in_both_cases()
    {
        foreach (string text in new[] { Line("cyrillic-uk"), Page("cyrillic") })
        {
            Assert.Contains("Ґ", text, StringComparison.Ordinal);
            Assert.Contains("ґ", text, StringComparison.Ordinal);
            Assert.Contains("’", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_turkish_page_still_has_both_pairs_of_the_letter_i()
    {
        foreach (string text in new[] { Line("latin-tr-dotted-dotless"), Page("latin-extended") })
        {
            Assert.Contains("ı", text, StringComparison.Ordinal);
            Assert.Contains("İ", text, StringComparison.Ordinal);
            Assert.Contains("I", text, StringComparison.Ordinal);
        }

        Assert.Contains("Isparta", Page("latin-extended"), StringComparison.Ordinal);
        Assert.Contains("ı", Page("latin-extended").Split('\n').Single(l => l.Contains("konular", StringComparison.Ordinal)), StringComparison.Ordinal);
    }

    [Fact]
    public void The_polish_and_vietnamese_lines_still_have_ogonek_acute_and_stacked_diacritics()
    {
        string polish = Line("latin-pl");
        foreach (char c in "ąęśćńźżó")
        {
            Assert.Contains(c, polish);
        }

        string vietnamese = Line("latin-vi");
        Assert.EndsWith("Đường Nguyễn Huệ", vietnamese, StringComparison.Ordinal);
        Assert.Contains("Đ", vietnamese, StringComparison.Ordinal);
        Assert.Contains("ễ", vietnamese, StringComparison.Ordinal); // a circumflex with a tilde
    }

    [Fact]
    public void The_german_page_still_has_all_three_umlauts_and_the_sharp_s()
    {
        string line = Page("latin-extended").Split('\n').Single(l => l.Contains("Beschaffungsliste", StringComparison.Ordinal));
        Assert.Contains("Äpfel", line, StringComparison.Ordinal);
        Assert.Contains("Übungshefte", line, StringComparison.Ordinal);
        Assert.Contains("Öl", line, StringComparison.Ordinal);
        Assert.Contains("Lösung finden", line, StringComparison.Ordinal);
        Assert.Contains("ß", Page("latin-extended"), StringComparison.Ordinal);
    }

    [Fact]
    public void The_indic_and_thai_pages_still_have_their_conjuncts_reph_split_vowels_and_stacked_marks()
    {
        string devanagari = Page("devanagari");
        Assert.Contains("द्वि", devanagari, StringComparison.Ordinal); // the conjunct dva with the reordering vowel sign i
        Assert.Contains("राहुल", devanagari, StringComparison.Ordinal);
        Assert.Contains("क्ष", devanagari, StringComparison.Ordinal);

        string bengali = Page("bengali");
        Assert.Contains("র্ষ", bengali, StringComparison.Ordinal); // reph
        Assert.Contains("৯:৩০টা", bengali, StringComparison.Ordinal);

        string tamil = Page("tamil");
        Assert.Contains("ஜெ", tamil, StringComparison.Ordinal); // a pre-base vowel sign after ja
        Assert.Contains("ஜெயக்குமார்", tamil, StringComparison.Ordinal);

        string thai = Page("thai");
        Assert.Contains("ผู้", thai, StringComparison.Ordinal); // a below vowel and a tone mark stacked under and over one consonant
        Assert.Contains("ศรีสุข", thai, StringComparison.Ordinal);
    }
}
