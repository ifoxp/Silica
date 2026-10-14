using System.Text.RegularExpressions;
using Microsoft.Win32;
using Silica.Core.Model;
using Silica.Core.Util;

namespace Silica.Core.Info.Software;

public sealed partial class FontsProvider : IInfoProvider
{
    private const string FontsKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts";

    public string Id => "fonts";
    public string Title => "Fonts";
    public InfoCategory Category => InfoCategory.Software;
    public int Order => 110;
    public string Icon => "\uE8D2";

    public InfoPage Collect(CancellationToken ct)
    {
        var b = new PageBuilder(Id, Title);
        var fonts = new List<(string Name, string Type, string File, string Scope)>();
        b.Try("System fonts", () => Read(fonts, Registry.LocalMachine, "System"));
        b.Try("User fonts", () => Read(fonts, Registry.CurrentUser, "User"));

        b.Group("Summary")
            .Add("Fonts", Fmt.Number(fonts.Count))
            .Add("System", Fmt.Number(fonts.Count(f => f.Scope == "System")))
            .Add("User", Fmt.Number(fonts.Count(f => f.Scope == "User")));

        var t = b.Table("Fonts", "Name", "Type", "File", "Scope");
        foreach (var f in fonts.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase))
            t.Row(f.Name, f.Type, f.File, f.Scope);
        return b.Build();
    }

    private static void Read(List<(string, string, string, string)> list, RegistryKey root, string scope)
    {
        using var key = Reg.Open(root, FontsKey);
        if (key == null) return;
        foreach (var value in key.GetValueNames())
        {
            if (key.GetValue(value) is not string file || file.Length == 0) continue;
            var m = TypeSuffix().Match(value);
            var name = m.Success ? value[..m.Index].Trim() : value.Trim();
            var type = m.Success ? (m.Groups[1].Value.EndsWith("res", StringComparison.OrdinalIgnoreCase) ? "Bitmap" : m.Groups[1].Value) : Path.GetExtension(file).ToLowerInvariant() switch
            {
                ".fon" => "Bitmap",
                ".ttf" or ".ttc" => "TrueType",
                ".otf" => "OpenType",
                _ => ""
            };
            list.Add((name, type, file, scope));
        }
    }

    [GeneratedRegex(@"\((TrueType|OpenType|Type 1|VGA res|All res)\)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex TypeSuffix();
}
