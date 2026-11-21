namespace Pdfwright.Unicode;

/// <summary>Lookups over the generated <see cref="ScriptTables"/>: the script of a code point and its Script_Extensions.</summary>
internal static class ScriptData
{
    /// <summary>The id of the script Common (Zyyy): punctuation, digits and symbols shared by many scripts.</summary>
    internal static readonly int Common = IdOf("Zyyy");

    /// <summary>The id of the script Inherited (Zinh): combining marks that take the script of their base.</summary>
    internal static readonly int Inherited = IdOf("Zinh");

    /// <summary>The id of the script Unknown (Zzzz): unassigned code points.</summary>
    internal static readonly int Unknown = IdOf("Zzzz");

    /// <summary>The id of a script from its ISO 15924 code.</summary>
    internal static int IdOf(string code)
    {
        int id = Array.IndexOf(ScriptTables.Codes, code);
        return id >= 0 ? id : throw new ArgumentException($"'{code}' is not a script code of Unicode 18.0.", nameof(code));
    }

    /// <summary>The ISO 15924 code of a script id, which is also the script tag HarfBuzz is given.</summary>
    internal static string CodeOf(int script) => ScriptTables.Codes[script];

    /// <summary>The script of a code point (Script property); a value outside the code space is Unknown.</summary>
    internal static int ScriptOf(int codePoint)
    {
        if (codePoint is < 0 or > 0x10FFFF)
        {
            return Unknown;
        }

        int run = Array.BinarySearch(ScriptTables.RunStarts, codePoint);
        if (run < 0)
        {
            run = ~run - 1;
        }

        return ScriptTables.RunScripts[run];
    }

    /// <summary>The scripts a code point is used with (Script_Extensions), or empty when the file lists none: then only <see cref="ScriptOf"/> applies.</summary>
    internal static ReadOnlySpan<byte> ExtensionsOf(int codePoint)
    {
        int index = Array.BinarySearch(ScriptTables.ExtensionFirst, codePoint);
        if (index < 0)
        {
            index = ~index - 1;
        }

        if (index < 0 || codePoint > ScriptTables.ExtensionLast[index])
        {
            return [];
        }

        return ScriptTables.ExtensionScripts.AsSpan(ScriptTables.ExtensionOffset[index], ScriptTables.ExtensionCount[index]);
    }
}

/// <summary>
/// Splits text into runs of one script, the unit that is shaped with one script tag (UAX #24, section 5.1). Common and Inherited
/// characters (spaces, digits, punctuation, combining marks) join the run they are in, or the next run at the start of the text;
/// a closing bracket takes the script of its opening bracket; a character of a real script whose Script_Extensions contain the run's script stays in it.
/// </summary>
/// <remarks>
/// A Common character that is used only with some scripts (U+060C, U+3001, U+0640) stays in the run it follows even when that script is
/// not one of them, as in ICU: the run's script tag is then the neighbour's, so the shaping step must pass the text around a run to
/// HarfBuzz as context (so that an Arabic letter after a tatweel is still joined) and choose fonts per character, not per run. Unknown
/// characters join the run they are in. At most 63 brackets are tracked; deeper openers are ignored.
/// </remarks>
internal static class ScriptItemizer
{
    private const int MaxBracketDepth = 63;

    /// <summary>The runs of <paramref name="text"/> (code points) as <c>(start, length, script id)</c>, in order, covering all of it.</summary>
    internal static List<(int Start, int Length, int Script)> Itemize(ReadOnlySpan<int> text)
    {
        int count = text.Length;
        List<(int Start, int Length, int Script)> runs = [];
        if (count == 0)
        {
            return runs;
        }

        const int Pending = -1;
        int[] resolved = new int[count];
        int current = Pending;
        List<(int Closing, int Script)> brackets = [];
        for (int i = 0; i < count; i++)
        {
            int codePoint = text[i];
            int script = ScriptData.ScriptOf(codePoint);
            // Unknown (unassigned, private use, noncharacters) has no glyph to shape: it joins the run it is in, like a neutral.
            if (script == ScriptData.Common || script == ScriptData.Inherited || script == ScriptData.Unknown)
            {
                resolved[i] = current;
                if (script != ScriptData.Common)
                {
                    continue;
                }

                BracketType bracket = BidiData.BracketTypeOf(codePoint);
                if (bracket == BracketType.Open && brackets.Count < MaxBracketDepth)
                {
                    brackets.Add((BidiData.CanonicalBracket(BidiData.PairedBracket(codePoint)), current));
                }
                else if (bracket == BracketType.Close)
                {
                    int canonical = BidiData.CanonicalBracket(codePoint);
                    for (int b = brackets.Count - 1; b >= 0; b--)
                    {
                        if (brackets[b].Closing == canonical)
                        {
                            // The bracket is in the script it was opened in, and the text after it returns to that script.
                            resolved[i] = brackets[b].Script != Pending ? brackets[b].Script : current;
                            if (brackets[b].Script != Pending)
                            {
                                current = brackets[b].Script;
                            }

                            brackets.RemoveRange(b, brackets.Count - b);
                            break;
                        }
                    }
                }

                continue;
            }

            // A real script. A character used with several scripts stays in the run it is in when that script is one of them.
            int chosen = script;
            if (current != Pending && Contains(ScriptData.ExtensionsOf(codePoint), current))
            {
                chosen = current;
            }

            resolved[i] = chosen;
            if (chosen != current)
            {
                if (current == Pending)
                {
                    // Brackets opened before the first real script belong to it, so that their closers return to it.
                    for (int b = 0; b < brackets.Count; b++)
                    {
                        if (brackets[b].Script == Pending)
                        {
                            brackets[b] = (brackets[b].Closing, chosen);
                        }
                    }
                }

                current = chosen;
                for (int j = i - 1; j >= 0 && resolved[j] == Pending; j--)
                {
                    resolved[j] = chosen;
                }
            }
        }

        int start = 0;
        for (int i = 1; i <= count; i++)
        {
            int previous = resolved[i - 1] == Pending ? ScriptData.Common : resolved[i - 1];
            int here = i == count ? -2 : resolved[i] == Pending ? ScriptData.Common : resolved[i];
            if (here != previous)
            {
                runs.Add((start, i - start, previous));
                start = i;
            }
        }

        return runs;
    }

    private static bool Contains(ReadOnlySpan<byte> scripts, int script)
    {
        foreach (byte candidate in scripts)
        {
            if (candidate == script)
            {
                return true;
            }
        }

        return false;
    }
}
