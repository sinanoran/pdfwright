namespace Pdfwright.Unicode;

/// <summary>What <see cref="UnicodeBidi.Resolve"/> found for one paragraph.</summary>
internal sealed class BidiResult
{
    internal BidiResult(byte paragraphLevel, byte[] levels, BidiClass[] classes)
    {
        ParagraphLevel = paragraphLevel;
        Levels = levels;
        Classes = classes;
    }

    /// <summary>The paragraph embedding level: 0 for left-to-right, 1 for right-to-left.</summary>
    /// <remarks>
    /// <see cref="Levels"/> is shared: <see cref="UnicodeBidi.ApplyL1"/> changes the array it is given in place, so a layout that
    /// breaks the paragraph into lines takes each line's levels from <see cref="UnicodeBidi.LineLevels"/>, which copies.
    /// </remarks>
    internal byte ParagraphLevel { get; }

    /// <summary>
    /// The resolved embedding level of each character, with rule L1 applied as if the paragraph were one line. A character that
    /// rule X9 removes (an explicit embedding, override or isolate-less formatting mark, or a boundary neutral) takes the level of the
    /// character before it, as the standard suggests (section 5.2), so it never starts or splits a run.
    /// </summary>
    internal byte[] Levels { get; }

    /// <summary>The original bidirectional class of each character, before any rule changed it.</summary>
    internal BidiClass[] Classes { get; }
}

/// <summary>
/// The Unicode Bidirectional Algorithm (UAX #9, Unicode 18.0): the levels of a paragraph (rules P2 to I2 and L1) and the visual order of
/// a line (L2). Written from the standard and held to <c>BidiTest.txt</c> and <c>BidiCharacterTest.txt</c> by the tests.
/// </summary>
internal static class UnicodeBidi
{
    private const int MaxDepth = 125;
    private const int MaxBracketDepth = 63;

    /// <summary>The paragraph level is found from the text (rules P2 and P3).</summary>
    internal const int AutoLevel = -1;

    private enum Override : byte
    {
        Neutral,
        Left,
        Right,
    }

    /// <summary>
    /// The levels of one paragraph of <paramref name="text"/>, given as Unicode code points (not UTF-16 code units: a supplementary
    /// character passed as two surrogates is two characters of class L). <paramref name="paragraphLevel"/> is 0 (left to right),
    /// 1 (right to left) or <see cref="AutoLevel"/>. A paragraph separator (class B) may only be the last character: the standard
    /// splits paragraphs first (P1), and rules P2, BD9, X5c and X10 would otherwise reach across the separator, so text that holds
    /// one earlier is refused. Split it with <see cref="Paragraphs"/>. A carriage return and line feed that are the last two characters are
    /// one separator and are accepted, as <see cref="Paragraphs"/> returns them.
    /// </summary>
    /// <exception cref="ArgumentException">A paragraph separator is not the last character.</exception>
    internal static BidiResult Resolve(ReadOnlySpan<int> text, int paragraphLevel = AutoLevel)
    {
        if (paragraphLevel is < AutoLevel or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(paragraphLevel), paragraphLevel, "The paragraph level is 0, 1 or AutoLevel (-1).");
        }

        int count = text.Length;
        BidiClass[] initial = new BidiClass[count];
        for (int i = 0; i < count; i++)
        {
            initial[i] = BidiData.ClassOf(text[i]);
            // A carriage return that is followed by the line feed that ends the text is one separator with it (the standard keeps CR LF together, as Paragraphs does).
            bool endsWithCrLf = text[i] == 0x0D && i == count - 2 && text[i + 1] == 0x0A;
            if (initial[i] == BidiClass.B && i < count - 1 && !endsWithCrLf)
            {
                throw new ArgumentException($"A paragraph separator (U+{text[i]:X4}) at index {i} is not the last character: split the text with UnicodeBidi.Paragraphs first.", nameof(text));
            }
        }

        (int[] matchingPdi, int[] matchingInitiator) = MatchIsolates(initial);
        byte level = paragraphLevel is 0 or 1 ? (byte)paragraphLevel : FirstStrongLevel(initial, matchingPdi, 0, count, 0);

