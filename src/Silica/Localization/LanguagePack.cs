using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Silica.Localization;

/// <summary>
/// One language file. Keys are the English source strings; English itself is the source language,
/// so a missing key always falls back to readable English. Only the header is read up front;
/// the strings are loaded when the language is actually used.
/// </summary>
public sealed class LanguagePack
{
    private readonly Func<Stream> _open;
    private Content? _content;
    private int _count = -1;

    private LanguagePack(Func<Stream> open, string source)
    {
        _open = open;
        Source = source;
    }

    public string Code { get; private init; } = "";
    public string Name { get; private init; } = "";
    public string EnglishName { get; private init; } = "";
    public string Author { get; private init; } = "";
    public PluralRule Plural { get; private init; } = PluralRule.OneOther;

    /// <summary>Where it was loaded from (embedded resource name or file path).</summary>
    public string Source { get; }

    public bool IsLoaded => _content != null;

    public Dictionary<string, string> Strings => Load().Strings;

    /// <summary>Extra search words per key, e.g. "Graphics" → ["відеокарта", "видюха"].</summary>
    public Dictionary<string, string[]> Synonyms => Load().Synonyms;

    /// <summary>Number of translated strings, counted without keeping the file in memory.</summary>
    public int StringCount
    {
        get
        {
            if (_content != null) return _content.Strings.Count;
            if (_count < 0) _count = CountStrings();
            return _count;
        }
    }

    public void Unload() => _content = null;

    public static LanguagePack Open(Func<Stream> open, string source)
    {
        using var stream = open();
        var meta = ReadHeader(stream) ?? throw new InvalidDataException("Missing \"language\" section");
        if (string.IsNullOrWhiteSpace(meta.Code)) throw new InvalidDataException("Missing language code");
        return new LanguagePack(open, source)
        {
            Code = meta.Code.Trim().ToLowerInvariant(),
            Name = string.IsNullOrWhiteSpace(meta.Name) ? meta.Code : meta.Name,
            EnglishName = meta.EnglishName ?? "",
            Author = meta.Author ?? "",
            Plural = PluralRules.Parse(meta.Plural)
        };
    }

    private Content Load()
    {
        if (_content != null) return _content;
        using var stream = _open();
        var file = JsonSerializer.Deserialize(stream, LanguageJsonContext.Default.LanguageFile) ?? new LanguageFile();
        _content = new Content(
            new Dictionary<string, string>((file.Strings ?? []).Where(p => !string.IsNullOrEmpty(p.Value)), StringComparer.Ordinal),
            new Dictionary<string, string[]>(file.Synonyms ?? [], StringComparer.Ordinal));
        return _content;
    }

    // The header is the first object in every file, so only the beginning of the file is read.
    private static LanguageMeta? ReadHeader(Stream stream)
    {
        var buffer = new byte[8192];
        var length = 0;
        int n;
        while (length < buffer.Length && (n = stream.Read(buffer, length, buffer.Length - length)) > 0) length += n;
        var bytes = buffer.AsSpan(0, length);
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) bytes = bytes[3..];
        var reader = new Utf8JsonReader(bytes, isFinalBlock: length < buffer.Length,
            new JsonReaderState(new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }));
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.PropertyName && reader.CurrentDepth == 1 && reader.ValueTextEquals("language"))
            {
                reader.Read();
                return JsonSerializer.Deserialize(ref reader, LanguageJsonContext.Default.LanguageMeta);
            }
            if (reader.TokenType == JsonTokenType.StartObject && reader.CurrentDepth == 1 && !reader.TrySkip()) return null;
        }
        return null;
    }

    private int CountStrings()
    {
        using var stream = _open();
        var reader = new Utf8JsonReader(ReadAll(stream), new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var inStrings = false;
        var count = 0;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.PropertyName && reader.CurrentDepth == 1)
                inStrings = reader.ValueTextEquals("strings");
            else if (inStrings && reader.TokenType == JsonTokenType.PropertyName && reader.CurrentDepth == 2)
                count++;
        }
        return count;
    }

    private static byte[] ReadAll(Stream stream)
    {
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        var bytes = ms.ToArray();
        // Skip a UTF-8 byte order mark if the file was saved with one.
        return bytes is [0xEF, 0xBB, 0xBF, ..] ? bytes[3..] : bytes;
    }

    private sealed record Content(Dictionary<string, string> Strings, Dictionary<string, string[]> Synonyms);
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
[JsonSerializable(typeof(LanguageMeta))]
internal sealed partial class LanguageJsonContext : JsonSerializerContext;
