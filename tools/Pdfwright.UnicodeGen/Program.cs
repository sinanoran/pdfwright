using Pdfwright.UnicodeGen;

// usage: Pdfwright.UnicodeGen <data directory> <output file>
if (args.Length != 2)
{
    Console.Error.WriteLine("usage: Pdfwright.UnicodeGen <data directory> <output file>");
    return 2;
}

string generated = BidiDataGenerator.Generate(args[0]);
File.WriteAllText(args[1], generated.Replace("\n", "\r\n", StringComparison.Ordinal), new System.Text.UTF8Encoding(false));
Console.WriteLine($"Wrote {args[1]} ({generated.Length} characters).");
return 0;