        BidiClass[] types = (BidiClass[])initial.Clone();
        byte[] levels = new byte[count];
        ExplicitLevels(initial, types, levels, matchingPdi, level);

        // X9: what rule X9 removes takes no part in the rest.
        List<int> kept = [];
        for (int i = 0; i < count; i++)
        {
            if (!IsRemovedByX9(initial[i]))
            {
                kept.Add(i);
            }
        }

        // X10: level runs, then isolating run sequences.
        List<(int First, int Last)> runs = [];
        int[] runOf = new int[count];
        for (int k = 0; k < kept.Count;)
        {
            int start = k;
            while (k + 1 < kept.Count && levels[kept[k + 1]] == levels[kept[start]])
            {
                k++;
            }

            for (int m = start; m <= k; m++)
            {
                runOf[kept[m]] = runs.Count;
            }

            runs.Add((start, k));
            k++;
        }

        List<Sequence> sequences = [];
        for (int r = 0; r < runs.Count; r++)
        {
            int firstChar = kept[runs[r].First];
            if (initial[firstChar] == BidiClass.PDI && matchingInitiator[firstChar] != -1)
            {
                continue;
            }

            List<int> chars = [];
            int run = r;
            while (true)
            {
                for (int m = runs[run].First; m <= runs[run].Last; m++)
                {
                    chars.Add(kept[m]);
                }

                int lastChar = chars[^1];
                if (IsIsolateInitiator(initial[lastChar]) && matchingPdi[lastChar] != -1 && matchingPdi[lastChar] < count)
                {
                    int next = runOf[matchingPdi[lastChar]];
                    if (next <= run)
                    {
                        break;
                    }

                    run = next;
                    continue;
                }

                break;
            }

            sequences.Add(MakeSequence([.. chars], initial, levels, count, level));
        }

        byte[] resolved = (byte[])levels.Clone();
        foreach (Sequence sequence in sequences)
        {
            ResolveSequence(sequence, text, initial, types, resolved);
        }

        // What X9 removed takes the level of the character before it (or the paragraph's).
        for (int i = 0; i < count; i++)
        {
            if (IsRemovedByX9(initial[i]))
            {
                resolved[i] = i > 0 ? resolved[i - 1] : level;
            }
        }

