using System.Text;

namespace Pdfwright.Pdf;

/// <summary>Writes PDF objects as the bytes the file format defines.</summary>
internal static class PdfSerializer
{
    private static readonly byte[] Newline = [(byte)'\n'];

    /// <summary>The characters <see cref="IsRegular"/> accepts, for the search of a name that has none other.</summary>
    private static readonly System.Buffers.SearchValues<char> RegularNameCharacters =
        System.Buffers.SearchValues.Create([.. Enumerable.Range('!', 0x7F - '!').Select(c => (char)c).Where(c => IsRegular((byte)c))]);

    internal static void Write(PdfObject value, Stream output)
    {
        switch (value)
        {
            case PdfNull:
                Ascii(output, "null");
                break;
            case PdfBoolean boolean:
                Ascii(output, boolean.Value ? "true" : "false");
                break;
            case PdfInteger integer:
                Integer(output, integer.Value);
                break;
            case PdfReal real:
                Ascii(output, PdfReal.Format(real.Value));
                break;
            case PdfName name:
                WriteName(name.Value, output);
                break;
            case PdfString text:
                WriteString(text, output);
                break;
            case PdfArray array:
                Ascii(output, "[");
                for (int i = 0; i < array.Items.Count; i++)
                {
                    if (i > 0)
                    {
                        Ascii(output, " ");
                    }

                    Write(array.Items[i], output);
                }

                Ascii(output, "]");
                break;
            case PdfDictionary dictionary:
                WriteDictionary(dictionary, output);
                break;
            case PdfReference reference:
                Integer(output, reference.Number);
                output.Write(" 0 R"u8);
                break;
            case PdfStream:
                throw new InvalidOperationException("A stream is an indirect object: write it with PdfFile.Write.");
            default:
                throw new ArgumentOutOfRangeException(nameof(value), value.GetType().Name, "unknown PDF object");
        }
    }

    internal static void WriteDictionary(PdfDictionary dictionary, Stream output)
    {
        Ascii(output, "<<");
        foreach (KeyValuePair<string, PdfObject> entry in dictionary.Entries)
        {
            output.Write(Newline);
            WriteName(entry.Key, output);
            Ascii(output, " ");
            Write(entry.Value, output);
        }

        output.Write(Newline);
        Ascii(output, ">>");
    }

    /// <summary>
    /// A name (ISO 32000-1, 7.3.5): the regular printable characters as they are, everything else, the delimiters and
    /// <c>#</c> included, as <c>#xx</c> for each byte of its UTF-8 form.
    /// </summary>
    internal static void WriteName(string name, Stream output)
    {
        // Nearly every name is short and regular (/Type, /StructElem, /F1): written as it is, with nothing allocated.
        if (name.Length <= 127 && name.AsSpan().IndexOfAnyExcept(RegularNameCharacters) < 0)
        {
            Span<byte> plain = stackalloc byte[name.Length + 1];
            plain[0] = (byte)'/';
            for (int i = 0; i < name.Length; i++)
            {
                plain[i + 1] = (byte)name[i];
            }

            output.Write(plain);
            return;
        }

        StringBuilder text = new("/");
        foreach (byte b in Encoding.UTF8.GetBytes(name))
        {
            if (IsRegular(b))
            {
                text.Append((char)b);
            }
            else
            {
                text.Append('#').Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        Ascii(output, text.ToString());
    }

    /// <summary>A byte of a name that is written as it is: printable ASCII other than the delimiters and <c>#</c>.</summary>
    private static bool IsRegular(byte b) =>
        b is > (byte)' ' and < 0x7F
        && b is not ((byte)'#' or (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%');

    /// <summary>An integer in decimal, as <see cref="long.ToString(IFormatProvider)"/> writes it in the invariant culture, without making a string.</summary>
    internal static void Integer(Stream output, long value)
    {
        Span<byte> digits = stackalloc byte[20];
        _ = value.TryFormat(digits, out int written, default, System.Globalization.CultureInfo.InvariantCulture);
        output.Write(digits[..written]);
    }

    private static void WriteString(PdfString text, Stream output)
    {
        if (text.Hex)
        {
            Ascii(output, "<" + Convert.ToHexString(text.Bytes) + ">");
            return;
        }

        StringBuilder literal = new("(");
        foreach (byte b in text.Bytes)
        {
            switch (b)
            {
                case (byte)'\\':
                    literal.Append("\\\\");
                    break;
                case (byte)'(':
                    literal.Append("\\(");
                    break;
                case (byte)')':
                    literal.Append("\\)");
                    break;
                case (byte)'\n':
                    literal.Append("\\n");
                    break;
                case (byte)'\r':
                    literal.Append("\\r");
                    break;
                case < (byte)' ' or > (byte)'~':
                    literal.Append('\\').Append(Convert.ToString(b, 8).PadLeft(3, '0'));
                    break;
                default:
                    literal.Append((char)b);
                    break;
            }
        }

        literal.Append(')');
        Ascii(output, literal.ToString());
    }

    internal static void Ascii(Stream output, string text)
    {
        Span<byte> buffer = text.Length <= 256 ? stackalloc byte[text.Length] : new byte[text.Length];
        for (int i = 0; i < text.Length; i++)
        {
            buffer[i] = (byte)text[i];
        }

        output.Write(buffer);
    }
}
