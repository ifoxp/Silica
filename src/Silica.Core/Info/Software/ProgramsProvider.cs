using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml;
using Microsoft.Win32;
using Silica.Core.Model;
using Silica.Core.Util;

namespace Silica.Core.Info.Software;

public sealed class ProgramsProvider : IInfoProvider
{
    private const string Uninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string Packages = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

    public string Id => "programs";
    public string Title => "Installed programs";
    public InfoCategory Category => InfoCategory.Software;
    public int Order => 40;
    public string Icon => "\uE71D";

    private sealed record Program(string Name, string Version, string Publisher, DateTime? Installed, long? SizeKb, string Location);

    public InfoPage Collect(CancellationToken ct)
    {
        var b = new PageBuilder(Id, Title);
        var programs = new Dictionary<string, Program>(StringComparer.OrdinalIgnoreCase);
        var store = new List<(string Name, string Version, string Publisher, string Arch)>();

        b.Try("Installed programs", () =>
        {
            Read(programs, Registry.LocalMachine, Uninstall, RegistryView.Registry64);
            if (Environment.Is64BitOperatingSystem) Read(programs, Registry.LocalMachine, Uninstall, RegistryView.Registry32);
            Read(programs, Registry.CurrentUser, Uninstall, RegistryView.Registry64);
        });
        b.Try("Store apps", () => ReadStoreApps(store, ct));

        b.Group("Summary")
            .Add("Programs", Fmt.Number(programs.Count))
            .Add("Store apps", store.Count > 0 ? Fmt.Number(store.Count) : null);

        var t = b.Table("Installed programs", "Name", "Version", "Publisher", "Install date", "Size", "Location");
        foreach (var p in programs.Values.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            t.Row(p.Name, p.Version, p.Publisher, Fmt.Date(p.Installed),
                p.SizeKb is > 0 and var kb ? Fmt.Bytes(kb * 1024L) : "", p.Location);
        }

        var s = b.Table("Store apps", "Name", "Version", "Publisher", "Architecture");
        foreach (var a in store.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase))
            s.Row(a.Name, a.Version, a.Publisher, a.Arch);
        return b.Build();
    }

    private static void Read(Dictionary<string, Program> programs, RegistryKey root, string path, RegistryView view)
    {
        using var key = Reg.Open(root, path, view);
        if (key == null) return;
        foreach (var sub in key.GetSubKeyNames())
        {
            try
            {
                using var k = key.OpenSubKey(sub);
                if (k == null) continue;
                var name = Text(k, "DisplayName");
                if (name.Length == 0) continue;
                if (k.GetValue("SystemComponent") is int sc && sc == 1) continue;
                if (k.GetValue("ParentKeyName") != null) continue;

                var version = Text(k, "DisplayVersion");
                var id = name + "|" + version;
                if (programs.ContainsKey(id)) continue;

                programs[id] = new Program(name, version, Text(k, "Publisher"), ParseDate(Text(k, "InstallDate")),
                    k.GetValue("EstimatedSize") is int size ? size : null, Text(k, "InstallLocation").TrimEnd('\\'));
            }
            catch { }
        }
    }

    // Some installers write strings padded with NULs and leftover bytes.
    private static string Text(RegistryKey key, string name) =>
        key.GetValue(name) is string s ? s.Split('\0', 2)[0].Trim() : "";

    private static DateTime? ParseDate(string s) =>
        DateTime.TryParseExact(s, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    private static void ReadStoreApps(List<(string, string, string, string)> list, CancellationToken ct)
    {
        using var key = Reg.Open(Registry.CurrentUser, Packages);
        if (key == null) return;
        var systemApps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SystemApps");
        var newest = new Dictionary<string, (Version Ver, string FullName, string Root, string Display, string Arch)>(StringComparer.OrdinalIgnoreCase);

        foreach (var fullName in key.GetSubKeyNames())
        {
            // Package full name: Name_Version_Architecture_ResourceId_PublisherId
            var parts = fullName.Split('_');
            if (parts.Length != 5 || parts[3].Length > 0) continue;
            using var k = key.OpenSubKey(fullName);
            if (k == null || k.GetValue("Framework") is int fw && fw != 0) continue;
            var root = k.GetValue("PackageRootFolder") as string ?? "";
            if (root.StartsWith(systemApps, StringComparison.OrdinalIgnoreCase)) continue;
            if (!Version.TryParse(parts[1], out var ver)) continue;
            if (newest.TryGetValue(parts[0], out var prev) && prev.Ver >= ver) continue;
            newest[parts[0]] = (ver, fullName, root, k.GetValue("DisplayName") as string ?? "", parts[2]);
        }

        var budget = System.Diagnostics.Stopwatch.StartNew();
        foreach (var (name, p) in newest)
        {
            ct.ThrowIfCancellationRequested();
            var display = p.Display;
            if (display.Contains("ms-resource:", StringComparison.OrdinalIgnoreCase))
                display = budget.ElapsedMilliseconds < 3000 ? ResolveResource(display, p.FullName, name) ?? name : name;
            if (string.IsNullOrWhiteSpace(display)) display = name;
            list.Add((display, p.Ver.ToString(), ManifestPublisher(p.Root) ?? "", p.Arch));
        }
    }

    private static string? ResolveResource(string value, string fullName, string name)
    {
        var source = value;
        if (!value.StartsWith('@'))
        {
            var res = value["ms-resource:".Length..];
            var uri = res.StartsWith("//") ? "ms-resource:" + res
                : res.StartsWith('/') ? $"ms-resource://{name}{res}"
                : $"ms-resource://{name}/Resources/{res}";
            source = $"@{{{fullName}?{uri}}}";
        }
        var sb = new StringBuilder(512);
        return SHLoadIndirectString(source, sb, sb.Capacity, IntPtr.Zero) == 0 && sb.Length > 0 && !sb.ToString().StartsWith("ms-resource:")
            ? sb.ToString()
            : null;
    }

    private static string? ManifestPublisher(string root)
    {
        try
        {
            var manifest = Path.Combine(root, "AppxManifest.xml");
            if (!File.Exists(manifest)) return null;
            using var reader = XmlReader.Create(manifest, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, IgnoreComments = true });
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "PublisherDisplayName")
                {
                    var v = reader.ReadElementContentAsString().Trim();
                    return v.StartsWith("ms-resource:") ? null : v;
                }
            }
        }
        catch { }
        return null;
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int SHLoadIndirectString(string source, StringBuilder output, int size, IntPtr reserved);
}
