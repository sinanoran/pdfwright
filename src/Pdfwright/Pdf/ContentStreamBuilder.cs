using System.Buffers;
using System.Globalization;
using System.Text;

namespace Pdfwright.Pdf;

/// <summary>
/// Builds the content stream of a page (ISO 32000-1, section 8 and 9): the operators that draw paths and text. A
/// method writes one operator and its operands; nothing is checked beyond what the format requires, because the
/// layout above decides what to draw. Coordinates are in the PDF's own space (origin bottom left, y up); the
/// document layer turns the top-left, y-down space of its public canvas into this one once.
/// </summary>
/// <remarks>
/// Everything is written straight into the byte buffer: numbers are formatted into its free space (see
/// <see cref="PdfReal.Write"/> and <see cref="PdfReal.WriteRelative"/>, which produce the same text as
/// <see cref="PdfReal.Format"/> and the relative format of the tests' reference), operators are ASCII literals, and a
/// glyph code is four hexadecimal digits written without an array. Writing an operator allocates nothing.
/// </remarks>
internal sealed class ContentStreamBuilder
{
    /// <summary>Room for the longest number: 309 digits of a double, a sign, a point and 20 decimals, with some to spare.</summary>
    private const int NumberRoom = 400;

    private static ReadOnlySpan<byte> HexDigits => "0123456789ABCDEF"u8;

    private readonly ArrayBufferWriter<byte> _bytes = new(4096);

    /// <summary>The font resource name and size the last <c>Tf</c> set, or <see langword="null"/> when none was written or a <c>Q</c> may have changed it.</summary>
    internal (string Name, double Size)? CurrentFont { get; private set; }

    internal int Length => _bytes.WrittenCount;

    /// <summary>The size of the buffer, in bytes.</summary>
    internal int Capacity => _bytes.Capacity;

    /// <summary>
    /// Empties the builder to be written again, keeping its buffer: the bytes of the buffer are left as they are, and <see cref="ToMemory"/> covers only what is written after.
    /// What the builder knows of the stream so far (the font and the running text position) is forgotten too, so that it writes what a new builder would.
    /// </summary>
    internal void Clear()
    {
        _bytes.ResetWrittenCount();
        CurrentFont = null;
        MarkedContentDepth = 0;
        _mcidDepth = 0;
        _artifactDepth = 0;
        _lineKnown = false;
        _lineX = 0;
        _lineY = 0;
    }

    internal ReadOnlyMemory<byte> ToMemory() => _bytes.WrittenMemory;

    // ---- graphics state

    internal ContentStreamBuilder SaveState() => Op("q\n"u8);

    internal ContentStreamBuilder RestoreState()
    {
        CurrentFont = null; // the restored state holds a font this builder has not tracked
        return Op("Q\n"u8);
    }

    /// <summary>Concatenates a matrix to the current transformation: <c>a b c d e f cm</c>.</summary>
    /// <remarks>The operands are written with relative precision (ten significant digits), so that a tiny scale or a small rotation survives.</remarks>
    internal ContentStreamBuilder Transform(double a, double b, double c, double d, double e, double f)
    {
        Relative(a);
        Ascii(" "u8);
        Relative(b);
        Ascii(" "u8);
        Relative(c);
        Ascii(" "u8);
        Relative(d);
        Ascii(" "u8);
        Relative(e);
        Ascii(" "u8);
        Relative(f);
        return Ascii(" cm\n"u8);
    }

    internal ContentStreamBuilder LineWidth(double width) => Op("w\n"u8, width);

    internal ContentStreamBuilder LineCap(int style) => Op("J\n"u8, style);

    internal ContentStreamBuilder LineJoin(int style) => Op("j\n"u8, style);

    internal ContentStreamBuilder MiterLimit(double limit) => Op("M\n"u8, limit);

