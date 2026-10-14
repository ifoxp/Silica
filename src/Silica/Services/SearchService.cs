using System.Windows;
using Silica.Localization;
using Silica.Core.Info;
using Silica.Core.Model;
using Silica.Core.Sensors;

namespace Silica.Services;

public enum SearchKind { Page, Setting, Sensor, Info, Row }

public sealed record SearchHit(SearchKind Kind, string Title, string Subtitle, string Icon, string NavId, string? Target, double Score);

/// <summary>
/// Searches pages, settings, live sensors and every collected value. Matching works across all installed
/// languages at once, so a Ukrainian query finds English labels and the other way round.
/// </summary>
public static class SearchService
{
    private sealed class Entry
    {
        public required SearchKind Kind { get; init; }
        public required string TitleKey { get; init; }
        public string? Value { get; init; }
        public string? SubtitleKey { get; init; }
        public string? SubtitleKey2 { get; init; }
        public required string Icon { get; init; }
        public required string NavId { get; init; }
        public string? Target { get; init; }
        public bool TranslateTitle { get; init; } = true;
        public required string[] Tokens { get; init; }
        public required string[] ContextTokens { get; init; }
        public required string[] Phrases { get; init; }
    }

    private static readonly object Gate = new();
    private static List<Entry> _static = [];
    private static readonly Dictionary<string, List<Entry>> PageEntries = [];
    private static List<Entry> _sensors = [];
    private static int _indexing;
    private static bool _initialized;

    public static event Action? IndexChanged;

    public static bool IsIndexing => _indexing > 0;

