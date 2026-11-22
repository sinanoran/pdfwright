using System.Buffers;

namespace Pdfwright;

/// <summary>
/// The working arrays of a layout, rented from the shared array pool when they are small and allocated when they are not. The shared pool keeps what is returned to it until the runtime trims it
/// (at a full garbage collection), so an array sized for one very long text would stay in the process after the text is gone; an array above <see cref="PoolLimit"/> is therefore never pooled.
/// </summary>
internal static class ScratchArray
{
    /// <summary>
    /// The longest array, in elements, that is rented from the pool; a longer one is allocated and left to the garbage collector. The same limit as the shaper's reused buffer
    /// (<c>HarfBuzzShaper.ReusedBufferLimit</c>): a run of this many characters is far longer than a line, a table cell or most paragraphs, so the pool serves the usual layout and no
    /// thread keeps memory in proportion to the longest text it ever laid out.
    /// </summary>
    internal const int PoolLimit = 4096;

    /// <summary>An array of at least <paramref name="minimumLength"/> elements holding anything (a pooled one holds what its last user left), or one of exactly that length, all zero, when that is above <see cref="PoolLimit"/>.</summary>
    internal static T[] Rent<T>(int minimumLength) =>
        minimumLength <= PoolLimit ? ArrayPool<T>.Shared.Rent(minimumLength) : new T[minimumLength];

    /// <summary>Gives back an array that <see cref="Rent{T}"/> returned. An array above <see cref="PoolLimit"/> was allocated and is just dropped; the caller must not use any array after returning it.</summary>
    internal static void Return<T>(T[] array)
    {
        if (array.Length <= PoolLimit)
        {
            ArrayPool<T>.Shared.Return(array);
        }
    }
}
