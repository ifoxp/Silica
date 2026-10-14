using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using Microsoft.Win32;

namespace Silica.Localization;

/// <summary>
/// Translation lookup. Language files are JSON: built-in ones are embedded, more can be dropped into
/// a "Languages" folder next to the exe or in the settings folder (a file with the same code overrides).
/// Only the active language, English and the languages of the installed keyboard layouts stay in memory.
/// </summary>
public sealed partial class Loc : INotifyPropertyChanged
{
    // Taken before Use() replaces CurrentUICulture with the chosen language.
    private static readonly CultureInfo DisplayCulture = CultureInfo.CurrentUICulture;

    public static Loc Instance { get; } = new();

    private readonly List<LanguagePack> _packs = [];
    private LanguagePack _current;
    private LanguagePack _english;
    private List<LanguagePack> _searchPacks = [];

    private Loc()
    {
        _english = _current = null!;
        Reload();
        _current = _english;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? LanguageChanged;

    /// <summary>Bumped on every language switch; XAML bindings listen to it.</summary>
    public int Revision { get; private set; }

    public LanguagePack Current => _current;
    public LanguagePack English => _english;
    public IReadOnlyList<LanguagePack> Available => _packs;

    public FlowDirection FlowDirection { get; private set; } = FlowDirection.LeftToRight;

    /// <summary>Languages whose words the search understands for every value: interface, English, keyboard layouts.</summary>
    public IReadOnlyList<LanguagePack> SearchPacks => _searchPacks;

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
            var resource = name;
            var pack = LanguagePack.Open(() => asm.GetManifestResourceStream(resource)!, resource);
            byCode[pack.Code] = pack;
        }

        foreach (var folder in ExternalFolders())
        {
            if (!Directory.Exists(folder)) continue;
            foreach (var file in Directory.EnumerateFiles(folder, "*.json"))
            {
                try
                {
                    var path = file;
                    var pack = LanguagePack.Open(() => File.OpenRead(path), path);
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
        _english = byCode["en"];
        if (_current != null) _current = byCode.GetValueOrDefault(_current.Code, _english);
        UpdateSearchPacks();
    }

    /// <summary>Picks a language by code; "auto" or unknown codes follow the Windows display language, then English.</summary>
    public void Use(string? code)
    {
        var pack = Find(code) ?? Match(DisplayCulture) ?? Match(CultureInfo.InstalledUICulture) ?? _english;
        var previous = _current;
        _current = pack;
        try
        {
            var culture = CultureInfo.GetCultureInfo(pack.Code);
            CultureInfo.CurrentUICulture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;
            FlowDirection = culture.TextInfo.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        }
        catch (CultureNotFoundException)
        {
            FlowDirection = FlowDirection.LeftToRight;
        }

        UpdateSearchPacks();
        if (previous != null && previous != pack && !_searchPacks.Contains(previous)) previous.Unload();

        Revision++;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Revision)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Current)));
        LanguageChanged?.Invoke();
    }

    public LanguagePack? Find(string? code) => code == null ? null : _packs.FirstOrDefault(p => p.Code == code.ToLowerInvariant());

    /// <summary>Best pack for a culture: exact name, then script variant (zh-Hant for Taiwan), then the language alone.</summary>
    public LanguagePack? Match(CultureInfo culture)
    {
        var name = culture.Name.ToLowerInvariant();
        if (Find(name) is { } exact) return exact;
        if (culture.TwoLetterISOLanguageName == "zh")
            return Find(name is "zh-tw" or "zh-hk" or "zh-mo" || name.StartsWith("zh-hant", StringComparison.Ordinal) ? "zh-hant" : "zh-hans");
        if (culture.TwoLetterISOLanguageName == "sr") return Find("sr-latn");
        if (culture.TwoLetterISOLanguageName is "nb" or "nn" or "no") return Find("nb");
        return Find(culture.TwoLetterISOLanguageName);
    }

    /// <summary>
    /// Runs <paramref name="action"/> for every installed language, loading each one only for the call,
    /// so building the page and setting part of the search index does not keep 50 languages in memory.
    /// </summary>
    public void ForEachPack(Action<LanguagePack> action)
    {
        foreach (var pack in _packs.ToList())
        {
            var wasLoaded = pack.IsLoaded;
            action(pack);
            if (!wasLoaded && pack != _current && pack != _english && !_searchPacks.Contains(pack)) pack.Unload();
        }
    }

    public string this[string key] => T(key);

    /// <summary>Translates a key. Keys ending with a separate number ("Core #3", "GPU Fan 2") also match a "{0}" template.</summary>
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

    public double Completeness(LanguagePack pack)
    {
        if (pack == _english || _english.Strings.Count == 0) return 1;
        return Math.Min(1, (double)pack.StringCount / _english.Strings.Count);
    }

    private void UpdateSearchPacks()
    {
        var list = new List<LanguagePack> { _english };
        if (_current != null && _current != _english) list.Add(_current);
        foreach (var culture in KeyboardCultures())
            if (Match(culture) is { } p && !list.Contains(p)) list.Add(p);
        _searchPacks = list;
    }

    // Layouts from HKCU\Keyboard Layout\Preload, e.g. "00000422" (Ukrainian). The low word is the language id.
    private static IEnumerable<CultureInfo> KeyboardCultures()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Keyboard Layout\Preload");
        foreach (var name in key?.GetValueNames() ?? [])
        {
            if (key!.GetValue(name) is not string hex || !int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var klid)) continue;
            CultureInfo? culture = null;
            try { culture = CultureInfo.GetCultureInfo(klid & 0xFFFF); } catch (CultureNotFoundException) { }
            if (culture != null) yield return culture;
        }
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