    internal ContentStreamBuilder Dash(double[] pattern, double phase)
    {
        Ascii("["u8);
        for (int i = 0; i < pattern.Length; i++)
        {
            if (i > 0)
            {
                Ascii(" "u8);
            }

            Number(pattern[i]);
        }

        Ascii("] "u8);
        Number(phase);
        return Ascii(" d\n"u8);
    }

    /// <summary>Selects an <c>ExtGState</c> of the page's resources by name (opacity is set through one).</summary>
    internal ContentStreamBuilder ExtGState(string name) => NameOp(name, " gs\n"u8);

    // ---- colour

    internal ContentStreamBuilder FillGray(double gray) => Op("g\n"u8, gray);

    internal ContentStreamBuilder StrokeGray(double gray) => Op("G\n"u8, gray);

    internal ContentStreamBuilder FillRgb(double r, double g, double b) => Op("rg\n"u8, r, g, b);

    internal ContentStreamBuilder StrokeRgb(double r, double g, double b) => Op("RG\n"u8, r, g, b);

    // ---- paths

    internal ContentStreamBuilder MoveTo(double x, double y) => Op("m\n"u8, x, y);

    internal ContentStreamBuilder LineTo(double x, double y) => Op("l\n"u8, x, y);

    internal ContentStreamBuilder CurveTo(double x1, double y1, double x2, double y2, double x3, double y3) =>
        Op("c\n"u8, x1, y1, x2, y2, x3, y3);

    internal ContentStreamBuilder Rectangle(double x, double y, double width, double height) => Op("re\n"u8, x, y, width, height);

    internal ContentStreamBuilder ClosePath() => Op("h\n"u8);

    internal ContentStreamBuilder Fill(bool evenOdd = false) => Op(evenOdd ? "f*\n"u8 : "f\n"u8);

    internal ContentStreamBuilder Stroke() => Op("S\n"u8);

    internal ContentStreamBuilder FillAndStroke(bool evenOdd = false) => Op(evenOdd ? "B*\n"u8 : "B\n"u8);

    /// <summary>Intersects the clipping path with the current path; the path is then ended without painting.</summary>
    internal ContentStreamBuilder Clip(bool evenOdd = false) => Op(evenOdd ? "W*\n"u8 : "W\n"u8).Op("n\n"u8);

    internal ContentStreamBuilder EndPath() => Op("n\n"u8);

    // ---- XObjects

    internal ContentStreamBuilder DrawXObject(string name) => NameOp(name, " Do\n"u8);

    // ---- text

    internal ContentStreamBuilder BeginText()
    {
        _lineKnown = false;
        return Op("BT\n"u8);
    }

    internal ContentStreamBuilder EndText() => Op("ET\n"u8);

    internal ContentStreamBuilder Font(string name, double size)
    {
        Name(name);
        Ascii(" "u8);
        Number(size);
        Ascii(" Tf\n"u8);
        CurrentFont = (name, size);
        return this;
    }

    /// <summary>Sets the text matrix, which also sets the line matrix: an absolute position for the next text.</summary>
    internal ContentStreamBuilder TextMatrix(double a, double b, double c, double d, double e, double f)
    {
        _lineKnown = false; // a matrix that is not a pure translation changes what a later Td means
        return Op("Tm\n"u8, a, b, c, d, e, f);
    }

    /// <summary>One unit of the position the stream keeps: a hundred-thousandth, the precision a number is written with.</summary>
    private const double Unit = 100000;

    /// <summary>The largest coordinate kept as a running position: far beyond any page, and far inside what a <see cref="long"/> of hundred-thousandths holds.</summary>
    private const double MaxTracked = 1e13;

    private bool _lineKnown;
    private long _lineX;
    private long _lineY;

