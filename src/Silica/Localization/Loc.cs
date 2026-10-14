using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Silica.Localization;

/// <summary>
/// Translation lookup. Language files are JSON: built-in ones are embedded, more can be dropped into
/// a "Languages" folder next to the exe or in the settings folder (a file with the same code overrides).
/// </summary>
public sealed partial class Loc : INotifyPropertyChanged
{
    public static Loc Instance { get; } = new();

    private readonly List<LanguagePack> _packs = [];
    private LanguagePack _current;
    private LanguagePack _english;

    private Loc()
    {
        _english = _current = null!;
        Reload();
        _english = _packs.First(p => p.Code == "en");
        _current = _english;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? LanguageChanged;

    /// <summary>Bumped on every language switch; XAML bindings listen to it.</summary>
    public int Revision { get; private set; }

    public LanguagePack Current => _current;
    public LanguagePack English => _english;
    public IReadOnlyList<LanguagePack> Available => _packs;

    public static IEnumerable<string> ExternalFolders()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "Languages");
        yield return Path.Combine(Services.AppPaths.DataDirectory, "Languages");
    }

    public void Reload()
    {
        var byCode = new Dictionary<string, LanguagePack>();
        var asm = Assembly.GetExecutingAssembly();
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith("Silica.Languages.", StringComparison.Ordinal)))
        {
            using var s = asm.GetManifestResourceStream(name)!;
            var pack = LanguagePack.Parse(s, name);
            byCode[pack.Code] = pack;
        }

        foreach (var folder in ExternalFolders())
        {
            if (!Directory.Exists(folder)) continue;
            foreach (var file in Directory.EnumerateFiles(folder, "*.json"))
            {
                try
                {
                    using var s = File.OpenRead(file);
                    var pack = LanguagePack.Parse(s, file);
                    byCode[pack.Code] = pack;
                }
                catch (Exception ex)
                {
                    Services.Log.Warn($"Language file skipped: {file}: {ex.Message}");
                }
            }
        }

        _packs.Clear();
        _packs.AddRange(byCode.Values.OrderBy(p => p.Code == "en" ? 0 : 1).ThenBy(p => p.EnglishName, StringComparer.OrdinalIgnoreCase));
        if (byCode.TryGetValue("en", out var en)) _english = en;
        if (_current != null && byCode.TryGetValue(_current.Code, out var cur)) _current = cur;
    }

    /// <summary>Picks a language by code; "auto" or unknown codes follow the Windows UI language, then English.</summary>
    public void Use(string? code)
    {
        var pack = _packs.FirstOrDefault(p => p.Code == code?.ToLowerInvariant());
        if (pack == null)
        {
            var ui = CultureInfo.InstalledUICulture.TwoLetterISOLanguageName;
            pack = _packs.FirstOrDefault(p => p.Code == ui) ?? _english;
        }

        _current = pack;
        try
        {
            var culture = CultureInfo.GetCultureInfo(pack.Code);
            CultureInfo.CurrentUICulture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;
        }
        catch (CultureNotFoundException) { }

        Revision++;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Revision)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Current)));
        LanguageChanged?.Invoke();
    }

    public string this[string key] => T(key);

    /// <summary>Translates a key. Keys that end with a number ("Core #3", "GPU2") also match a "{0}" template.</summary>
    public string T(string key)
    {
        if (string.IsNullOrEmpty(key)) return key;
        if (_current.Strings.TryGetValue(key, out var s)) return s;
        if (_current != _english && TryTemplate(_current, key, out var t)) return t;
        if (_english.Strings.TryGetValue(key, out var e)) return e;
        return key;
    }

    public bool Has(string key) => _current.Strings.ContainsKey(key);

    public string F(string key, params object?[] args)
    {
        try { return string.Format(CultureInfo.CurrentCulture, T(key), args); }
        catch (FormatException) { return T(key); }
    }

    /// <summary>Plural-aware format: the translation holds forms separated by '|'; {0} is the count.</summary>
    public string N(long count, string key)
    {
        var raw = _current.Strings.TryGetValue(key, out var s) ? s : _english.Strings.GetValueOrDefault(key, key);
        var rule = _current.Strings.ContainsKey(key) ? _current.Plural : _english.Plural;
        var forms = raw.Split('|');
        var form = forms[Math.Min(PluralRules.FormIndex(rule, count), forms.Length - 1)];
        return string.Format(CultureInfo.CurrentCulture, form, count);
    }

    /// <summary>Keys present in English but missing from <paramref name="pack"/>.</summary>
    public IEnumerable<string> Missing(LanguagePack pack) =>
        pack == _english ? [] : _english.Strings.Keys.Where(k => !pack.Strings.ContainsKey(k));

    public double Completeness(LanguagePack pack)
    {
        if (pack == _english || _english.Strings.Count == 0) return 1;
        return 1 - (double)Missing(pack).Count() / _english.Strings.Count;
    }

    private static bool TryTemplate(LanguagePack pack, string key, out string result)
    {
        var m = TrailingNumber().Match(key);
        if (m.Success && pack.Strings.TryGetValue(key[..m.Index] + "{0}" + key[(m.Index + m.Length)..], out var template))
        {
            result = template.Replace("{0}", m.Value);
            return true;
        }
        result = key;
        return false;
    }

    [GeneratedRegex(@"(?<=[#\s])\d+(?=\D*$)")]
    private static partial Regex TrailingNumber();
}
