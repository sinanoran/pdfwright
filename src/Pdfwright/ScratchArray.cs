using System.Runtime.CompilerServices;

namespace Pdfwright;

/// <summary>
/// The working arrays of a layout, kept for reuse by each thread when they are small and allocated when they are not. An array above <see cref="PoolLimit"/> is never kept, so no thread holds memory in
/// proportion to the longest text it ever laid out.
/// </summary>
/// <remarks>
/// <para>
/// The arrays are kept here rather than in the framework's shared array pool: the pool is a generic class of the framework, and for an element type of this library (the classes of the Unicode
/// algorithms) or one the framework does not pool itself (<see cref="double"/>) its code was compiled at run time on the first save of a process even in a ReadyToRun build, about forty methods in all.
/// The code here is the library's own and is compiled ahead of time with it.
/// </para>
/// <para>
/// What a thread keeps is never trimmed, only dropped with the thread, so it is bounded tightly. For each element type it keeps at most <see cref="SlotsPerSize"/> arrays of each size (the powers of two
/// from 16 to <see cref="PoolLimit"/> elements); of the sizes up to <see cref="LargestSmall"/> elements that is all (8 x 496 elements), and the larger sizes share at most <see cref="LargeBytesPerType"/>
/// bytes of elements (a reference counts as its eight bytes), so that the arrays of long texts never crowd out those of cells and lines; within that share the arrays given back last take the place of
/// those of other large sizes, so that what earlier texts left does not keep the arrays of the current work from being kept. The layout uses nine element types (<see cref="int"/>,
/// <see cref="double"/>, <see cref="bool"/>, <see cref="byte"/>, the shaper's words, the character properties and the three classes of the line breaker and the bidi algorithm), of 29 bytes together,
/// so a thread keeps at most 3,968 x 29 bytes (115 KB) of small arrays and 9 x 28 KB (252 KB) of large ones, about 367 KB, and the headers of at most 9 x 72 arrays of 24 bytes (about 15 KB). A thread that
/// has laid out only cells and lines of usual length keeps a few kilobytes.
/// </para>
/// </remarks>
internal static class ScratchArray
{
    /// <summary>
    /// The longest array, in elements, that is kept for reuse; a longer one is allocated and left to the garbage collector. The same limit as the shaper's reused buffer
    /// (<c>HarfBuzzShaper.ReusedBufferLimit</c>): a run of this many characters is far longer than a line, a table cell or most paragraphs, so the kept arrays serve the usual layout and no
    /// thread keeps memory in proportion to the longest text it ever laid out.
    /// </summary>
    internal const int PoolLimit = 4096;

    /// <summary>The longest of the small sizes, which are kept in all their slots; a larger array is kept only within <see cref="LargeBytesPerType"/>.</summary>
    internal const int LargestSmall = 256;

    /// <summary>
    /// The most bytes of elements of the sizes above <see cref="LargestSmall"/> that a thread keeps of one element type: an array given back that would pass it takes the place of kept arrays of the other
    /// large sizes (the largest first), and is dropped when the arrays of its own size alone leave no room. Every array of the layout of a
    /// paragraph of up to about a thousand characters fits (seven arrays of 1,024 <see cref="int"/> are 28 KB), so the usual paragraphs allocate none; a longer text keeps what fits and allocates the rest.
    /// </summary>
    internal const int LargeBytesPerType = 28 * 1024;

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
                    if (kept.Length > LargestSmall)
                    {
                        Kept<T>.Bytes -= BytesOf<T>(kept.Length);
                    }

