using System.Buffers.Binary;
using System.Text;
using Pdfwright.Fonts;
using Pdfwright.Text;

namespace Pdfwright.Tests.Text;

/// <summary>
/// The check that decides whether a font may be shaped word by word: the rules that match the space glyph without naming it (class 0 of a class definition, which the engine's list of glyphs leaves out) are found in a font patched
/// to have one and in tables built to have each kind, a font is not read after it is disposed, and shaping a slice of printable ASCII with context gives what shaping it alone gives.
/// </summary>
public class SpaceCheckTests
{
    private const int Space = 3;

    private static byte[] U(params int[] values)
    {
        byte[] bytes = new byte[values.Length * 2];
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(2 * i), (ushort)values[i]);
        }

        return bytes;
    }

    private static byte[] Join(params byte[][] parts) => [.. parts.SelectMany(p => p)];

    /// <summary>A GSUB or GPOS table with one lookup of the given type and one subtable, reached through an extension subtable when <paramref name="extension"/> is set.</summary>
    private static byte[] Table(int lookupType, byte[] subtable, bool extension = false, int extensionType = 0)
    {
        byte[] header = U(1, 0, 0, 0, 10);
        byte[] lookupList = U(1, 4);
        byte[] lookup = U(extension ? extensionType : lookupType, 0, 1, 8);
        byte[] body = subtable;
        if (extension)
        {
            body = Join(U(1, lookupType, 0, 0), subtable);
            BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(4), 8);
        }

        return Join(header, lookupList, lookup, body);
    }

    private static byte[] ClassDef(int spaceClass) => spaceClass == 0 ? U(2, 0) : U(2, 1, Space, Space, spaceClass);

    private static byte[] Coverage => U(1, 1, 10);

    /// <summary>A chaining context subtable of format 2 with one rule; the classes of its backtrack, of its input after the first glyph and of its lookahead are the arguments, and the space glyph has the class <paramref name="spaceClass"/> in all three class definitions.</summary>
    private static byte[] Chain2(int[] backtrack, int[] input, int[] lookahead, int spaceClass)
    {
        byte[] classDef = ClassDef(spaceClass);
        int classDefAt = 14 + Coverage.Length;
        int setAt = classDefAt + classDef.Length;
        byte[] rule = Join(U(backtrack.Length), U(backtrack), U(input.Length + 1), U(input), U(lookahead.Length), U(lookahead), U(0));
        return Join(U(2, 14, classDefAt, classDefAt, classDefAt, 1, setAt), Coverage, classDef, U(1, 4), rule);
    }

    private static byte[] Context2(int[] input, int spaceClass)
    {
        byte[] classDef = ClassDef(spaceClass);
        int classDefAt = 10 + Coverage.Length;
        int setAt = classDefAt + classDef.Length;
        byte[] rule = Join(U(input.Length + 1, 0), U(input));
        return Join(U(2, 10, classDefAt, 1, setAt), Coverage, classDef, U(1, 4), rule);
    }

    /// <summary>A pair adjustment of format 2 with two classes for the first glyph and two for the second (0, and 1 for the glyph 10, or for the space when <paramref name="spaceClass"/> is 1); the value in the column of the space's class is <paramref name="spaceColumnValue"/> and the other column is 0.</summary>
    private static byte[] Pair2(int spaceColumnValue, int spaceClass)
    {
        int[] row = new int[2];
        row[spaceClass] = spaceColumnValue;
        byte[] records = U(row[0], row[1], row[0], row[1]);
        byte[] classDef1 = U(2, 0);
        byte[] classDef2 = spaceClass == 0 ? U(2, 1, 10, 10, 1) : U(2, 1, Space, Space, 1);
        int coverageAt = 16 + records.Length;
        int classDef1At = coverageAt + Coverage.Length;
        int classDef2At = classDef1At + classDef1.Length;
        return Join(U(2, coverageAt, 4, 0, classDef1At, classDef2At, 2, 2), records, Coverage, classDef1, classDef2);
    }

    [Fact]
    public void A_pair_adjustment_with_a_value_against_class_zero_refuses_the_font()
    {
        string? refusal = OpenTypeSpaceCheck.Refusal(Table(2, Pair2(-300, 0)), true, Space);
        Assert.NotNull(refusal);
        Assert.Contains("pair adjustment of format 2", refusal);
        Assert.Contains("GPOS lookup 0, subtable 0", refusal);

        // The same through an extension lookup.
        Assert.NotNull(OpenTypeSpaceCheck.Refusal(Table(2, Pair2(-300, 0), extension: true, extensionType: 9), true, Space));

        // No value in the column of the space's class: nothing is lost by shaping the words apart. A value in the column of the space's class when it is listed (class 1) is found too.
        Assert.Null(OpenTypeSpaceCheck.Refusal(Table(2, Pair2(0, 0)), true, Space));
        Assert.Null(OpenTypeSpaceCheck.Refusal(Table(2, Pair2(0, 1)), true, Space));
        Assert.NotNull(OpenTypeSpaceCheck.Refusal(Table(2, Pair2(5, 1)), true, Space));
    }

    [Fact]
    public void A_chaining_context_rule_with_the_class_of_the_space_refuses_the_font()
    {
        // Class 0 in the backtrack, the input (after the first glyph) or the lookahead, the space being unlisted (class 0).
        foreach ((int[] backtrack, int[] input, int[] lookahead, string where) in new (int[], int[], int[], string)[]
        {
            ([0], [1], [1], "backtrack"), ([1], [0], [1], "input"), ([1], [1], [1, 0], "lookahead"),
        })
        {
            string? refusal = OpenTypeSpaceCheck.Refusal(Table(6, Chain2(backtrack, input, lookahead, 0)), false, Space);
            Assert.NotNull(refusal);
            Assert.Contains(where, refusal);
            Assert.NotNull(OpenTypeSpaceCheck.Refusal(Table(6, Chain2(backtrack, input, lookahead, 0), extension: true, extensionType: 7), false, Space));
        }

        // Other classes only: the space (class 0) cannot be matched.
        Assert.Null(OpenTypeSpaceCheck.Refusal(Table(6, Chain2([1], [1], [1], 0)), false, Space));

        // The space is listed in class 2: a rule that does not use class 2 cannot match it, one that does can.
        Assert.Null(OpenTypeSpaceCheck.Refusal(Table(6, Chain2([0], [1], [1], 2)), false, Space));
        Assert.NotNull(OpenTypeSpaceCheck.Refusal(Table(6, Chain2([1], [1], [2], 2)), false, Space));

        // GPOS chaining context (type 8) is read like GSUB's.
        Assert.NotNull(OpenTypeSpaceCheck.Refusal(Table(8, Chain2([1], [0], [1], 0)), true, Space));
    }

    [Fact]
    public void A_context_rule_with_the_class_of_the_space_after_its_first_glyph_refuses_the_font()
    {
        Assert.NotNull(OpenTypeSpaceCheck.Refusal(Table(5, Context2([1, 0], 0)), false, Space));
        Assert.NotNull(OpenTypeSpaceCheck.Refusal(Table(7, Context2([0], 0)), true, Space));
        Assert.Null(OpenTypeSpaceCheck.Refusal(Table(5, Context2([1, 1], 0)), false, Space));

        // A rule of one glyph has no classes after the first, whose glyph the coverage names.
        Assert.Null(OpenTypeSpaceCheck.Refusal(Table(5, Context2([], 0)), false, Space));
    }

    [Fact]
    public void A_coverage_with_the_space_and_a_table_that_is_cut_short_refuse_the_font()
    {
        // A single substitution (GSUB type 1, format 1) that covers the space glyph: the engine reports it too, and so does this.
        Assert.NotNull(OpenTypeSpaceCheck.Refusal(Table(1, Join(U(1, 6, 0), U(1, 1, Space))), false, Space));
        Assert.Null(OpenTypeSpaceCheck.Refusal(Table(1, Join(U(1, 6, 0), U(1, 1, 10))), false, Space));

        byte[] good = Table(6, Chain2([1], [1], [1], 0));
        Assert.Null(OpenTypeSpaceCheck.Refusal(good, false, Space));
        for (int cut = 1; cut < good.Length - 8; cut += 3)
        {
            Assert.NotNull(OpenTypeSpaceCheck.Refusal(good[..cut], false, Space));
        }

        // An unknown lookup type is malformed too, and a font without the table has nothing to refuse.
        Assert.Contains("malformed", OpenTypeSpaceCheck.Refusal(Table(42, U(1, 0)), false, Space));
        Assert.Null(OpenTypeSpaceCheck.Refusal([], false, Space));
    }

    private static int TableOffset(byte[] font, string tag)
    {
        int numTables = BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(4));
        for (int i = 0; i < numTables; i++)
        {
            int record = 12 + (16 * i);
            if (Encoding.ASCII.GetString(font, record, 4) == tag)
            {
                return (int)BinaryPrimitives.ReadUInt32BigEndian(font.AsSpan(record + 8));
            }
        }

        return -1;
    }

    private static int U16(byte[] data, int at) => BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(at));

    /// <summary>Sets the x advance of every value in the column of class 0 of the second glyph in every pair adjustment of format 2 (also behind an extension lookup), and returns how many it set.</summary>
    private static int PatchClassZeroKerning(byte[] font, short value)
    {
        int gpos = TableOffset(font, "GPOS");
        int lookupList = gpos + U16(font, gpos + 8);
        int patched = 0;
        for (int l = 0; l < U16(font, lookupList); l++)
        {
            int lookup = lookupList + U16(font, lookupList + 2 + (2 * l));
            int type = U16(font, lookup);
            for (int s = 0; s < U16(font, lookup + 4); s++)
            {
                int sub = lookup + U16(font, lookup + 6 + (2 * s));
                int subType = type;
                if (type == 9)
                {
                    subType = U16(font, sub + 2);
                    sub += (int)BinaryPrimitives.ReadUInt32BigEndian(font.AsSpan(sub + 4));
                }

                if (subType != 2 || U16(font, sub) != 2 || (U16(font, sub + 4) & 0x4) == 0)
                {
                    continue;
                }

                int vf1 = U16(font, sub + 4);
                int vf2 = U16(font, sub + 6);
                int class2Count = U16(font, sub + 14);
                int recordSize = 2 * (System.Numerics.BitOperations.PopCount((uint)vf1) + System.Numerics.BitOperations.PopCount((uint)vf2));
                int xAdvanceAt = 2 * System.Numerics.BitOperations.PopCount((uint)(vf1 & 0x3));
                for (int c1 = 0; c1 < U16(font, sub + 12); c1++)
                {
                    BinaryPrimitives.WriteInt16BigEndian(font.AsSpan(sub + 16 + (c1 * class2Count * recordSize) + xAdvanceAt), value);
                    patched++;
                }
            }
        }

        return patched;
    }

    [Fact]
    public void A_font_with_kerning_against_class_zero_is_refused_and_shaped_as_a_whole()
    {
        byte[] bytes = File.ReadAllBytes(TestPaths.Font("NotoSans-Regular.ttf"));
        Assert.True(PatchClassZeroKerning(bytes, -300) > 0);
        using ShapingFont font = new(TrueTypeFont.Parse(bytes));
        string? refusal = font.Handle.WordShapingRefusal;
        Assert.NotNull(refusal);
        Assert.Contains("pair adjustment of format 2", refusal);

        // A refused font is shaped whole, so the kerning against the space is still applied.
        foreach (string text in new[] { "T o", "a b", "Row 1 cell 2 text" })
        {
            ShapedParagraph fast = TextShaper.Shape(text, [font], Pdfwright.Unicode.UnicodeBidi.AutoLevel, null);
            ShapedGlyph[] whole = HarfBuzzShaper.Instance.Shape(font.Handle, text, 0, text.Length, false, text.Any(char.IsLetter) ? "Latn" : "Zyyy", null);
            Assert.Equal(whole, fast.Runs.SelectMany(r => r.Glyphs).ToArray());
        }
    }

    [Fact]
    public void The_bundled_fonts_that_pass_still_pass_after_the_class_check()
    {
        string[] passing = ["NotoSans-Regular.ttf", "NotoSans-Bold.ttf", "NotoSansHebrew-Regular.ttf", "NotoSansBengali-Regular.ttf", "NotoSansTamil-Regular.ttf", "NotoSansThai-Regular.ttf"];
        foreach (string file in Pdfwright.Tests.Document.DocumentTestKit.AllFonts)
        {
            using ShapingFont font = new(TrueTypeFont.Parse(File.ReadAllBytes(TestPaths.Font(file))));
            Assert.True(passing.Contains(file) == font.Handle.CanShapeWordByWord, $"{file}: {font.Handle.WordShapingRefusal ?? "passes"}");
        }
    }

    [Fact]
    public void The_verdict_of_a_disposed_font_throws_and_does_not_read_freed_memory()
    {
        ShapingFont font = new(TrueTypeFont.Parse(File.ReadAllBytes(TestPaths.Font("NotoSans-Regular.ttf"))));
        font.Dispose();
        Assert.Throws<ObjectDisposedException>(() => font.Handle.WordShapingRefusal);
    }

    private static string D(ShapedGlyph[] glyphs) => string.Join(";", glyphs.Select(g => $"{g.Glyph}/{g.Cluster}/{g.XAdvance}/{g.XOffset}/{g.YOffset}"));

    [Fact]
    public void The_text_around_a_slice_of_printable_ASCII_does_not_change_its_glyphs()
    {
        foreach (string file in new[] { "NotoSans-Regular.ttf", "NotoSans-Bold.ttf", "NotoSans-Italic.ttf" })
        {
            using ShapingFont font = new(TrueTypeFont.Parse(File.ReadAllBytes(TestPaths.Font(file))));
            Random random = new(3);
            for (int i = 0; i < 20000; i++)
            {
                string text = new([.. Enumerable.Range(0, random.Next(2, 30)).Select(_ => (char)random.Next(' ', '~' + 1))]);
                int start = random.Next(text.Length);
                int length = random.Next(1, text.Length - start + 1);
                string slice = text.Substring(start, length);
                string inContext = D([.. HarfBuzzShaper.Instance.Shape(font.Handle, text, start, length, false, "Latn", null).Select(g => g with { Cluster = g.Cluster - start })]);
                string alone = D(HarfBuzzShaper.Instance.Shape(font.Handle, slice, 0, length, false, "Latn", null));
                Assert.True(inContext == alone, $"{file} [{text}] {start}+{length}");
            }
        }
    }
}
