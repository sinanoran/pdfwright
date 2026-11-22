using System.Globalization;
using System.Text;

namespace Pdfwright.Pdf;

/// <summary>
/// The objects of the PDF file format (ISO 32000-1, section 7.3), as the writer builds them. Internal: the public
/// API of the package is blocks and styles (section 5 of <c>docs/plan-0.1.md</c>), never PDF objects.
/// </summary>
internal abstract class PdfObject;

internal sealed class PdfNull : PdfObject
{
    internal static readonly PdfNull Instance = new();
}

internal sealed class PdfBoolean(bool value) : PdfObject
{
    internal static readonly PdfBoolean True = new(true);

    internal static readonly PdfBoolean False = new(false);

    internal bool Value { get; } = value;
}

internal sealed class PdfInteger(long value) : PdfObject
{
    internal long Value { get; } = value;
}

internal sealed class PdfReal : PdfObject
{
    internal PdfReal(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "A PDF number is finite.");
        }

        Value = value;
    }

    internal double Value { get; }

    /// <summary>
    /// A number as a content stream or an object writes it: invariant culture, at most five decimals, trailing zeros
    /// dropped, <b>never an exponent</b> (the format has none) and never "-0". A comma here would corrupt the file on a
    /// German machine, so every number goes through this one method.
    /// </summary>
    internal static string Format(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "A PDF number is finite.");
        }

        string text = value.ToString("0.#####", CultureInfo.InvariantCulture);
        return text == "-0" ? "0" : text;
    }

    /// <summary>
    /// A number with ten significant digits instead of five decimals, for the operands whose size says nothing about their importance (a
    /// matrix scaled by 1e-6 would be all zeros with <see cref="Format"/>). Still plain decimal text, never an exponent, never "-0"; a
    /// number below about 1e-20 in size is written as 0.
    /// </summary>
    internal static string FormatRelative(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "A PDF number is finite.");
        }

        if (value == 0)
        {
            return "0";
        }

        int decimals = Math.Clamp(9 - (int)Math.Floor(Math.Log10(Math.Abs(value))), 0, 20);
        string text = value.ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        if (text.Contains('.', StringComparison.Ordinal))
        {
            text = text.TrimEnd('0').TrimEnd('.');
        }

        return text is "-0" or "" ? "0" : text;
    }

    /// <summary>
    /// Writes <see cref="Format"/> of <paramref name="value"/> as ASCII into <paramref name="destination"/> without allocating, and returns the number of
    /// bytes written: the same text, byte for byte (a test compares the two over many doubles). The destination must hold at least 400 bytes.
    /// </summary>
    internal static int Write(double value, Span<byte> destination)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "A PDF number is finite.");
        }

        if (!value.TryFormat(destination, out int written, "0.#####", CultureInfo.InvariantCulture))
        {
            throw new ArgumentException("The destination is too small for the number.", nameof(destination));
        }

        if (written == 2 && destination[0] == (byte)'-' && destination[1] == (byte)'0')
        {
            destination[0] = (byte)'0';
            return 1;
        }

        return written;
    }

    private static readonly string[] FixedFormats = [.. Enumerable.Range(0, 21).Select(d => "F" + d.ToString(CultureInfo.InvariantCulture))];

    /// <summary>
    /// Writes <see cref="FormatRelative"/> of <paramref name="value"/> as ASCII into <paramref name="destination"/> without allocating, and returns the number of
    /// bytes written: the same text, byte for byte. The destination must hold at least 400 bytes.
    /// </summary>
    internal static int WriteRelative(double value, Span<byte> destination)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "A PDF number is finite.");
        }

        if (value == 0)
        {
            destination[0] = (byte)'0';
            return 1;
        }

        int decimals = Math.Clamp(9 - (int)Math.Floor(Math.Log10(Math.Abs(value))), 0, 20);
        if (!value.TryFormat(destination, out int written, FixedFormats[decimals], CultureInfo.InvariantCulture))
        {
            throw new ArgumentException("The destination is too small for the number.", nameof(destination));
        }

        if (destination[..written].Contains((byte)'.'))
        {
            while (written > 0 && destination[written - 1] == (byte)'0')
            {
                written--;
            }

            while (written > 0 && destination[written - 1] == (byte)'.')
            {
                written--;
            }
        }

        if (written == 0 || (written == 2 && destination[0] == (byte)'-' && destination[1] == (byte)'0'))
        {
            destination[0] = (byte)'0';
            return 1;
        }

        return written;
    }
}