    /// <summary>Settings that can be jumped to: (English title, anchor, extra keywords).</summary>
    public static readonly (string Title, string Anchor, string Keywords)[] SettingEntries =
    [
        ("Theme", "appearance", "dark light mode color scheme"),
        ("Accent color", "appearance", "colour highlight"),
        ("Language", "language", "translation locale"),
        ("Temperature unit", "monitoring", "celsius fahrenheit"),
        ("Polling interval", "monitoring", "refresh rate update frequency"),
        ("Monitored hardware", "monitoring", "sensor groups devices"),
        ("Tray icon", "tray", "notification area taskbar"),
        ("Overlay", "overlay", "osd on screen display panel always on top"),
        ("Sensor logging", "logging", "csv log record history"),
        ("Alerts", "alerts", "notification warning threshold overheat"),
        ("Start with Windows", "startup", "autostart boot launch"),
        ("Search", "search", "index"),
        ("About", "about", "version license")
    ];

    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        PageStore.Collected += page => { IndexPage(page); IndexChanged?.Invoke(); };
        SensorHub.Shared.DevicesChanged += () => { IndexSensors(); IndexChanged?.Invoke(); };
        Loc.Instance.LanguageChanged += () => Task.Run(BuildStatic);
        Task.Run(BuildStatic);
        foreach (var page in PageStore.All) IndexPage(page);
        IndexSensors();
    }

    /// <summary>Called when the search box gets focus: reads the remaining pages in the background so values become searchable.</summary>
    public static void WarmUp()
    {
        Initialize();
        if (!Settings.Current.DeepSearch || Interlocked.CompareExchange(ref _indexing, 1, 0) != 0) return;
        var missing = InfoProviders.All.Where(p => PageStore.Cached(p.Id) == null).ToList();
        if (missing.Count == 0)
        {
            _indexing = 0;
            return;
        }

        Task.Run(async () =>
        {
            foreach (var p in missing)
            {
                try { await PageStore.GetAsync(p); } catch { }
            }
            _indexing = 0;
            IndexChanged?.Invoke();
        });
    }

    public static IReadOnlyList<SearchHit> Query(string text, int max = 40)
    {
        Initialize();
        var variants = QueryVariants(text);
        if (variants.Count == 0) return [];

        List<Entry> all;
        lock (Gate) all = [.. _static, .. _sensors, .. PageEntries.Values.SelectMany(v => v)];

        var scored = new List<(Entry Entry, double Score)>();
        foreach (var e in all)
        {
            var best = 0.0;
            foreach (var v in variants) best = Math.Max(best, Score(v, e));
            if (best >= 0.5) scored.Add((e, best * Weight(e.Kind)));
        }

        var perPage = new Dictionary<string, int>();
        var perDevice = new Dictionary<string, int>();
        var hits = new List<SearchHit>();
        foreach (var (e, score) in scored.OrderByDescending(s => s.Score).ThenBy(s => s.Entry.Kind))
        {
            if (e.Kind == SearchKind.Sensor)
            {
                perDevice.TryGetValue(e.SubtitleKey ?? "", out var d);
                if (d >= 3) continue;
                perDevice[e.SubtitleKey ?? ""] = d + 1;
            }
            if (e.Kind is SearchKind.Info or SearchKind.Row)
            {
                perPage.TryGetValue(e.NavId, out var n);
                if (n >= 6) continue;
                perPage[e.NavId] = n + 1;
            }
            hits.Add(ToHit(e, score));
            if (hits.Count >= max) break;
        }
        return hits;
    }

    private sealed record QueryVariant(string[] Tokens, string Phrase, double Penalty);

    private static List<QueryVariant> QueryVariants(string text)
    {
        var list = new List<QueryVariant>();
        void Add(string raw, double penalty)
        {
            var n = TextMatch.Normalize(raw);
            if (n.Length == 0) return;
            list.Add(new QueryVariant(n.Split(' '), n, penalty));
            var latin = TextMatch.ToLatin(n);
            if (latin != n) list.Add(new QueryVariant(latin.Split(' '), latin, penalty + 0.04));
        }
        Add(text, 0);
        foreach (var v in TextMatch.LayoutVariants(text)) Add(v, 0.06);
        return list;
    }

    private static double Score(QueryVariant q, Entry e)
    {
        var sum = 0.0;
        foreach (var qt in q.Tokens)
        {
            var best = 0.0;
            foreach (var t in e.Tokens)
            {
                var s = TextMatch.TokenScore(qt, t);
                if (s > best) { best = s; if (s >= 1) break; }
            }
            // Matching only the device or page name is weaker than matching the entry itself.
            if (best < 1)
                foreach (var t in e.ContextTokens)
                    best = Math.Max(best, TextMatch.TokenScore(qt, t) * 0.8);
            if (best == 0) return 0;
            sum += best;
        }
        var score = sum / q.Tokens.Length;
        if (q.Tokens.Length > 1)
            foreach (var p in e.Phrases)
                if (p.Contains(q.Phrase, StringComparison.Ordinal)) { score = Math.Min(1, score + 0.12); break; }
        return Math.Max(0, score - q.Penalty);
    }

    private static double Weight(SearchKind kind) => kind switch
    {
        SearchKind.Page => 1.0,
        SearchKind.Setting => 0.95,
        SearchKind.Sensor => 0.86,
        SearchKind.Info => 0.82,
        _ => 0.7
    };

    private static SearchHit ToHit(Entry e, double score)
    {
        var loc = Loc.Instance;
        var title = e.TranslateTitle ? loc.T(e.TitleKey) : e.TitleKey;
        if (e.Value != null) title += ": " + e.Value;
        var subtitle = e.SubtitleKey == null ? "" : loc.T(e.SubtitleKey);
        if (e.SubtitleKey2 != null) subtitle += "  ·  " + loc.T(e.SubtitleKey2);
        return new SearchHit(e.Kind, title, subtitle, e.Icon, e.NavId, e.Target, score);
    }

    // Page and setting names are few, so they are matched in every installed language.
    private static void BuildStatic()
    {
        var items = Views.NavCatalog.Build();
        var keys = items.Select(i => i.Title).Concat(SettingEntries.Select(e => e.Title)).Distinct().ToList();
        var words = keys.ToDictionary(k => k, k => new List<string> { k });
        Loc.Instance.ForEachPack(pack =>
        {
            foreach (var key in keys)
            {
                if (pack.Strings.TryGetValue(key, out var t)) words[key].Add(t);
                if (pack.Synonyms.TryGetValue(key, out var syn)) words[key].AddRange(syn);
            }
        });

        var list = new List<Entry>();
        foreach (var item in items)
        {
            list.Add(Make(SearchKind.Page, item.Title, null, KindCaption(item.Section), null, item.Icon, item.Id, null, true,
                words[item.Title].Append(item.Id)));
        }
        foreach (var (title, anchor, keywords) in SettingEntries)
        {
            list.Add(Make(SearchKind.Setting, title, null, "Settings", null, "", "settings", anchor, true,
                words[title].Append(keywords)));
        }
        lock (Gate) _static = list;
        IndexChanged?.Invoke();
    }

    private static string KindCaption(Views.NavSection s) => s switch
    {
        Views.NavSection.Hardware => "Hardware",
        Views.NavSection.Software => "Software",
        Views.NavSection.Tools => "Tools",
        _ => "Silica"
    };

    private static void IndexSensors()
    {
        var list = new List<Entry>();
        foreach (var s in SensorHub.Shared.AllSensors)
        {
            list.Add(Make(SearchKind.Sensor, s.Name, null, s.Device.Name, null, "", "sensors", s.Id, true,
                Variants(s.Name), [s.Device.Name, s.Kind.ToString()]));
        }
        lock (Gate) _sensors = list;
    }

    private static void IndexPage(InfoPage page)
    {
        var provider = InfoProviders.Find(page.Id);
        if (provider == null) return;
        var list = new List<Entry>();
        foreach (var block in page.Blocks)
        {
            var blockTerms = block.LocalizeTitle ? Variants(block.Title) : [block.Title];
            switch (block)
            {
                case PropertyBlock pb:
                    foreach (var item in pb.Items)
                    {
                        var value = item.LocalizeValue ? Loc.Instance.T(item.Value) : item.Value;
                        var terms = Variants(item.Label).Append(item.Value).Concat(item.LocalizeValue ? Variants(item.Value) : []);
                        list.Add(Make(SearchKind.Info, item.Label, Shorten(value), page.Title, block.LocalizeTitle ? block.Title : null,
                            provider.Icon, page.Id, item.Label, true, terms, blockTerms));
                    }
                    break;
                case TableBlock tb:
                    foreach (var row in tb.Rows)
                    {
                        var first = row.FirstOrDefault(c => c.Length > 0) ?? "";
                        if (first.Length == 0) continue;
                        list.Add(Make(SearchKind.Row, first, null, page.Title, block.LocalizeTitle ? block.Title : null,
                            provider.Icon, page.Id, first, false, row, blockTerms));
                    }
                    break;
            }
        }
        lock (Gate) PageEntries[page.Id] = list;
    }

    /// <summary>The English key with its translations and synonyms in the search languages.</summary>
    private static IEnumerable<string> Variants(string key)
    {
        yield return key;
        foreach (var pack in Loc.Instance.SearchPacks)
        {
            if (pack.Strings.TryGetValue(key, out var t)) yield return t;
            if (pack.Synonyms.TryGetValue(key, out var syn))
                foreach (var s in syn) yield return s;
        }
    }

    private static Entry Make(SearchKind kind, string titleKey, string? value, string? subtitle, string? subtitle2, string icon,
        string navId, string? target, bool translateTitle, IEnumerable<string> terms, IEnumerable<string>? context = null)
    {
        var phrases = new List<string>();
        var tokens = Tokenize(terms, phrases);
        var contextTokens = context == null ? [] : Tokenize(context, phrases);
        contextTokens.ExceptWith(tokens);
        return new Entry
        {
            Kind = kind, TitleKey = titleKey, Value = value, SubtitleKey = subtitle, SubtitleKey2 = subtitle2, Icon = icon,
            NavId = navId, Target = target, TranslateTitle = translateTitle,
            Tokens = [.. tokens], ContextTokens = [.. contextTokens], Phrases = [.. phrases]
        };
    }

    private static HashSet<string> Tokenize(IEnumerable<string> terms, List<string> phrases)
    {
        var tokens = new HashSet<string>();
        foreach (var term in terms)
        {
            var n = TextMatch.Normalize(term);
            if (n.Length == 0) continue;
            phrases.Add(n);
            foreach (var t in n.Split(' ')) tokens.Add(t);
            var latin = TextMatch.ToLatin(n);
            if (latin == n) continue;
            phrases.Add(latin);
            foreach (var t in latin.Split(' ')) tokens.Add(t);
        }
        return tokens;
    }

    private static string Shorten(string s) => s.Length > 90 ? s[..87] + "..." : s;
}
