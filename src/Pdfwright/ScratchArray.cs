namespace Pdfwright;

/// <summary>
/// The working arrays of a layout, kept for reuse by each thread when they are small and allocated when they are not. An array above <see cref="PoolLimit"/> is never kept, so no thread holds memory in
/// proportion to the longest text it ever laid out.
/// </summary>
/// <remarks>
/// The arrays are kept here rather than in the framework's shared array pool: the pool is a generic class of the framework, and for an element type of this library (the classes of the Unicode
/// algorithms) or one the framework does not pool itself (<see cref="double"/>) its code was compiled at run time on the first save of a process even in a ReadyToRun build, about forty methods in all.
/// The code here is the library's own and is compiled ahead of time with it. Each thread keeps at most <see cref="SlotsPerSize"/> arrays of each size (the powers of two from 16 to <see cref="PoolLimit"/>
/// elements) of each element type, which for the short texts of cells and lines is a few kilobytes; what a thread keeps goes with the thread.
/// </remarks>
internal static class ScratchArray
{
    /// <summary>
    /// The longest array, in elements, that is kept for reuse; a longer one is allocated and left to the garbage collector. The same limit as the shaper's reused buffer
    /// (<c>HarfBuzzShaper.ReusedBufferLimit</c>): a run of this many characters is far longer than a line, a table cell or most paragraphs, so the kept arrays serve the usual layout and no
    /// thread keeps memory in proportion to the longest text it ever laid out.
    /// </summary>
    internal const int PoolLimit = 4096;

    /// <summary>The shortest array that is handed out (an array for fewer elements is this long), as the framework's pool does.</summary>
    private const int ShortestLength = 16;

    /// <summary>The number of sizes kept: the powers of two from <see cref="ShortestLength"/> to <see cref="PoolLimit"/>.</summary>
    private const int Sizes = 9;

    /// <summary>
    /// The most arrays of one size and element type a thread keeps. A layout holds several of one type at once (the shaper's arrays of code points, indices, scripts, styles and fonts, and the line breaker's, are all of
    /// <see cref="int"/> and about the length of the text), and an array given back when the slots of its size are full is dropped, so this is enough for every array of a layout to be reused.
    /// </summary>
    private const int SlotsPerSize = 8;

    /// <summary>An array of at least <paramref name="minimumLength"/> elements holding anything (a reused one holds what its last user left), or one of exactly that length, all zero, when that is above <see cref="PoolLimit"/>.</summary>
    internal static T[] Rent<T>(int minimumLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(minimumLength);
        if (minimumLength > PoolLimit)
        {
            return new T[minimumLength];
        }

        if (minimumLength == 0)
        {
            return [];
        }

        int size = SizeOf(minimumLength);
        if (Kept<T>.Slots is { } slots)
        {
            // The array given back last is taken first, which is the one most likely still in the processor's cache.
            int first = size * SlotsPerSize;
            for (int i = first + SlotsPerSize - 1; i >= first; i--)
            {
                if (slots[i] is { } kept)
                {
                    slots[i] = null;
                    return kept;
                }
            }
        }

        return new T[ShortestLength << size];
    }

    /// <summary>Gives back an array that <see cref="Rent{T}"/> returned. An array above <see cref="PoolLimit"/> was allocated and is just dropped, as is one when the thread keeps enough of its size; the caller must not use any array after returning it.</summary>
    internal static void Return<T>(T[] array)
    {
        ArgumentNullException.ThrowIfNull(array);
        int length = array.Length;
        if (length < ShortestLength || length > PoolLimit || (length & (length - 1)) != 0)
        {
            return; // not one of the sizes kept: one allocated for a long text, or the empty array
        }

        T[]?[] slots = Kept<T>.Slots ??= new T[]?[Sizes * SlotsPerSize];
        int first = SizeOf(length) * SlotsPerSize;
        for (int i = first; i < first + SlotsPerSize; i++)
        {
            if (slots[i] is null)
            {
                slots[i] = array;
                return;
            }
        }
    }

    /// <summary>The index of the size of array that holds <paramref name="length"/> elements (1 to <see cref="PoolLimit"/>): 0 for up to 16, 1 for up to 32, and so on.</summary>
    private static int SizeOf(int length) => Math.Max(0, System.Numerics.BitOperations.Log2((uint)length - 1) - 3);

    /// <summary>The arrays of one element type that the current thread keeps: <see cref="SlotsPerSize"/> slots for each size, the smallest size first.</summary>
    private static class Kept<T>
    {
        [ThreadStatic]
        internal static T[]?[]? Slots;
    }
}