internal sealed class PdfName(string value) : PdfObject
{
    internal string Value { get; } = value;
}

/// <summary>A string object: raw bytes, written as a literal or, for binary text, as hexadecimal.</summary>
internal sealed class PdfString(byte[] bytes, bool hex) : PdfObject
{
    internal byte[] Bytes { get; } = bytes;

    internal bool Hex { get; } = hex;

    /// <summary>
    /// A text string (ISO 32000-1, 7.9.2.2): printable ASCII as it is, anything else as UTF-16BE with a byte-order
    /// mark, which every reader understands and which carries any language.
    /// </summary>
    internal static PdfString Text(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.All(c => c is >= ' ' and <= '~'))
        {
            return new PdfString(Encoding.ASCII.GetBytes(text), hex: false);
        }

        byte[] body = Encoding.BigEndianUnicode.GetBytes(text);
        byte[] bytes = new byte[body.Length + 2];
        bytes[0] = 0xFE;
        bytes[1] = 0xFF;
        body.CopyTo(bytes, 2);
        return new PdfString(bytes, hex: true);
    }

    /// <summary>A date (ISO 32000-1, 7.9.4): <c>D:YYYYMMDDHHmmSSZ</c> in UTC, so that it needs no zone arithmetic to read.</summary>
    internal static PdfString Date(DateTimeOffset moment)
    {
        DateTimeOffset utc = moment.ToUniversalTime();
        return new PdfString(
            Encoding.ASCII.GetBytes(utc.ToString("'D:'yyyyMMddHHmmss'Z'", CultureInfo.InvariantCulture)),
            hex: false);
    }
}

internal sealed class PdfArray : PdfObject
{
    private readonly List<PdfObject> _items = [];

    internal PdfArray(params PdfObject[] items) => _items.AddRange(items);

    internal IReadOnlyList<PdfObject> Items => _items;

    internal PdfArray Add(PdfObject item)
    {
        _items.Add(item);
        return this;
    }

    internal static PdfArray Of(params double[] numbers) => new([.. numbers.Select(n => (PdfObject)Number(n))]);

    private static PdfObject Number(double value) =>
        value == Math.Floor(value) && Math.Abs(value) < 1e15 ? new PdfInteger((long)value) : new PdfReal(value);
}

/// <summary>A dictionary. Keys keep the order they were added in, so a file's bytes do not depend on a hash table.</summary>
internal sealed class PdfDictionary : PdfObject
{
    private readonly List<KeyValuePair<string, PdfObject>> _entries = [];

    internal IReadOnlyList<KeyValuePair<string, PdfObject>> Entries => _entries;

    internal PdfObject? this[string key] => _entries.FirstOrDefault(e => e.Key == key).Value;

    internal PdfDictionary Set(string key, PdfObject value)
    {
        int at = _entries.FindIndex(e => e.Key == key);
        if (at >= 0)
        {
            _entries[at] = new KeyValuePair<string, PdfObject>(key, value);
        }
        else
        {
            _entries.Add(new KeyValuePair<string, PdfObject>(key, value));
        }

        return this;
    }

    internal PdfDictionary Set(string key, string name) => Set(key, new PdfName(name));

    internal PdfDictionary Set(string key, long number) => Set(key, new PdfInteger(number));

    internal PdfDictionary Set(string key, bool value) => Set(key, value ? PdfBoolean.True : PdfBoolean.False);
}

/// <summary>An indirect reference to an object of the file, by number (generation is always 0: nothing is ever updated).</summary>
internal sealed class PdfReference(int number) : PdfObject
{
    internal int Number { get; } = number;
}

/// <summary>A stream object: a dictionary and bytes. The writer adds <c>/Length</c> and, when it compresses, <c>/Filter</c>.</summary>
internal sealed class PdfStream(PdfDictionary dictionary, ReadOnlyMemory<byte> data) : PdfObject
{
    internal PdfDictionary Dictionary { get; } = dictionary;

    internal ReadOnlyMemory<byte> Data { get; } = data;

    /// <summary>Keep the bytes as they are: a metadata stream must stay readable by tools that do not inflate.</summary>
    internal bool NeverCompress { get; init; }
}
