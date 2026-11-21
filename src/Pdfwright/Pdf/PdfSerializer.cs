using System.Text;

namespace Pdfwright.Pdf;

/// <summary>Writes PDF objects as the bytes the file format defines.</summary>
internal static class PdfSerializer
{
    private static readonly byte[] Newline = [(byte)'\n'];

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
                Ascii(output, integer.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
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
                Ascii(output, reference.Number.ToString(System.Globalization.CultureInfo.InvariantCulture) + " 0 R");
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
        StringBuilder text = new("/");
        foreach (byte b in Encoding.UTF8.GetBytes(name))
        {
            bool regular = b is > (byte)' ' and < 0x7F
                && b is not ((byte)'#' or (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%');
            if (regular)
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
