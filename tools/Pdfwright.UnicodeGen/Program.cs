using System.Text;
using Pdfwright.UnicodeGen;

// usage: Pdfwright.UnicodeGen <data directory> <output directory>
if (args.Length != 2)
{
    Console.Error.WriteLine("usage: Pdfwright.UnicodeGen <data directory> <output directory>");
    return 2;
}

foreach ((string file, string text) in new[]
{
    ("BidiTables.g.cs", BidiDataGenerator.Generate(args[0])),
    ("CharacterTables.g.cs", PropertyDataGenerator.Generate(args[0])),
    ("ScriptTables.g.cs", ScriptDataGenerator.Generate(args[0])),
})
{
    string path = Path.Combine(args[1], file);
    File.WriteAllText(path, text.Replace("\n", "\r\n", StringComparison.Ordinal), new UTF8Encoding(false));
    Console.WriteLine($"Wrote {path} ({text.Length} characters).");
}

return 0;
