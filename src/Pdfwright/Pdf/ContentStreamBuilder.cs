using System.Buffers;
using System.Text;

namespace Pdfwright.Pdf;

/// <summary>
/// Builds the content stream of a page (ISO 32000-1, section 8 and 9): the operators that draw paths and text. A
/// method writes one operator and its operands; nothing is checked beyond what the format requires, because the
/// layout above decides what to draw. Coordinates are in the PDF's own space (origin bottom left, y up); the
/// document layer turns the top-left, y-down space of its public canvas into this one once.
/// </summary>
internal sealed class ContentStreamBuilder
{
    private readonly ArrayBufferWriter<byte> _bytes = new();

    internal int Length => _bytes.WrittenCount;

    internal ReadOnlyMemory<byte> ToMemory() => _bytes.WrittenMemory;

    // ---- graphics state

    internal ContentStreamBuilder SaveState() => Op("q");

    internal ContentStreamBuilder RestoreState() => Op("Q");

    /// <summary>Concatenates a matrix to the current transformation: <c>a b c d e f cm</c>.</summary>
    internal ContentStreamBuilder Transform(double a, double b, double c, double d, double e, double f) =>
        Op("cm", a, b, c, d, e, f);

    internal ContentStreamBuilder LineWidth(double width) => Op("w", width);

    internal ContentStreamBuilder LineCap(int style) => Op("J", style);

    internal ContentStreamBuilder LineJoin(int style) => Op("j", style);

    internal ContentStreamBuilder Dash(double[] pattern, double phase)
    {
        Raw("[");
        for (int i = 0; i < pattern.Length; i++)
        {
            Raw((i > 0 ? " " : string.Empty) + PdfReal.Format(pattern[i]));
        }

        Raw("] " + PdfReal.Format(phase) + " d\n");
        return this;
    }

    /// <summary>Selects an <c>ExtGState</c> of the page's resources by name (opacity is set through one).</summary>
    internal ContentStreamBuilder ExtGState(string name) => NameOp("gs", name);

    // ---- colour

    internal ContentStreamBuilder FillGray(double gray) => Op("g", gray);

    internal ContentStreamBuilder StrokeGray(double gray) => Op("G", gray);

    internal ContentStreamBuilder FillRgb(double r, double g, double b) => Op("rg", r, g, b);

    internal ContentStreamBuilder StrokeRgb(double r, double g, double b) => Op("RG", r, g, b);

    // ---- paths

    internal ContentStreamBuilder MoveTo(double x, double y) => Op("m", x, y);

    internal ContentStreamBuilder LineTo(double x, double y) => Op("l", x, y);

    internal ContentStreamBuilder CurveTo(double x1, double y1, double x2, double y2, double x3, double y3) =>
        Op("c", x1, y1, x2, y2, x3, y3);

    internal ContentStreamBuilder Rectangle(double x, double y, double width, double height) => Op("re", x, y, width, height);

    internal ContentStreamBuilder ClosePath() => Op("h");

    internal ContentStreamBuilder Fill(bool evenOdd = false) => Op(evenOdd ? "f*" : "f");

    internal ContentStreamBuilder Stroke() => Op("S");

    internal ContentStreamBuilder FillAndStroke(bool evenOdd = false) => Op(evenOdd ? "B*" : "B");

    /// <summary>Intersects the clipping path with the current path; the path is then ended without painting.</summary>
    internal ContentStreamBuilder Clip(bool evenOdd = false) => Op(evenOdd ? "W*" : "W").Op("n");

    internal ContentStreamBuilder EndPath() => Op("n");

    // ---- XObjects

    internal ContentStreamBuilder DrawXObject(string name) => NameOp("Do", name);

    // ---- text

    internal ContentStreamBuilder BeginText() => Op("BT");

    internal ContentStreamBuilder EndText() => Op("ET");

    internal ContentStreamBuilder Font(string name, double size)
    {
        Raw("/" + Regular(name) + " " + PdfReal.Format(size) + " Tf\n");
        return this;
    }

