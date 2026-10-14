using Microsoft.Win32;
using Silica.Core.Model;
using Silica.Core.Util;

namespace Silica.Core.Info.Software;

public sealed class SharesProvider : IInfoProvider
{
    public string Id => "shares";
    public string Title => "Shared resources";
    public InfoCategory Category => InfoCategory.Software;
    public int Order => 100;
    public string Icon => "\uE8F4";

    public InfoPage Collect(CancellationToken ct)
    {
        var b = new PageBuilder(Id, Title);

        b.Try("Shares", () =>
        {
            var t = b.Table("Shared folders", "Name", "Path", "Type", "Description");
            foreach (var s in Wmi.Query("SELECT Name, Path, Type, Description FROM Win32_Share").OrderBy(s => s.Str("Name"), StringComparer.OrdinalIgnoreCase))
                t.Row(s.Str("Name"), s.Str("Path"), ShareType(s.Long("Type")), s.Str("Description"));
        });

        b.Try("Network drives", () =>
        {
            var drives = new SortedDictionary<string, (string Remote, string State, string Size, string Free)>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Network))
            {
                var letter = d.Name.TrimEnd('\\');
                // An unreachable server can block these calls for a long time.
                var ready = SoftwareHelpers.TryRun(() => Space(d),
                    TimeSpan.FromSeconds(3), out var space) && space != null;
                var remote = Reg.Str(Registry.CurrentUser, @"Network\" + letter[0], "RemotePath") ?? "";
                drives[letter] = (remote, ready ? "Connected" : "Unavailable",
                    ready ? Fmt.Bytes((double)space!.Value.Total) : "", ready ? Fmt.Bytes((double)space!.Value.Free) : "");
            }
            // Persistent mappings that are not connected in this session.
            foreach (var letter in Reg.SubKeys(Registry.CurrentUser, "Network"))
            {
                var key = letter.ToUpperInvariant() + ":";
                if (!drives.ContainsKey(key))
                    drives[key] = (Reg.Str(Registry.CurrentUser, @"Network\" + letter, "RemotePath") ?? "", "Disconnected", "", "");
            }
            foreach (var m in Wmi.Query("SELECT Name, ProviderName FROM Win32_MappedLogicalDisk"))
            {
                if (m.Str("Name") is { } n && drives.TryGetValue(n, out var e) && e.Remote.Length == 0)
                    drives[n] = e with { Remote = m.Str("ProviderName") ?? "" };
            }

            var t = b.Table("Network drives", "Drive", "Remote path", "State", "Size", "Free");
            foreach (var (letter, d) in drives) t.Row(letter, d.Remote, d.State, d.Size, d.Free);
        });

        return b.Build();
    }

    private static (long Total, long Free)? Space(DriveInfo d)
    {
        try { return d.IsReady ? (d.TotalSize, d.AvailableFreeSpace) : null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static string ShareType(long? type)
    {
        if (type == null) return "";
        var admin = (type.Value & 0x80000000) != 0;
        var kind = (type.Value & 0xFF) switch
        {
            0 => "Disk drive",
            1 => "Print queue",
            2 => "Device",
            3 => "IPC",
            _ => "Other"
        };
        return admin ? kind + " (admin)" : kind;
    }
}
