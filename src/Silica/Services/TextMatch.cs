using System.Globalization;
using System.Text;

namespace Silica.Services;

/// <summary>
/// Text normalisation for forgiving search: case/diacritics folding, Cyrillic→Latin transliteration,
/// wrong-keyboard-layout recovery and typo-tolerant token comparison.
/// </summary>
public static class TextMatch
{
    private const string LatinKeys = "qwertyuiop[]asdfghjkl;'zxcvbnm,.`";
    private const string UkrainianKeys = "йцукенгшщзхїфівапролджєячсмитьбю'";
    private const string RussianKeys = "йцукенгшщзхъфывапролджэячсмитьбюё";

    private static readonly Dictionary<char, string> Translit = new()
    {
        ['а'] = "a", ['б'] = "b", ['в'] = "v", ['г'] = "h", ['ґ'] = "g", ['д'] = "d", ['е'] = "e", ['є'] = "ie",
        ['ж'] = "zh", ['з'] = "z", ['и'] = "y", ['і'] = "i", ['ї'] = "i", ['й'] = "i", ['к'] = "k", ['л'] = "l",
        ['м'] = "m", ['н'] = "n", ['о'] = "o", ['п'] = "p", ['р'] = "r", ['с'] = "s", ['т'] = "t", ['у'] = "u",
        ['ф'] = "f", ['х'] = "kh", ['ц'] = "ts", ['ч'] = "ch", ['ш'] = "sh", ['щ'] = "shch", ['ь'] = "", ['ю'] = "iu",
        ['я'] = "ia", ['ы'] = "y", ['э'] = "e", ['ъ'] = "", ['ё'] = "e"
    };

    /// <summary>Lower-case, strip accents and punctuation, collapse spaces.</summary>
    public static string Normalize(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(text.Length);
        var lastSpace = true;
        foreach (var ch in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
            {
                // Keep the breve of й and the diaeresis of ї: they are letters in Ukrainian, not accents.
                if (sb.Length > 0 && sb[^1] is 'и' or 'і' or 'е')
                {
                    sb[^1] = (sb[^1], ch) switch
                    {
                        ('и', '̆') => 'й',
                        ('і', '̈') => 'ї',
                        ('е', '̈') => 'е',
                        _ => sb[^1]
                    };
                }
                continue;
            }
            var c = char.ToLowerInvariant(ch);
            if (char.IsLetterOrDigit(c) || c == '#' || c == '+')
            {
                sb.Append(c);
                lastSpace = false;
            }
            else if (!lastSpace)
            {
                sb.Append(' ');
                lastSpace = true;
            }
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>Cyrillic to a plain Latin spelling; Latin input passes through.</summary>
    public static string ToLatin(string normalized)
    {
        var sb = new StringBuilder(normalized.Length + 8);
        foreach (var c in normalized)
            sb.Append(Translit.TryGetValue(c, out var t) ? t : c.ToString());
        return Simplify(sb.ToString());
    }

    /// <summary>
    /// Collapses spellings that people mix up when transliterating by ear (kh/h, ts/c, iu/yu, y/i…),
    /// so "videokarta", "vydeokarta" and "відеокарта" end up equal.
    /// </summary>
    private static string Simplify(string latin)
    {
        var s = latin.Replace("shch", "sch").Replace("kh", "h").Replace("ts", "c").Replace("zh", "j")
            .Replace("iu", "u").Replace("yu", "u").Replace("ia", "a").Replace("ya", "a").Replace("ie", "e")
            .Replace("ye", "e").Replace("ck", "k").Replace("ph", "f").Replace("w", "v").Replace("q", "k")
            .Replace("x", "ks");
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            var m = c switch { 'y' => 'i', 'g' => 'h', _ => c };
            if (sb.Length > 0 && sb[^1] == m && char.IsLetter(m)) continue;
            sb.Append(m);
        }
        return sb.ToString();
    }

    /// <summary>Re-reads text typed with the wrong keyboard layout (e.g. "ghjwtcjh" → "процесор").</summary>
    public static IEnumerable<string> LayoutVariants(string raw)
    {
        var lower = raw.ToLowerInvariant();
        var anyLatin = lower.Any(c => c is >= 'a' and <= 'z');
        var anyCyr = lower.Any(c => c is >= 'а' and <= 'я' or 'і' or 'ї' or 'є' or 'ґ' or 'ё');
        if (anyLatin)
        {
            yield return Map(lower, LatinKeys, UkrainianKeys);
            yield return Map(lower, LatinKeys, RussianKeys);
        }
        if (anyCyr)
        {
            yield return Map(lower, UkrainianKeys, LatinKeys);
            yield return Map(lower, RussianKeys, LatinKeys);
        }
    }

    private static string Map(string s, string from, string to)
    {
        var chars = s.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            var idx = from.IndexOf(chars[i]);
            if (idx >= 0 && idx < to.Length) chars[i] = to[idx];
        }
        return new string(chars);
    }

    /// <summary>
    /// How well one query token matches one indexed token (0 = no match, 1 = exact).
    /// Partial typing is treated as a prefix, small typos are forgiven.
    /// </summary>
    public static double TokenScore(string q, string t)
    {
        if (q.Length == 0 || t.Length == 0) return 0;
        if (q == t) return 1;
        if (t.StartsWith(q, StringComparison.Ordinal)) return q.Length == 1 ? 0.55 : 0.92 - 0.1 * Math.Min(1, (t.Length - q.Length) / 12.0);
        if (q.Length >= 3 && t.Contains(q, StringComparison.Ordinal)) return 0.7;
        if (q.Length < 3) return 0;

        var allowed = q.Length <= 4 ? 1 : q.Length <= 7 ? 2 : 3;
        // Compare with the start of the token too, so a typo in a half-typed word still matches.
        var prefixLen = Math.Min(t.Length, q.Length);
        var d = Math.Min(Distance(q, t, allowed), prefixLen < t.Length ? Distance(q, t[..prefixLen], allowed) + 1 : int.MaxValue);
        if (d > allowed) return 0;
        return 0.78 - 0.14 * d;
    }

    /// <summary>Optimal-string-alignment distance, giving up early once it exceeds <paramref name="limit"/>.</summary>
    public static int Distance(string a, string b, int limit)
    {
        if (Math.Abs(a.Length - b.Length) > limit) return limit + 1;
        var prev2 = new int[b.Length + 1];
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            var rowMin = cur[0];
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                var v = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1]) v = Math.Min(v, prev2[j - 2] + 1);
                cur[j] = v;
                rowMin = Math.Min(rowMin, v);
            }
            if (rowMin > limit) return limit + 1;
            (prev2, prev, cur) = (prev, cur, prev2);
        }
        return prev[b.Length];
    }
}