    /// <summary>
    /// Puts the next text at (<paramref name="x"/>, <paramref name="y"/>) with the unit text matrix: a <c>Td</c> by the difference from the line matrix as
    /// written so far when that is a pure translation (the delta is taken from the rounded position the reader holds, so rounding never adds up),
    /// otherwise (the start of a text object, or after <see cref="TextMatrix"/>) an absolute <c>Tm</c>. A coordinate too large for the position kept in
    /// hundred-thousandths (beyond <see cref="MaxTracked"/>, which a page never reaches but a canvas with a tiny scale can) is written as an absolute <c>Tm</c>
    /// as given, and the next text starts from an absolute position again.
    /// </summary>
    internal ContentStreamBuilder TextPlace(double x, double y)
    {
        if (Math.Abs(x) >= MaxTracked || Math.Abs(y) >= MaxTracked)
        {
            _lineKnown = false;
            return Op("Tm\n"u8, 1, 0, 0, 1, x, y);
        }

        long targetX = (long)Math.Round(x * Unit, MidpointRounding.AwayFromZero);
        long targetY = (long)Math.Round(y * Unit, MidpointRounding.AwayFromZero);
        if (!_lineKnown)
        {
            Ascii("1 0 0 1 "u8);
            Scaled(targetX);
            Scaled(targetY);
            _ = Ascii("Tm\n"u8);
            _lineKnown = true;
        }
        else
        {
            Scaled(targetX - _lineX);
            Scaled(targetY - _lineY);
            _ = Ascii("Td\n"u8);
        }

        _lineX = targetX;
        _lineY = targetY;
        return this;
    }

    /// <summary>Moves to the start of the next line, offset from the start of the current one.</summary>
    internal ContentStreamBuilder TextMove(double x, double y)
    {
        _lineKnown = false;
        return Op("Td\n"u8, x, y);
    }

    internal ContentStreamBuilder TextRenderMode(int mode) => Op("Tr\n"u8, mode);

    /// <summary>Shows a string whose bytes are the codes of the current font, as hexadecimal.</summary>
    internal ContentStreamBuilder ShowHex(ReadOnlySpan<byte> codes)
    {
        Ascii("<"u8);
        Hex(codes);
        return Ascii("> Tj\n"u8);
    }

    /// <summary>Shows one two-byte code of the current font (see <see cref="PdfEmbeddedFont.Code"/>) as four hexadecimal digits: the same bytes as <c>ShowHex(PdfEmbeddedFont.Code(code))</c>, without an array.</summary>
    internal ContentStreamBuilder ShowCode(int code)
    {
        Span<byte> span = _bytes.GetSpan(11);
        span[0] = (byte)'<';
        span[1] = HexDigits[(code >> 12) & 0xF];
        span[2] = HexDigits[(code >> 8) & 0xF];
        span[3] = HexDigits[(code >> 4) & 0xF];
        span[4] = HexDigits[code & 0xF];
        span[5] = (byte)'>';
        span[6] = (byte)' ';
        span[7] = (byte)'T';
        span[8] = (byte)'j';
        span[9] = (byte)'\n';
        _bytes.Advance(10);
        return this;
    }

    /// <summary>
    /// Shows glyph codes with adjustments between them (the <c>TJ</c> operator): a number moves the next glyph left by that
    /// many thousandths of the font size, so a positive number tightens and a negative one loosens.
    /// </summary>
    internal ContentStreamBuilder ShowAdjusted(IEnumerable<(byte[] Codes, double? AdjustAfter)> parts)
    {
        Ascii("["u8);
        foreach ((byte[] codes, double? adjust) in parts)
        {
            Ascii("<"u8);
            Hex(codes);
            Ascii(">"u8);
            if (adjust is { } value)
            {
                Number(value);
            }
        }

        return Ascii("] TJ\n"u8);
    }

    // ---- marked content (the structure of a tagged PDF is built from these, see PdfStructureTree)

    /// <summary>How many marked-content sequences are open: a <c>BDC</c> or <c>BMC</c> without its <c>EMC</c> yet.</summary>
    internal int MarkedContentDepth { get; private set; }

    /// <summary>The depth at which the open sequence with an <c>MCID</c> began, or 0 when none is open.</summary>
    private int _mcidDepth;