        ApplyL1(resolved, initial, 0, count, level);
        return new BidiResult(level, resolved, initial);
    }

    /// <summary>
    /// Rule P1: where the paragraphs of <paramref name="text"/> are. Each paragraph keeps its separator as its last character, and a
    /// carriage return followed by a line feed is one separator.
    /// </summary>
    internal static List<(int Start, int Length)> Paragraphs(ReadOnlySpan<int> text)
    {
        List<(int Start, int Length)> paragraphs = [];
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (BidiData.ClassOf(text[i]) != BidiClass.B)
            {
                continue;
            }

            if (text[i] == 0x0D && i + 1 < text.Length && text[i + 1] == 0x0A)
            {
                i++;
            }

            paragraphs.Add((start, i + 1 - start));
            start = i + 1;
        }

        if (start < text.Length)
        {
            paragraphs.Add((start, text.Length - start));
        }

        return paragraphs;
    }

    /// <summary>
    /// The levels of the line [<paramref name="start"/>, <paramref name="end"/>) of a resolved paragraph, as a new array indexed from 0:
    /// rule L1 applied to that line. A line that is not the last of the paragraph ends in whitespace that the paragraph-wide L1 of
    /// <see cref="Resolve"/> left at its inner level; call this, then <see cref="VisualOrder"/> on the result.
    /// </summary>
    internal static byte[] LineLevels(BidiResult paragraph, int start, int end)
    {
        ArgumentNullException.ThrowIfNull(paragraph);
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)start, (uint)end, nameof(start));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(end, paragraph.Levels.Length, nameof(end));
        byte[] levels = paragraph.Levels[start..end];
        ApplyL1(levels, paragraph.Classes[start..end], 0, levels.Length, paragraph.ParagraphLevel);
        return levels;
    }

    /// <summary>
    /// The same as <see cref="LineLevels"/>, written into the first <c>end - start</c> entries of <paramref name="levels"/> instead of a new array, so that a layout that builds many lines allocates none for their levels.
    /// </summary>
    internal static void LineLevelsInto(BidiResult paragraph, int start, int end, Span<byte> levels)
    {
        ArgumentNullException.ThrowIfNull(paragraph);
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)start, (uint)end, nameof(start));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(end, paragraph.Levels.Length, nameof(end));
        int count = end - start;
        ArgumentOutOfRangeException.ThrowIfLessThan(levels.Length, count, nameof(levels));
        levels = levels[..count];
        paragraph.Levels.AsSpan(start, count).CopyTo(levels);
        ApplyL1Core(levels, paragraph.Classes.AsSpan(start, count), 0, count, paragraph.ParagraphLevel);
    }

    /// <summary>
    /// Rule L1 for the line [<paramref name="start"/>, <paramref name="end"/>) of <paramref name="levels"/>, in place: segment and
    /// paragraph separators, and whitespace before them or at the end of the line, return to the paragraph level. It only ever
    /// sets a level to the paragraph level, so applying it again to a level array that already had it applied changes nothing.
    /// </summary>
    internal static void ApplyL1(byte[] levels, BidiClass[] classes, int start, int end, byte paragraphLevel) => ApplyL1Core(levels.AsSpan(), classes.AsSpan(), start, end, paragraphLevel);

    /// <summary>The same as the array form, on spans (a line's levels need not be an array).</summary>
    internal static void ApplyL1Core(Span<byte> levels, ReadOnlySpan<BidiClass> classes, int start, int end, byte paragraphLevel)
    {
        for (int i = start; i < end; i++)
        {
            if (classes[i] is BidiClass.B or BidiClass.S)
            {
                levels[i] = paragraphLevel;
                for (int j = i - 1; j >= start && IsWhitespaceForL1(classes[j]); j--)
                {
                    levels[j] = paragraphLevel;
                }
            }
        }

        for (int j = end - 1; j >= start && IsWhitespaceForL1(classes[j]); j--)
        {
            levels[j] = paragraphLevel;
        }
    }

    /// <summary>
    /// Rule L2 for the line [<paramref name="start"/>, <paramref name="end"/>) of <paramref name="levels"/>: the indices of its
    /// characters in visual order, left to right. The levels must already have L1 applied to this line (see <see cref="LineLevels"/>).
    /// </summary>
    internal static int[] VisualOrder(ReadOnlySpan<byte> levels, int start, int end)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfLessThan(end, start);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(end, levels.Length);
        int[] order = new int[end - start];
        for (int i = 0; i < order.Length; i++)
        {
            order[i] = start + i;
        }

        byte highest = 0;
        byte lowestOdd = byte.MaxValue;
        for (int i = start; i < end; i++)
        {
            highest = Math.Max(highest, levels[i]);
            if ((levels[i] & 1) == 1)
            {
                lowestOdd = Math.Min(lowestOdd, levels[i]);
            }
        }

        for (int level = highest; level >= lowestOdd && level > 0; level--)
        {
            for (int i = 0; i < order.Length;)
            {
                if (levels[order[i]] < level)
                {
                    i++;
                    continue;
                }

                int runEnd = i;
                while (runEnd + 1 < order.Length && levels[order[runEnd + 1]] >= level)
                {
                    runEnd++;
                }

                Array.Reverse(order, i, runEnd - i + 1);
                i = runEnd + 1;
            }
        }

        return order;
    }

    internal static bool IsRemovedByX9(BidiClass type) =>
        type is BidiClass.LRE or BidiClass.RLE or BidiClass.LRO or BidiClass.RLO or BidiClass.PDF or BidiClass.BN;

    private static bool IsIsolateInitiator(BidiClass type) => type is BidiClass.LRI or BidiClass.RLI or BidiClass.FSI;

    private static bool IsWhitespaceForL1(BidiClass type) =>
        type is BidiClass.WS or BidiClass.LRI or BidiClass.RLI or BidiClass.FSI or BidiClass.PDI || IsRemovedByX9(type);

    /// <summary>BD9: the matching PDI of each isolate initiator and the matching initiator of each PDI (-1 when there is none).</summary>
    private static (int[] MatchingPdi, int[] MatchingInitiator) MatchIsolates(BidiClass[] types)
    {
        int[] pdi = new int[types.Length];
        int[] initiator = new int[types.Length];
        Array.Fill(pdi, -1);
        Array.Fill(initiator, -1);
        Stack<int> open = new();
        for (int i = 0; i < types.Length; i++)
        {
            if (IsIsolateInitiator(types[i]))
            {
                open.Push(i);
            }
            else if (types[i] == BidiClass.PDI && open.Count > 0)
            {
                int from = open.Pop();
                pdi[from] = i;
                initiator[i] = from;
            }
        }

        return (pdi, initiator);
    }

    /// <summary>Rules P2 and P3 over [<paramref name="start"/>, <paramref name="end"/>): the level of the first strong character outside isolates, or the default.</summary>
    private static byte FirstStrongLevel(BidiClass[] types, int[] matchingPdi, int start, int end, byte defaultLevel)
    {
        for (int i = start; i < end; i++)
        {
            BidiClass type = types[i];
            if (type == BidiClass.L)
            {
                return 0;
            }

            if (type is BidiClass.R or BidiClass.AL)
            {
                return 1;
            }

            if (IsIsolateInitiator(type))
            {
                if (matchingPdi[i] == -1)
                {
                    return defaultLevel;
                }

                i = matchingPdi[i];
            }
        }

        return defaultLevel;
    }

    /// <summary>Rules X1 to X8: the explicit embedding level of each character, and the overrides they apply to the types.</summary>
    private static void ExplicitLevels(BidiClass[] initial, BidiClass[] types, byte[] levels, int[] matchingPdi, byte paragraphLevel)
    {
        byte[] stackLevel = new byte[MaxDepth + 2];
        Override[] stackOverride = new Override[MaxDepth + 2];
        bool[] stackIsolate = new bool[MaxDepth + 2];
        int size = 0;
        int overflowIsolates = 0;
        int overflowEmbeddings = 0;
        int validIsolates = 0;

        void Reset()
        {
            size = 0;
            stackLevel[0] = paragraphLevel;
            stackOverride[0] = Override.Neutral;
            stackIsolate[0] = false;
            size = 1;
            overflowIsolates = 0;
            overflowEmbeddings = 0;
            validIsolates = 0;
        }

        Reset();
        for (int i = 0; i < initial.Length; i++)
        {
            BidiClass type = initial[i];
            int top = size - 1;
            switch (type)
            {
                case BidiClass.RLE:
                case BidiClass.LRE:
                case BidiClass.RLO:
                case BidiClass.LRO:
                {
                    levels[i] = stackLevel[top];
                    bool rtl = type is BidiClass.RLE or BidiClass.RLO;
                    int next = NextLevel(stackLevel[top], rtl);
                    if (next <= MaxDepth && overflowIsolates == 0 && overflowEmbeddings == 0)
                    {
                        stackLevel[size] = (byte)next;
                        stackOverride[size] = type == BidiClass.RLO ? Override.Right : type == BidiClass.LRO ? Override.Left : Override.Neutral;
                        stackIsolate[size] = false;
                        size++;
                    }
                    else if (overflowIsolates == 0)
                    {
                        overflowEmbeddings++;
                    }

                    break;
                }

                case BidiClass.RLI:
                case BidiClass.LRI:
                case BidiClass.FSI:
                {
                    levels[i] = stackLevel[top];
                    ApplyOverride(types, i, stackOverride[top]);
                    bool rtl = type == BidiClass.RLI;
                    if (type == BidiClass.FSI)
                    {
                        int end = matchingPdi[i] == -1 ? initial.Length : matchingPdi[i];
                        rtl = FirstStrongLevel(initial, matchingPdi, i + 1, end, 0) == 1;
                    }

                    int next = NextLevel(stackLevel[top], rtl);
                    if (next <= MaxDepth && overflowIsolates == 0 && overflowEmbeddings == 0)
                    {
                        validIsolates++;
                        stackLevel[size] = (byte)next;
                        stackOverride[size] = Override.Neutral;
                        stackIsolate[size] = true;
                        size++;
                    }
                    else
                    {
                        overflowIsolates++;
                    }

                    break;
                }

                case BidiClass.PDI:
                {
                    if (overflowIsolates > 0)
                    {
                        overflowIsolates--;
                    }
                    else if (validIsolates > 0)
                    {
                        overflowEmbeddings = 0;
                        while (!stackIsolate[size - 1])
                        {
                            size--;
                        }

                        size--;
                        validIsolates--;
                    }

                    top = size - 1;
                    levels[i] = stackLevel[top];
                    ApplyOverride(types, i, stackOverride[top]);
                    break;
                }

                case BidiClass.PDF:
                {
                    levels[i] = stackLevel[top];
                    if (overflowIsolates > 0)
                    {
                        break;
                    }

                    if (overflowEmbeddings > 0)
                    {
                        overflowEmbeddings--;
                    }
                    else if (!stackIsolate[top] && size >= 2)
                    {
                        size--;
                    }

                    break;
                }

                case BidiClass.B:
                    levels[i] = paragraphLevel;
                    Reset();
                    break;

                case BidiClass.BN:
                    levels[i] = stackLevel[top];
                    break;

                default:
                    levels[i] = stackLevel[top];
                    ApplyOverride(types, i, stackOverride[top]);
                    break;
            }
        }
    }

    private static int NextLevel(byte level, bool rightToLeft) =>
        rightToLeft ? (level + 1) | 1 : (level + 2) & ~1;

    private static void ApplyOverride(BidiClass[] types, int index, Override value)
    {
        if (value == Override.Left)
        {
            types[index] = BidiClass.L;
        }
        else if (value == Override.Right)
        {
            types[index] = BidiClass.R;
        }
    }

    /// <summary>An isolating run sequence (BD13) with the start-of-sequence and end-of-sequence types of X10.</summary>
    private sealed class Sequence
    {
        internal required int[] Indices { get; init; }

        internal required byte Level { get; init; }

        internal required BidiClass StartOfSequence { get; init; }

        internal required BidiClass EndOfSequence { get; init; }
    }

    private static Sequence MakeSequence(int[] indices, BidiClass[] initial, byte[] levels, int count, byte paragraphLevel)
    {
        byte level = levels[indices[0]];
        int before = indices[0] - 1;
        while (before >= 0 && IsRemovedByX9(initial[before]))
        {
            before--;
        }

        byte previousLevel = before >= 0 ? levels[before] : paragraphLevel;

        byte nextLevel;
        int last = indices[^1];
        if (IsIsolateInitiator(initial[last]))
        {
            nextLevel = paragraphLevel;
        }
        else
        {
            int after = last + 1;
            while (after < count && IsRemovedByX9(initial[after]))
            {
                after++;
            }

            nextLevel = after < count ? levels[after] : paragraphLevel;
        }

        return new Sequence
        {
            Indices = indices,
            Level = level,
            StartOfSequence = Math.Max(previousLevel, level) % 2 == 0 ? BidiClass.L : BidiClass.R,
            EndOfSequence = Math.Max(nextLevel, level) % 2 == 0 ? BidiClass.L : BidiClass.R,
        };
    }

    /// <summary>Rules W1 to W7, N0 to N2 and I1 to I2 over one isolating run sequence.</summary>
    private static void ResolveSequence(Sequence sequence, ReadOnlySpan<int> text, BidiClass[] initial, BidiClass[] types, byte[] resolved)
    {
        int[] indices = sequence.Indices;
        int length = indices.Length;
        BidiClass[] t = new BidiClass[length];
        for (int i = 0; i < length; i++)
        {
            t[i] = types[indices[i]];
        }

        BidiClass sos = sequence.StartOfSequence;
        BidiClass eos = sequence.EndOfSequence;

        // W1: a nonspacing mark takes the type of the character before it (ON after an isolate initiator or a PDI).
        BidiClass previous = sos;
        for (int i = 0; i < length; i++)
        {
            if (t[i] == BidiClass.NSM)
            {
                t[i] = previous;
            }
            else
            {
                previous = t[i] is BidiClass.LRI or BidiClass.RLI or BidiClass.FSI or BidiClass.PDI ? BidiClass.ON : t[i];
            }

        }

        // W2: a European number after an Arabic letter is an Arabic number.
        BidiClass lastStrong = sos;
        for (int i = 0; i < length; i++)
        {
            if (t[i] is BidiClass.L or BidiClass.R or BidiClass.AL)
            {
                lastStrong = t[i];
            }
            else if (t[i] == BidiClass.EN && lastStrong == BidiClass.AL)
            {
                t[i] = BidiClass.AN;
            }
        }

        // W3: Arabic letters are right-to-left.
        for (int i = 0; i < length; i++)
        {
            if (t[i] == BidiClass.AL)
            {
                t[i] = BidiClass.R;
            }
        }

        // W4: a single separator between two numbers of one kind joins them.
        for (int i = 1; i < length - 1; i++)
        {
            if (t[i] == BidiClass.ES && t[i - 1] == BidiClass.EN && t[i + 1] == BidiClass.EN)
            {
                t[i] = BidiClass.EN;
            }
            else if (t[i] == BidiClass.CS && t[i - 1] == t[i + 1] && t[i - 1] is BidiClass.EN or BidiClass.AN)
            {
                t[i] = t[i - 1];
            }
        }

        // W5: a run of terminators next to a European number becomes European numbers.
        for (int i = 0; i < length;)
        {
            if (t[i] != BidiClass.ET)
            {
                i++;
                continue;
            }

            int end = i;
            while (end + 1 < length && t[end + 1] == BidiClass.ET)
            {
                end++;
            }

            bool adjacent = (i > 0 && t[i - 1] == BidiClass.EN) || (end + 1 < length && t[end + 1] == BidiClass.EN);
            if (adjacent)
            {
                for (int k = i; k <= end; k++)
                {
                    t[k] = BidiClass.EN;
                }
            }

            i = end + 1;
        }

        // W6: the separators and terminators that are left are neutral.
        for (int i = 0; i < length; i++)
        {
            if (t[i] is BidiClass.ES or BidiClass.ET or BidiClass.CS)
            {
                t[i] = BidiClass.ON;
            }
        }

        // W7: a European number after a left-to-right letter is left-to-right.
        lastStrong = sos;
        for (int i = 0; i < length; i++)
        {
            if (t[i] is BidiClass.L or BidiClass.R)
            {
                lastStrong = t[i];
            }
            else if (t[i] == BidiClass.EN && lastStrong == BidiClass.L)
            {
                t[i] = BidiClass.L;
            }
        }

        BidiClass embedding = sequence.Level % 2 == 0 ? BidiClass.L : BidiClass.R;
        ResolveBrackets(sequence, text, initial, t, sos, embedding);

        // N1 and N2: a run of neutrals between two characters of one direction takes it, otherwise the embedding direction.
        for (int i = 0; i < length;)
        {
            if (!IsNeutralOrIsolate(t[i]))
            {
                i++;
                continue;
            }

            int end = i;
            while (end + 1 < length && IsNeutralOrIsolate(t[end + 1]))
            {
                end++;
            }

            BidiClass leading = i == 0 ? sos : StrongDirection(t[i - 1]);
            BidiClass trailing = end + 1 == length ? eos : StrongDirection(t[end + 1]);
            BidiClass direction = leading == trailing ? leading : embedding;
            for (int k = i; k <= end; k++)
            {
                t[k] = direction;
            }

            i = end + 1;
        }

        // I1 and I2: the implicit levels.
        for (int i = 0; i < length; i++)
        {
            int index = indices[i];
            if (sequence.Level % 2 == 0)
            {
                if (t[i] == BidiClass.R)
                {
                    resolved[index] = (byte)(sequence.Level + 1);
                }
                else if (t[i] is BidiClass.AN or BidiClass.EN)
                {
                    resolved[index] = (byte)(sequence.Level + 2);
                }
            }
            else if (t[i] is BidiClass.L or BidiClass.EN or BidiClass.AN)
            {
                resolved[index] = (byte)(sequence.Level + 1);
            }
        }
    }

    private static bool IsNeutralOrIsolate(BidiClass type) =>
        type is BidiClass.B or BidiClass.S or BidiClass.WS or BidiClass.ON or BidiClass.FSI or BidiClass.LRI or BidiClass.RLI or BidiClass.PDI;

    /// <summary>The direction a character counts as for the neutrals around it: numbers count as right-to-left.</summary>
    private static BidiClass StrongDirection(BidiClass type) => type == BidiClass.L ? BidiClass.L : BidiClass.R;

    /// <summary>The direction of a strong type for N0, or ON when the type is not strong (numbers count as right-to-left).</summary>
    private static BidiClass StrongOrNone(BidiClass type) => type switch
    {
        BidiClass.L => BidiClass.L,
        BidiClass.R or BidiClass.EN or BidiClass.AN => BidiClass.R,
        _ => BidiClass.ON,
    };

    /// <summary>Rule N0 (with BD14 to BD16): paired brackets take the direction of what they enclose.</summary>
    private static void ResolveBrackets(Sequence sequence, ReadOnlySpan<int> text, BidiClass[] initial, BidiClass[] t, BidiClass sos, BidiClass embedding)
    {
        int[] indices = sequence.Indices;
        int length = indices.Length;

        // BD16: find the pairs.
        List<(int Open, int Close)> pairs = [];
        List<(int Wanted, int Position)> stack = [];
        for (int i = 0; i < length; i++)
        {
            if (t[i] != BidiClass.ON)
            {
                continue;
            }

            int code = text[indices[i]];
            BracketType bracket = BidiData.BracketTypeOf(code);
            if (bracket == BracketType.Open)
            {
                if (stack.Count == MaxBracketDepth)
                {
                    break;
                }

                stack.Add((BidiData.CanonicalBracket(BidiData.PairedBracket(code)), i));
            }
            else if (bracket == BracketType.Close)
            {
                int canonical = BidiData.CanonicalBracket(code);
                for (int s = stack.Count - 1; s >= 0; s--)
                {
                    if (stack[s].Wanted == canonical)
                    {
                        pairs.Add((stack[s].Position, i));
                        stack.RemoveRange(s, stack.Count - s);
                        break;
                    }
                }
            }
        }

        pairs.Sort((a, b) => a.Open.CompareTo(b.Open));
        BidiClass opposite = embedding == BidiClass.L ? BidiClass.R : BidiClass.L;
        foreach ((int open, int close) in pairs)
        {
            bool foundEmbedding = false;
            bool foundOpposite = false;
            for (int k = open + 1; k < close; k++)
            {
                BidiClass direction = StrongOrNone(t[k]);
                if (direction == embedding)
                {
                    foundEmbedding = true;
                    break;
                }

                if (direction == opposite)
                {
                    foundOpposite = true;
                }
            }

            BidiClass? chosen = null;
            if (foundEmbedding)
            {
                chosen = embedding;
            }
            else if (foundOpposite)
            {
                BidiClass context = sos;
                for (int k = open - 1; k >= 0; k--)
                {
                    BidiClass direction = StrongOrNone(t[k]);
                    if (direction != BidiClass.ON)
                    {
                        context = direction;
                        break;
                    }
                }

                chosen = context == opposite ? opposite : embedding;
            }

            if (chosen is not { } value)
            {
                continue;
            }

            t[open] = value;
            t[close] = value;

            // Marks that followed a bracket take its new direction.
            for (int k = open + 1; k < length && initial[indices[k]] == BidiClass.NSM; k++)
            {
                t[k] = value;
            }

            for (int k = close + 1; k < length && initial[indices[k]] == BidiClass.NSM; k++)
            {
                t[k] = value;
            }
        }
    }
}