                    return kept;
                }
            }
        }

        return new T[ShortestLength << size];
    }

    /// <summary>
    /// Gives back an array that <see cref="Rent{T}"/> returned. An array above <see cref="PoolLimit"/> was allocated and is just dropped, as is one when the thread keeps all the slots of its size, or,
    /// for a large size, when the arrays of its own size leave it no room within <see cref="LargeBytesPerType"/> (kept arrays of other large sizes make room for it); the caller must not use any array
    /// after returning it.
    /// </summary>
    internal static void Return<T>(T[] array)
    {
        ArgumentNullException.ThrowIfNull(array);
        int length = array.Length;
        if (length < ShortestLength || length > PoolLimit || (length & (length - 1)) != 0)
        {
            return; // not one of the sizes kept: one allocated for a long text, or the empty array
        }

        int bytes = length > LargestSmall ? BytesOf<T>(length) : 0;
        if (bytes > LargeBytesPerType)
        {
            return; // larger than all a thread may keep of the large arrays of this type
        }

        T[]?[] slots = Kept<T>.Slots ??= new T[]?[Sizes * SlotsPerSize];
        int size = SizeOf(length);
        int first = size * SlotsPerSize;
        for (int i = first; i < first + SlotsPerSize; i++)
        {
            if (slots[i] is null)
            {
                // The arrays of the other large sizes make room for this one, the largest first: they were left by earlier texts, and the array given back now is the one the current work uses. Kept
                // as they were, they would keep this size from being kept at all, and every paragraph of this length would allocate its arrays again.
                if (Kept<T>.Bytes + bytes > LargeBytesPerType && !MakeRoom(slots, size, bytes))
                {
                    return; // the arrays of this size alone are as much as the thread may keep
                }

                slots[i] = array;
                Kept<T>.Bytes += bytes;
                return;
            }
        }
    }

    /// <summary>
    /// Drops kept arrays of the large sizes other than <paramref name="size"/>, the largest first, until <paramref name="bytes"/> more fit within <see cref="LargeBytesPerType"/>. Whether they fit; when the
    /// arrays of <paramref name="size"/> itself leave no room, nothing is dropped.
    /// </summary>
    private static bool MakeRoom<T>(T[]?[] slots, int size, int bytes)
    {
        int ownBytes = 0;
        for (int i = size * SlotsPerSize; i < (size + 1) * SlotsPerSize; i++)
        {
            ownBytes += slots[i] is { } own ? BytesOf<T>(own.Length) : 0;
        }

        if (ownBytes + bytes > LargeBytesPerType)
        {
            return false;
        }

        for (int other = Sizes - 1; other > SizeOf(LargestSmall); other--)
        {
            if (other == size)
            {
                continue;
            }

            for (int i = other * SlotsPerSize; i < (other + 1) * SlotsPerSize; i++)
            {
                if (slots[i] is { } kept)
                {
                    slots[i] = null;
                    Kept<T>.Bytes -= BytesOf<T>(kept.Length);
                    if (Kept<T>.Bytes + bytes <= LargeBytesPerType)
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    /// <summary>How many arrays of one element type, of the size that holds <paramref name="length"/> elements, the current thread keeps now. For tests.</summary>
    internal static int KeptCount<T>(int length)
    {
        if (Kept<T>.Slots is not { } slots || length < 1 || length > PoolLimit)
        {
            return 0;
        }

        int first = SizeOf(length) * SlotsPerSize;
        int kept = 0;
        for (int i = first; i < first + SlotsPerSize; i++)
        {
            kept += slots[i] is null ? 0 : 1;
        }

        return kept;
    }

    /// <summary>How many bytes of elements of one type, in arrays above <see cref="LargestSmall"/> elements, the current thread keeps now. For tests.</summary>
    internal static int KeptBytes<T>() => Kept<T>.Bytes;

    /// <summary>The index of the size of array that holds <paramref name="length"/> elements (1 to <see cref="PoolLimit"/>): 0 for up to 16, 1 for up to 32, and so on.</summary>
    private static int SizeOf(int length) => Math.Max(0, System.Numerics.BitOperations.Log2((uint)length - 1) - 3);

    /// <summary>The bytes of the elements of an array of <paramref name="length"/> elements (a reference is the size of a pointer).</summary>
    private static int BytesOf<T>(int length) => length * Unsafe.SizeOf<T>();

    /// <summary>The arrays of one element type that the current thread keeps (<see cref="SlotsPerSize"/> slots for each size, the smallest size first) and the bytes of the elements of the large ones.</summary>
    private static class Kept<T>
    {
        [ThreadStatic]
        internal static T[]?[]? Slots;

        [ThreadStatic]
        internal static int Bytes;
    }
}
