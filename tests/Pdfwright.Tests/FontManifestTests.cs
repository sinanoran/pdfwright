using System.Security.Cryptography;
using System.Text.Json;

namespace Pdfwright.Tests;

/// <summary>
/// The fonts of the corpus are what the repository says they are: listed, licensed, unchanged and embeddable.
/// A font nobody wrote down is a font nobody checked the licence of.
/// </summary>
public class FontManifestTests
{
    private sealed record FontEntry(
        string File,
        string Family,
        string Style,
        string Sha256,
        long Bytes,
        string Copyright,
        string License,
        string LicenseFile,
        string Source);

    private sealed record Manifest(List<FontEntry> Fonts);

    private static Manifest Load()
    {
        string json = File.ReadAllText(Path.Combine(TestPaths.FontsDirectory, "manifest.json"));
        return JsonSerializer.Deserialize<Manifest>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("manifest.json is empty");
    }

    public static TheoryData<string> Fonts()
    {
        TheoryData<string> data = [];
        foreach (FontEntry font in Load().Fonts)
        {
            data.Add(font.File);
        }

        return data;
    }

    [Fact]
    public void Every_font_file_in_the_folder_is_in_the_manifest_and_every_entry_has_its_file()
    {
        string[] onDisk = Directory.GetFiles(TestPaths.FontsDirectory)
            .Select(path => Path.GetFileName(path))
            // Everything that is not a licence text, a readme or the manifest is a font: an unlisted .ttc or .woff counts too.
            .Where(name => !name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
                && !name.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                && !name.Equals("manifest.json", StringComparison.OrdinalIgnoreCase))
            .Order()
            .ToArray();
        string[] listed = Load().Fonts.Select(f => f.File).Order().ToArray();

        Assert.Equal(listed, onDisk);
    }

    [Theory]
    [MemberData(nameof(Fonts))]
    public void A_font_is_unchanged_since_it_was_recorded(string file)
    {
        FontEntry entry = Load().Fonts.Single(f => f.File == file);
        byte[] bytes = File.ReadAllBytes(TestPaths.Font(file));

        Assert.Equal(entry.Bytes, bytes.LongLength);
        Assert.Equal(entry.Sha256, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }

    [Theory]
    [MemberData(nameof(Fonts))]
    public void A_font_has_its_licence_on_record_and_its_text_beside_it(string file)
    {
        FontEntry entry = Load().Fonts.Single(f => f.File == file);

        Assert.Equal("SIL OFL 1.1", entry.License);
        Assert.StartsWith("https://", entry.Source);
        string licence = File.ReadAllText(Path.Combine(TestPaths.FontsDirectory, entry.LicenseFile));
        Assert.Contains("SIL Open Font License, Version 1.1", licence);
        Assert.Contains("Copyright", licence);
        // The licence text must be the one of the font's own project: the repository named in the font's copyright line
        // is named in the first line of the licence file too, so another script's OFL cannot stand in.
        string repository = System.Text.RegularExpressions.Regex.Match(entry.Copyright, @"\((https://[^)]+)\)").Groups[1].Value;
        Assert.NotEmpty(repository);
        Assert.Contains(repository, licence.Split('\n')[0]);
    }

    [Theory]
    [InlineData((ushort)0, true)]
    [InlineData((ushort)4, true)]
    [InlineData((ushort)8, true)]
    [InlineData((ushort)2, false)]
    [InlineData((ushort)0x0100, false)]
    [InlineData((ushort)0x0200, false)]
    [InlineData((ushort)0x0202, false)]
    public void The_embedding_permission_forbids_restricted_no_subsetting_and_bitmap_only_fonts(ushort fsType, bool allowed)
    {
        Assert.Equal(allowed, TrueTypeInfo.MayEmbed(fsType));
    }

    [Theory]
    [MemberData(nameof(Fonts))]
    public void A_font_says_the_same_copyright_as_the_manifest_and_may_be_embedded(string file)
    {
        FontEntry entry = Load().Fonts.Single(f => f.File == file);
        byte[] bytes = File.ReadAllBytes(TestPaths.Font(file));

        Assert.Equal(entry.Copyright, TrueTypeInfo.Copyright(bytes));
        ushort? permission = TrueTypeInfo.EmbeddingPermission(bytes);
        Assert.NotNull(permission);
        Assert.True(TrueTypeInfo.MayEmbed(permission.Value), $"{file} has fsType {permission}, which forbids embedding");
    }
}
