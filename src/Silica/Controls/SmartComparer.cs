using System.Collections;
using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Silica.Controls;

/// <summary>
/// Sorts table cells the way a person expects: sizes by bytes ("512 MB" before "1.5 GB"), numbers and dates
/// by value, everything else naturally ("Core 2" before "Core 10"). Empty cells always go last.
/// </summary>
public sealed partial class SmartComparer(int column, ListSortDirection direction) : IComparer
{
    private static readonly Dictionary<string, double> Units = new(StringComparer.OrdinalIgnoreCase)
    {
        ["B"] = 1, ["KB"] = 1024, ["MB"] = 1024d * 1024, ["GB"] = 1024d * 1024 * 1024, ["TB"] = 1024d * 1024 * 1024 * 1024,
        ["KiB"] = 1024, ["MiB"] = 1024d * 1024, ["GiB"] = 1024d * 1024 * 1024, ["TiB"] = 1024d * 1024 * 1024 * 1024,
        ["Hz"] = 1, ["kHz"] = 1e3, ["MHz"] = 1e6, ["GHz"] = 1e9,
        ["bps"] = 1, ["Kbps"] = 1e3, ["Mbps"] = 1e6, ["Gbps"] = 1e9,
        ["ms"] = 1e-3, ["s"] = 1, ["µs"] = 1e-6, ["ns"] = 1e-9
    };

    public int Compare(object? x, object? y)
    {
        var a = Cell(x);
        var b = Cell(y);
        if (a.Length == 0 || b.Length == 0)
            return a.Length == b.Length ? 0 : a.Length == 0 ? 1 : -1;
        var r = CompareText(a, b);
        return direction == ListSortDirection.Ascending ? r : -r;
    }

    private string Cell(object? row) => row is string[] cells && column < cells.Length ? cells[column] : "";

    public static int CompareText(string a, string b)
    {
        var qa = Quantity(a);
        var qb = Quantity(b);
        if (qa is { } va && qb is { } vb) return va.CompareTo(vb);

        if (DateTime.TryParse(a, CultureInfo.CurrentCulture, DateTimeStyles.None, out var da)
            && DateTime.TryParse(b, CultureInfo.CurrentCulture, DateTimeStyles.None, out var db)
            && LooksLikeDate(a) && LooksLikeDate(b))
            return da.CompareTo(db);

        return Natural(a, b);
    }

    private static bool LooksLikeDate(string s) => s.Count(char.IsDigit) >= 4 && (s.Contains('.') || s.Contains('/') || s.Contains('-'));

    private static double? Quantity(string s)
    {
        var m = QuantityRegex().Match(s.Trim());
        if (!m.Success) return null;
        var num = m.Groups["n"].Value.Replace(" ", "").Replace(" ", "");
        if (!double.TryParse(num.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            return null;
        var unit = m.Groups["u"].Value;
        if (unit.EndsWith("/s", StringComparison.Ordinal)) unit = unit[..^2];
        if (unit.Length == 0) return v;
        if (Units.TryGetValue(unit, out var mul)) return v * mul;
        return unit is "%" or "°C" or "°F" or "W" or "V" or "A" or "RPM" ? v : null;
    }

    private static int Natural(string a, string b)
    {
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
            {
                var si = i; while (i < a.Length && char.IsDigit(a[i])) i++;
                var sj = j; while (j < b.Length && char.IsDigit(b[j])) j++;
                var na = a[si..i].TrimStart('0');
                var nb = b[sj..j].TrimStart('0');
                if (na.Length != nb.Length) return na.Length.CompareTo(nb.Length);
                var c = string.CompareOrdinal(na, nb);
                if (c != 0) return c;
            }
            else
            {
                var c = string.Compare(a[i].ToString(), b[j].ToString(), CultureInfo.CurrentCulture, CompareOptions.IgnoreCase);
                if (c != 0) return c;
                i++; j++;
            }
        }
        return (a.Length - i).CompareTo(b.Length - j);
    }

    [GeneratedRegex(@"^(?<n>[-+]?\d[\d  ]*(?:[.,]\d+)?)\s*(?<u>[A-Za-zµ°%/]*)$")]
    private static partial Regex QuantityRegex();
}
