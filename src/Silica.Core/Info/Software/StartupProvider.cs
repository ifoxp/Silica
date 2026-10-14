using Microsoft.Win32;
using Silica.Core.Model;
using Silica.Core.Util;

namespace Silica.Core.Info.Software;

public sealed class StartupProvider : IInfoProvider
{
    private const string CurrentVersion = @"SOFTWARE\Microsoft\Windows\CurrentVersion";
    private const string Wow = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion";
    private const string Approved = CurrentVersion + @"\Explorer\StartupApproved\";

    public string Id => "startup";
    public string Title => "Startup";
    public InfoCategory Category => InfoCategory.Software;
    public int Order => 30;
    public string Icon => "\uE7E8";

    private sealed record Entry(string Name, string Command, string Location, bool Enabled);

    public InfoPage Collect(CancellationToken ct)
    {
        var b = new PageBuilder(Id, Title);
        var entries = new List<Entry>();
        var lm = Registry.LocalMachine;
        var cu = Registry.CurrentUser;

        b.Try("Registry", () =>
        {
            ReadRun(entries, lm, "HKLM", CurrentVersion + @"\Run", Approved + "Run");
            ReadRun(entries, lm, "HKLM", Wow + @"\Run", Approved + "Run32");
            ReadRun(entries, lm, "HKLM", CurrentVersion + @"\RunOnce", null);
            ReadRun(entries, lm, "HKLM", Wow + @"\RunOnce", null);
            ReadRun(entries, cu, "HKCU", CurrentVersion + @"\Run", Approved + "Run");
            ReadRun(entries, cu, "HKCU", Wow + @"\Run", Approved + "Run32");
            ReadRun(entries, cu, "HKCU", CurrentVersion + @"\RunOnce", null);
        });
        b.Try("Startup folders", () =>
        {
            ReadFolder(entries, Environment.GetFolderPath(Environment.SpecialFolder.Startup), cu);
            ReadFolder(entries, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), lm);
        });

        b.Group("Summary")
            .Add("Total", Fmt.Number(entries.Count))
            .Add("Enabled", Fmt.Number(entries.Count(e => e.Enabled)))
            .Add("Disabled", Fmt.Number(entries.Count(e => !e.Enabled)));

        var t = b.Table("Startup programs", "Name", "Command", "Location", "State");
        foreach (var e in entries.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase))
            t.Row(e.Name, e.Command, e.Location, e.Enabled ? "Enabled" : "Disabled");
        return b.Build();
    }

    private static void ReadRun(List<Entry> list, RegistryKey root, string hive, string path, string? approvedPath)
    {
        using var key = Reg.Open(root, path);
        if (key == null) return;
        foreach (var name in key.GetValueNames())
        {
            if (key.GetValue(name) is not string cmd || string.IsNullOrWhiteSpace(cmd)) continue;
            var enabled = approvedPath == null || IsApproved(root, approvedPath, name);
            list.Add(new Entry(name.Length == 0 ? "(default)" : name, cmd, $@"{hive}\{path}", enabled));
        }
    }

    private static void ReadFolder(List<Entry> list, string folder, RegistryKey approvedRoot)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
        foreach (var file in Directory.EnumerateFiles(folder))
        {
            var fileName = Path.GetFileName(file);
            if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
            list.Add(new Entry(Path.GetFileNameWithoutExtension(file), fileName, folder,
                IsApproved(approvedRoot, Approved + "StartupFolder", fileName)));
        }
    }

    // StartupApproved holds a binary flag per entry; an odd first byte (0x03, 0x07) means disabled in Task Manager.
    private static bool IsApproved(RegistryKey root, string path, string name) =>
        Reg.Get(root, path, name) is not byte[] { Length: > 0 } data || (data[0] & 1) == 0;
}