    /// <summary>Sets the text matrix, which also sets the line matrix: an absolute position for the next text.</summary>
    internal ContentStreamBuilder TextMatrix(double a, double b, double c, double d, double e, double f) =>
        Op("Tm", a, b, c, d, e, f);

    /// <summary>Moves to the start of the next line, offset from the start of the current one.</summary>
    internal ContentStreamBuilder TextMove(double x, double y) => Op("Td", x, y);

    internal ContentStreamBuilder TextRenderMode(int mode) => Op("Tr", mode);

    /// <summary>Shows a string whose bytes are the codes of the current font, as hexadecimal.</summary>
    internal ContentStreamBuilder ShowHex(ReadOnlySpan<byte> codes)
    {
        Raw("<" + Convert.ToHexString(codes) + "> Tj\n");
        return this;
    }

    /// <summary>
    /// Shows glyph codes with adjustments between them (the <c>TJ</c> operator): a number moves the next glyph left by that
    /// many thousandths of the font size, so a positive number tightens and a negative one loosens.
    /// </summary>
    internal ContentStreamBuilder ShowAdjusted(IEnumerable<(byte[] Codes, double? AdjustAfter)> parts)
    {
        Raw("[");
        foreach ((byte[] codes, double? adjust) in parts)
        {
            Raw("<" + Convert.ToHexString(codes) + ">");
            if (adjust is { } value)
            {
                Raw(PdfReal.Format(value));
            }
        }

        Raw("] TJ\n");
        return this;
    }

    // ---- marked content (the structure of a tagged PDF, added in 0.2, is built from these)

    /// <summary>Begins a marked-content sequence with a property list that carries an <c>MCID</c>: <c>/Tag &lt;&lt;/MCID n&gt;&gt; BDC</c>.</summary>
    internal ContentStreamBuilder BeginMarkedContent(string tag, int mcid)
    {
        Raw("/" + Regular(tag) + " <</MCID " + mcid.ToString(System.Globalization.CultureInfo.InvariantCulture) + ">> BDC\n");
        return this;
    }

    internal ContentStreamBuilder EndMarkedContent() => Op("EMC");

    /// <summary>
    /// Begins a span whose <c>ActualText</c> is what a reader gives back when the marked glyphs are copied or searched, in
    /// place of the text it derives from the glyphs. A right-to-left run is drawn in visual order, so it carries its text
    /// in logical order this way. End it with <see cref="EndMarkedContent"/>.
    /// </summary>
    internal ContentStreamBuilder BeginActualText(string text)
    {
        byte[] utf16 = Encoding.BigEndianUnicode.GetBytes(text);
        Raw("/Span <</ActualText <FEFF" + Convert.ToHexString(utf16) + ">>> BDC\n");
        return this;
    }

    // ---- the one place that writes

    private ContentStreamBuilder Op(string op, params double[] operands)
    {
        StringBuilder line = new();
        foreach (double operand in operands)
        {
            line.Append(PdfReal.Format(operand)).Append(' ');
        }

        line.Append(op).Append('\n');
        Raw(line.ToString());
        return this;
    }

    private ContentStreamBuilder NameOp(string op, string name)
    {
        Raw("/" + Regular(name) + " " + op + "\n");
        return this;
    }

    /// <summary>
    /// A resource name or a marked-content tag as it is written: letters, digits, underscore, dot and hyphen only. The names
    /// this library makes (<c>F1</c>, <c>X2</c>, <c>G3</c>) are such, and anything else is refused here rather than written as
    /// bytes that mean something else in a content stream.
    /// </summary>
    private static string Regular(string name)
    {
        if (name.Length == 0 || !name.All(c => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '.' or '-'))
        {
            throw new ArgumentException($"'{name}' is not a plain name: use letters, digits, underscore, dot and hyphen.", nameof(name));
        }

        return name;
    }

    private void Raw(string text)
    {
        Span<byte> span = _bytes.GetSpan(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            span[i] = (byte)text[i];
        }

        _bytes.Advance(text.Length);
    }
}