    /// <summary>The depth at which the outermost open artifact began, or 0 when none is open.</summary>
    private int _artifactDepth;

    /// <summary>
    /// Throws when a sequence with an <c>MCID</c> cannot begin here: inside another one (its content would belong to two elements, which PDF/UA-1
    /// refuses) or inside an artifact (it would be the document's text and not at once). The page checks it before it records anything.
    /// </summary>
    internal void CheckMarkedContentAllowed()
    {
        if (_mcidDepth > 0)
        {
            throw new InvalidOperationException("Marked content with an MCID cannot begin inside another: end the element's content first.");
        }

        if (_artifactDepth > 0)
        {
            throw new InvalidOperationException("Marked content with an MCID cannot begin inside an artifact.");
        }
    }

    /// <summary>
    /// Begins a marked-content sequence with a property list that carries an <c>MCID</c>: <c>/Tag &lt;&lt;/MCID n&gt;&gt; BDC</c>. The tag is the
    /// structure type of the element the content belongs to (<c>P</c>, <c>H1</c>, <c>TD</c>), and the number is the content's place in the page's
    /// entry of the parent tree. It cannot begin inside another such sequence or inside an artifact. End it with <see cref="EndMarkedContent"/>.
    /// </summary>
    internal ContentStreamBuilder BeginMarkedContent(string tag, int mcid)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(mcid);
        CheckMarkedContentAllowed();
        Name(tag);
        Ascii(" <</MCID "u8);
        Span<byte> span = _bytes.GetSpan(16);
        _ = mcid.TryFormat(span, out int written, default, CultureInfo.InvariantCulture);
        _bytes.Advance(written);
        MarkedContentDepth++;
        _mcidDepth = MarkedContentDepth;
        return Ascii(">> BDC\n"u8);
    }

    /// <summary>Ends the innermost open sequence (marked content, artifact or span): <c>EMC</c>. With none open it throws, rather than write an unbalanced stream.</summary>
    internal ContentStreamBuilder EndMarkedContent()
    {
        if (MarkedContentDepth == 0)
        {
            throw new InvalidOperationException("No marked content is open: an EMC would have no BDC or BMC.");
        }

        if (_mcidDepth == MarkedContentDepth)
        {
            _mcidDepth = 0;
        }

        if (_artifactDepth == MarkedContentDepth)
        {
            _artifactDepth = 0;
        }

        MarkedContentDepth--;
        return Op("EMC\n"u8);
    }

    /// <summary>
    /// Begins a span in another language than the element around it, <c>/Span &lt;&lt;/Lang (de)&gt;&gt; BDC</c>. Its intended place is inside the
    /// element's own marked content (a sequence with an <c>MCID</c>), so that a screen reader changes its voice for the span and back after it; it
    /// has no <c>MCID</c> of its own, so it may also stand elsewhere. The language is a BCP 47 tag, written as a text string (ISO 32000-1, 14.9.2).
    /// End it with <see cref="EndMarkedContent"/>.
    /// </summary>
    internal ContentStreamBuilder BeginLanguageSpan(string language)
    {
        ArgumentException.ThrowIfNullOrEmpty(language);
        Ascii("/Span <</Lang "u8);
        TextString(language);
        MarkedContentDepth++;
        return Ascii(">> BDC\n"u8);
    }

    /// <summary>
    /// Begins an artifact, content that is not part of the document's text (a table's fills and borders, a decorative image): <c>/Artifact BMC</c>.
    /// A screen reader skips it; it has no <c>MCID</c> and no structure element. It cannot begin inside a sequence with an <c>MCID</c> (an artifact
    /// inside tagged content fails PDF/UA-1); an artifact inside an artifact is allowed. End it with <see cref="EndMarkedContent"/>.
    /// </summary>
    internal ContentStreamBuilder BeginArtifact()
    {
        OpenArtifact();
        return Op("/Artifact BMC\n"u8);
    }

    /// <summary>
    /// Begins a pagination artifact, the page's running header or footer: <c>/Artifact &lt;&lt;/Type /Pagination /Subtype /Header&gt;&gt; BDC</c>
    /// (or <c>/Footer</c>), or content repeated because the page broke, with no subtype (<see cref="PdfPaginationArtifact.Repeated"/>), which a
    /// reader can tell apart from the body and leave out of what it reads aloud. It nests as <see cref="BeginArtifact"/> does. End it with
    /// <see cref="EndMarkedContent"/>.
    /// </summary>
    internal ContentStreamBuilder BeginPaginationArtifact(PdfPaginationArtifact subtype)
    {
        ReadOnlySpan<byte> op = subtype switch
        {
            PdfPaginationArtifact.Header => "/Artifact <</Type /Pagination /Subtype /Header>> BDC\n"u8,
            PdfPaginationArtifact.Footer => "/Artifact <</Type /Pagination /Subtype /Footer>> BDC\n"u8,
            PdfPaginationArtifact.Repeated => "/Artifact <</Type /Pagination>> BDC\n"u8,
            _ => throw new ArgumentOutOfRangeException(nameof(subtype), subtype, "A pagination artifact is a header, a footer or repeated content."),
        };
        OpenArtifact();
        return Op(op);
    }

    private void OpenArtifact()
    {
        if (_mcidDepth > 0)
        {
            throw new InvalidOperationException("An artifact cannot begin inside marked content with an MCID: end the element's content first.");
        }

        MarkedContentDepth++;
        if (_artifactDepth == 0)
        {
            _artifactDepth = MarkedContentDepth;
        }
    }

    /// <summary>
    /// Begins a span whose <c>ActualText</c> is what a reader gives back when the marked glyphs are copied or searched, in
    /// place of the text it derives from the glyphs. A right-to-left run is drawn in visual order, so it carries its text
    /// in logical order this way. End it with <see cref="EndMarkedContent"/>.
    /// </summary>
    internal ContentStreamBuilder BeginActualText(string text)
    {
        byte[] utf16 = Encoding.BigEndianUnicode.GetBytes(text);
        Ascii("/Span <</ActualText <FEFF"u8);
        Hex(utf16);
        MarkedContentDepth++;
        return Ascii(">>> BDC\n"u8);
    }

    /// <summary>
    /// A text string as <see cref="PdfString.Text"/> makes it and the serializer writes it, straight into the buffer: printable ASCII as a literal
    /// with its parentheses and backslashes escaped, anything else as UTF-16BE with a byte-order mark, in hexadecimal.
    /// </summary>
    private void TextString(string text)
    {
        foreach (char c in text)
        {
            if (c is < ' ' or > '~')
            {
                Ascii("<FEFF"u8);
                Hex(Encoding.BigEndianUnicode.GetBytes(text));
                Ascii(">"u8);
                return;
            }
        }

        Span<byte> span = _bytes.GetSpan((text.Length * 2) + 2);
        int length = 0;
        span[length++] = (byte)'(';
        foreach (char c in text)
        {
            if (c is '(' or ')' or '\\')
            {
                span[length++] = (byte)'\\';
            }

            span[length++] = (byte)c;
        }

        span[length++] = (byte)')';
        _bytes.Advance(length);
    }

    // ---- the places that write

    /// <summary>An operator with no operand: <paramref name="op"/> is the operator and its line feed.</summary>
    private ContentStreamBuilder Op(ReadOnlySpan<byte> op) => Ascii(op);

    private ContentStreamBuilder Op(ReadOnlySpan<byte> op, double a)
    {
        Operand(a);
        return Ascii(op);
    }

    private ContentStreamBuilder Op(ReadOnlySpan<byte> op, double a, double b)
    {
        Operand(a);
        Operand(b);
        return Ascii(op);
    }

    private ContentStreamBuilder Op(ReadOnlySpan<byte> op, double a, double b, double c)
    {
        Operand(a);
        Operand(b);
        Operand(c);
        return Ascii(op);
    }

    private ContentStreamBuilder Op(ReadOnlySpan<byte> op, double a, double b, double c, double d)
    {
        Operand(a);
        Operand(b);
        Operand(c);
        Operand(d);
        return Ascii(op);
    }

    private ContentStreamBuilder Op(ReadOnlySpan<byte> op, double a, double b, double c, double d, double e, double f)
    {
        Operand(a);
        Operand(b);
        Operand(c);
        Operand(d);
        Operand(e);
        Operand(f);
        return Ascii(op);
    }

    /// <summary>A name operand and its operator (<paramref name="op"/> starts with its space and ends with the line feed).</summary>
    private ContentStreamBuilder NameOp(string name, ReadOnlySpan<byte> op)
    {
        Name(name);
        return Ascii(op);
    }

    /// <summary>A number and the space after it.</summary>
    private void Operand(double value)
    {
        Number(value);
        Ascii(" "u8);
    }

    private void Number(double value)
    {
        Span<byte> span = _bytes.GetSpan(NumberRoom);
        _bytes.Advance(PdfReal.Write(value, span));
    }

    /// <summary>A count of hundred-thousandths as the number it is, and the space after it: what <see cref="Operand"/> writes for the count divided by 100000, without the slow custom format.</summary>
    private void Scaled(long count)
    {
        Span<byte> span = _bytes.GetSpan(NumberRoom);
        int length = PdfReal.WriteScaled(count, span);
        span[length] = (byte)' ';
        _bytes.Advance(length + 1);
    }

    private void Relative(double value)
    {
        Span<byte> span = _bytes.GetSpan(NumberRoom);
        _bytes.Advance(PdfReal.WriteRelative(value, span));
    }

    private ContentStreamBuilder Ascii(ReadOnlySpan<byte> bytes)
    {
        bytes.CopyTo(_bytes.GetSpan(bytes.Length));
        _bytes.Advance(bytes.Length);
        return this;
    }

    private void Hex(ReadOnlySpan<byte> bytes)
    {
        Span<byte> span = _bytes.GetSpan(bytes.Length * 2);
        for (int i = 0; i < bytes.Length; i++)
        {
            span[2 * i] = HexDigits[bytes[i] >> 4];
            span[(2 * i) + 1] = HexDigits[bytes[i] & 0xF];
        }

        _bytes.Advance(bytes.Length * 2);
    }

    /// <summary>
    /// A resource name or a marked-content tag as it is written, with its solidus: letters, digits, underscore, dot and hyphen only. The names
    /// this library makes (<c>F1</c>, <c>X2</c>, <c>G3</c>) are such, and anything else is refused here rather than written as
    /// bytes that mean something else in a content stream.
    /// </summary>
    private void Name(string name)
    {
        if (name.Length == 0)
        {
            throw NotPlain(name);
        }

        foreach (char c in name)
        {
            if (c is not ((>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '.' or '-'))
            {
                throw NotPlain(name);
            }
        }

        Span<byte> span = _bytes.GetSpan(name.Length + 1);
        span[0] = (byte)'/';
        for (int i = 0; i < name.Length; i++)
        {
            span[i + 1] = (byte)name[i];
        }

        _bytes.Advance(name.Length + 1);
    }

    private static ArgumentException NotPlain(string name) =>
        new($"'{name}' is not a plain name: use letters, digits, underscore, dot and hyphen.", nameof(name));
}

/// <summary>Which running part of the page a pagination artifact is (ISO 32000-1, 14.8.2.2).</summary>
internal enum PdfPaginationArtifact
{
    /// <summary>The running header, above the body.</summary>
    Header,

    /// <summary>The running footer, below the body.</summary>
    Footer,

    /// <summary>
    /// Content of the body drawn again because the page broke, written with no subtype (ISO 32000-1, table 330, where the subtypes are the running
    /// parts of the page): a table's header rows repeated at the top of a later page, whose first drawing is the table's header in the structure.
    /// </summary>
    Repeated,
}
