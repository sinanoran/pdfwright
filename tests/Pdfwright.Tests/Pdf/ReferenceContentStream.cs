using System.Globalization;
using System.Text;

namespace Pdfwright.Tests.Pdf;

/// <summary>
/// The content stream writer as it was before it wrote straight into bytes: a string for each number and each operator, kept here as the reference
/// the new one is compared with. Nothing in the library uses it.
/// </summary>
internal sealed class ReferenceContentStream
{
    private readonly List<byte> _bytes = [];

    internal byte[] ToArray() => [.. _bytes];

    /// <summary><c>PdfReal.Format</c> as it was.</summary>
    internal static string Format(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "A PDF number is finite.");
        }

        string text = value.ToString("0.#####", CultureInfo.InvariantCulture);
        return text == "-0" ? "0" : text;
    }

    /// <summary><c>PdfReal.FormatRelative</c> as it was.</summary>
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

    internal ReferenceContentStream SaveState() => Op("q");

    internal ReferenceContentStream RestoreState() => Op("Q");

    internal ReferenceContentStream Transform(double a, double b, double c, double d, double e, double f)
    {
        Raw(string.Join(' ', new[] { a, b, c, d, e, f }.Select(FormatRelative)) + " cm\n");
        return this;
    }

    internal ReferenceContentStream LineWidth(double width) => Op("w", width);

    internal ReferenceContentStream LineCap(int style) => Op("J", style);

    internal ReferenceContentStream LineJoin(int style) => Op("j", style);

    internal ReferenceContentStream MiterLimit(double limit) => Op("M", limit);

    internal ReferenceContentStream Dash(double[] pattern, double phase)
    {
        Raw("[");
        for (int i = 0; i < pattern.Length; i++)
        {
            Raw((i > 0 ? " " : string.Empty) + Format(pattern[i]));
        }

        Raw("] " + Format(phase) + " d\n");
        return this;
    }

    internal ReferenceContentStream ExtGState(string name) => NameOp("gs", name);

    internal ReferenceContentStream FillGray(double gray) => Op("g", gray);

    internal ReferenceContentStream StrokeGray(double gray) => Op("G", gray);

    internal ReferenceContentStream FillRgb(double r, double g, double b) => Op("rg", r, g, b);

    internal ReferenceContentStream StrokeRgb(double r, double g, double b) => Op("RG", r, g, b);

    internal ReferenceContentStream MoveTo(double x, double y) => Op("m", x, y);

    internal ReferenceContentStream LineTo(double x, double y) => Op("l", x, y);

    internal ReferenceContentStream CurveTo(double x1, double y1, double x2, double y2, double x3, double y3) => Op("c", x1, y1, x2, y2, x3, y3);

    internal ReferenceContentStream Rectangle(double x, double y, double width, double height) => Op("re", x, y, width, height);

    internal ReferenceContentStream ClosePath() => Op("h");

    internal ReferenceContentStream Fill(bool evenOdd = false) => Op(evenOdd ? "f*" : "f");

    internal ReferenceContentStream Stroke() => Op("S");

    internal ReferenceContentStream FillAndStroke(bool evenOdd = false) => Op(evenOdd ? "B*" : "B");

    internal ReferenceContentStream Clip(bool evenOdd = false) => Op(evenOdd ? "W*" : "W").Op("n");

    internal ReferenceContentStream EndPath() => Op("n");

    internal ReferenceContentStream DrawXObject(string name) => NameOp("Do", name);

    internal ReferenceContentStream BeginText() => Op("BT");

    internal ReferenceContentStream EndText() => Op("ET");

    internal ReferenceContentStream Font(string name, double size)
    {
        Raw("/" + name + " " + Format(size) + " Tf\n");
        return this;
    }

    internal ReferenceContentStream TextMatrix(double a, double b, double c, double d, double e, double f) => Op("Tm", a, b, c, d, e, f);

    internal ReferenceContentStream TextMove(double x, double y) => Op("Td", x, y);

    internal ReferenceContentStream TextRenderMode(int mode) => Op("Tr", mode);

    internal ReferenceContentStream ShowHex(ReadOnlySpan<byte> codes)
    {
        Raw("<" + Convert.ToHexString(codes) + "> Tj\n");
        return this;
    }

    internal ReferenceContentStream ShowAdjusted(IEnumerable<(byte[] Codes, double? AdjustAfter)> parts)
    {
        Raw("[");
        foreach ((byte[] codes, double? adjust) in parts)
        {
            Raw("<" + Convert.ToHexString(codes) + ">");
            if (adjust is { } value)
            {
                Raw(Format(value));
            }
        }

        Raw("] TJ\n");
        return this;
    }

    internal ReferenceContentStream BeginMarkedContent(string tag, int mcid)
    {
        Raw("/" + tag + " <</MCID " + mcid.ToString(CultureInfo.InvariantCulture) + ">> BDC\n");
        return this;
    }

    internal ReferenceContentStream EndMarkedContent() => Op("EMC");

    internal ReferenceContentStream BeginActualText(string text)
    {
        byte[] utf16 = Encoding.BigEndianUnicode.GetBytes(text);
        Raw("/Span <</ActualText <FEFF" + Convert.ToHexString(utf16) + ">>> BDC\n");
        return this;
    }

    private ReferenceContentStream Op(string op, params double[] operands)
    {
        StringBuilder line = new();
        foreach (double operand in operands)
        {
            line.Append(Format(operand)).Append(' ');
        }

        line.Append(op).Append('\n');
        Raw(line.ToString());
        return this;
    }

    private ReferenceContentStream NameOp(string op, string name)
    {
        Raw("/" + name + " " + op + "\n");
        return this;
    }

    private void Raw(string text)
    {
        foreach (char c in text)
        {
            _bytes.Add((byte)c);
        }
    }
}
