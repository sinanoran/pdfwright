using System.Globalization;
using System.Text;
using Pdfwright.Graphics;
using Pdfwright.Pdf;

namespace Pdfwright.Tests.Drawing;

/// <summary>What a canvas wrote into a page: the operators, one for each line, and the page's opacity states.</summary>
internal sealed record Drawn(string[] Lines, IReadOnlyList<(string Name, double Fill, double Stroke)> States, string[] Resources)
{
    /// <summary>The lines between the canvas's own <c>q</c> and <c>cm</c> at the start and its <c>Q</c> at the end.</summary>
    internal string[] Inner => Lines[2..^1];
}

internal static class CanvasTestKit
{
    internal const double PageWidth = 595;

    internal const double PageHeight = 842;

    /// <summary>The first two lines every canvas on a page of <see cref="PageHeight"/> starts with, and the last one.</summary>
    internal static string[] Wrap(params string[] inner) => ["q", "1 0 0 -1 0 842 cm", .. inner, "Q"];

    /// <summary>Runs <paramref name="draw"/> on a canvas whose box starts at the page's top left, ends the canvas and returns what it wrote.</summary>
    internal static Drawn Run(Action<Canvas> draw, double left = 0, double top = PageHeight)
    {
        using MemoryStream stream = new();
        using PdfDocumentWriter document = new(stream, new PdfWriterOptions { Compression = PdfCompression.None });
        PdfPageWriter page = document.BeginPage(PageWidth, PageHeight);
        Canvas canvas = new(page, left, top, 400, 300);
        draw(canvas);
        canvas.End();
        string text = Encoding.ASCII.GetString(page.Content.ToMemory().Span);
        PdfDictionary resources = page.BuildResources();
        List<(string, double, double)> states = [];
        if (resources["ExtGState"] is PdfDictionary gs)
        {
            foreach (KeyValuePair<string, PdfObject> entry in gs.Entries)
            {
                PdfDictionary state = (PdfDictionary)entry.Value;
                states.Add((entry.Key, ((PdfReal)state["ca"]!).Value, ((PdfReal)state["CA"]!).Value));
            }
        }

        List<string> resourceNames = [];
        foreach (KeyValuePair<string, PdfObject> entry in resources.Entries)
        {
            resourceNames.Add(entry.Key);
        }

        return new Drawn(text.Split('\n', StringSplitOptions.RemoveEmptyEntries), states, [.. resourceNames]);
    }

    internal static Pdfwright.Graphics.Path Rect(double x, double y, double w, double h) => new PathBuilder().Rectangle(x, y, w, h).Build();

    /// <summary>The numbers before the operator of a line such as <c>1 0 0 -1 0 842 cm</c>.</summary>
    internal static double[] Numbers(string line) =>
        [.. line.Split(' ').SkipLast(1).Select(t => double.Parse(t, CultureInfo.InvariantCulture))];
}
