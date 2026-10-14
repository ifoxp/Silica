using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Silica.Localization;

/// <summary>
/// One language file. Keys are the English source strings; English itself is the source language,
/// so a missing key always falls back to readable English.
/// </summary>
public sealed class LanguagePack
{
    public required string Code { get; init; }
    public required string Name { get; init; }
    public string EnglishName { get; init; } = "";
    public string Author { get; init; } = "";
    public PluralRule Plural { get; init; } = PluralRule.OneOther;
    public Dictionary<string, string> Strings { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Extra search words per key, e.g. "Graphics" → ["відеокарта", "видюха"].</summary>
    public Dictionary<string, string[]> Synonyms { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Where it was loaded from (embedded resource name or file path).</summary>
    public string Source { get; init; } = "";

    public static LanguagePack Parse(Stream stream, string source)
    {
        var file = JsonSerializer.Deserialize(stream, LanguageJsonContext.Default.LanguageFile)
                   ?? throw new InvalidDataException("Empty language file");
        var meta = file.Language ?? throw new InvalidDataException("Missing \"language\" section");
        if (string.IsNullOrWhiteSpace(meta.Code)) throw new InvalidDataException("Missing language code");

        return new LanguagePack
        {
            Code = meta.Code.Trim().ToLowerInvariant(),
            Name = string.IsNullOrWhiteSpace(meta.Name) ? meta.Code : meta.Name,
            EnglishName = meta.EnglishName ?? "",
            Author = meta.Author ?? "",
            Plural = PluralRules.Parse(meta.Plural),
            Strings = new Dictionary<string, string>(
                (file.Strings ?? []).Where(p => !string.IsNullOrEmpty(p.Value)), StringComparer.Ordinal),
            Synonyms = new Dictionary<string, string[]>(file.Synonyms ?? [], StringComparer.Ordinal),
            Source = source
        };
    }
}

internal sealed class LanguageFile
{
    public LanguageMeta? Language { get; set; }
    public Dictionary<string, string>? Strings { get; set; }
    public Dictionary<string, string[]>? Synonyms { get; set; }
}

internal sealed class LanguageMeta
{
    public string? Code { get; set; }
    public string? Name { get; set; }
    public string? EnglishName { get; set; }
    public string? Author { get; set; }
    public string? Plural { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true)]
[JsonSerializable(typeof(LanguageFile))]
internal sealed partial class LanguageJsonContext : JsonSerializerContext;
