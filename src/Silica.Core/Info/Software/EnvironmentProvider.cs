using System.Globalization;
using Microsoft.Win32;
using Silica.Core.Model;
using Silica.Core.Util;

namespace Silica.Core.Info.Software;

public sealed class EnvironmentProvider : IInfoProvider
{
    public string Id => "environment";
    public string Title => "Environment";
    public InfoCategory Category => InfoCategory.Software;
    public int Order => 90;
    public string Icon => "\uE909";

    public InfoPage Collect(CancellationToken ct)
    {
        var b = new PageBuilder(Id, Title);

        b.Try("Regional settings", () =>
        {
            var c = CultureInfo.CurrentCulture;
            var region = RegionInfo.CurrentRegion;
            var f = c.DateTimeFormat;
            b.Group("Regional settings")
                .Add("Locale", OsProvider.CultureLabel(c))
                .Add("UI language", OsProvider.CultureLabel(CultureInfo.CurrentUICulture))
                .Add("Region", $"{region.DisplayName} ({region.Name})")
                .Add("Time zone", TimeZoneInfo.Local.DisplayName)
                .Add("Short date format", f.ShortDatePattern)
                .Add("Long date format", f.LongDatePattern)
                .Add("Time format", f.LongTimePattern)
                .Add("Decimal separator", c.NumberFormat.NumberDecimalSeparator)
                .Add("Digit grouping", Visible(c.NumberFormat.NumberGroupSeparator))
                .Add("Currency", $"{c.NumberFormat.CurrencySymbol} ({region.ISOCurrencySymbol})")
                .Word("Measurement system", region.IsMetric ? "Metric" : "US")
                .Word("First day of week", f.FirstDayOfWeek.ToString())
                .Add("Keyboard layouts", string.Join(", ", KeyboardLayouts()));
        });

        b.Try("System variables", () =>
            Variables(b, "System variables", Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment"));
        b.Try("User variables", () =>
            Variables(b, "User variables", Registry.CurrentUser, "Environment"));

        return b.Build();
    }

    private static string Visible(string s) => s switch
    {
        " " => "Space",
        " " or " " => "Non-breaking space",
        _ => s
    };

    private static void Variables(PageBuilder b, string title, RegistryKey root, string path)
    {
        using var key = Reg.Open(root, path);
        if (key == null) return;
        var t = b.Table(title, "Name", "Value");
        foreach (var name in key.GetValueNames().Order(StringComparer.OrdinalIgnoreCase))
        {
            var value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            t.Row(name, value is string[] multi ? string.Join("; ", multi) : value);
        }
    }

    private static IEnumerable<string> KeyboardLayouts()
    {
        using var key = Reg.Open(Registry.CurrentUser, @"Keyboard Layout\Preload");
        if (key == null) yield break;
        var seen = new HashSet<int>();
        foreach (var name in key.GetValueNames().OrderBy(n => int.TryParse(n, out var i) ? i : int.MaxValue))
        {
            // Values are KLIDs like "00000409"; the low word is the language id.
            if (key.GetValue(name) is not string klid || !int.TryParse(klid, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var id)) continue;
            var lang = id & 0xFFFF;
            if (!seen.Add(lang)) continue;
            string label;
            try { label = OsProvider.CultureLabel(CultureInfo.GetCultureInfo(lang)); }
            catch (CultureNotFoundException) { label = klid; }
            yield return label;
        }
    }
}
