using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Silica.Localization;

/// <summary>Builds a starter file for a new translation: every English string, ready to be replaced.</summary>
public static class LanguageTemplate
{
    public static string Build(LanguagePack reference)
    {
        var english = Loc.Instance.English;
        var strings = new JsonObject();
        foreach (var key in english.Strings.Keys.Order(StringComparer.Ordinal))
            strings[key] = english.Strings[key];

        var root = new JsonObject
        {
            ["language"] = new JsonObject
            {
                ["code"] = "xx",
                ["name"] = "Native name of the language",
                ["englishName"] = "English name of the language",
                ["author"] = "",
                ["plural"] = "one-other"
            },
            ["strings"] = strings,
            ["synonyms"] = new JsonObject()
        };

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }
}
