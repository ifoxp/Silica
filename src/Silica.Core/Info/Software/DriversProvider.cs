using Microsoft.Win32;
using Silica.Core.Model;
using Silica.Core.Util;

namespace Silica.Core.Info.Software;

public sealed class DriversProvider : IInfoProvider
{
    public string Id => "drivers";
    public string Title => "Drivers";
    public InfoCategory Category => InfoCategory.Software;
    public int Order => 50;
    public string Icon => "\uE950";

    public InfoPage Collect(CancellationToken ct)
    {
        var b = new PageBuilder(Id, Title);
        var summary = b.Group("Summary");

        b.Try("Device drivers", () =>
        {
            var classNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var rows = Wmi.Query("SELECT DeviceName, DeviceClass, DriverProviderName, DriverVersion, DriverDate, InfName, IsSigned, ClassGuid FROM Win32_PnPSignedDriver")
                .Where(r => r.Str("DeviceName") != null && (r.Str("DriverVersion") != null || r.Str("InfName") != null))
                .Select(r => (Row: r, Class: ClassName(classNames, r.Str("ClassGuid"), r.Str("DeviceClass"))))
                .OrderBy(x => x.Class.Length == 0 ? "~" : x.Class, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(x => x.Row.Str("DeviceName"), StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            summary.Add("Device drivers", Fmt.Number(rows.Count))
                .Add("Unsigned", Fmt.Number(rows.Count(x => x.Row.Bool("IsSigned") == false)));

            var t = b.Table("Device drivers", "Device", "Class", "Provider", "Version", "Date", "INF", "Signed");
            foreach (var (r, cls) in rows)
            {
                var signed = r.Bool("IsSigned");
                t.Row(r.Str("DeviceName"), cls, r.Str("DriverProviderName"), r.Str("DriverVersion"),
                    Fmt.Date(r.Date("DriverDate")), r.Str("InfName"), signed == null ? "" : signed.Value ? "Yes" : "No");
            }
        });

        b.Try("Kernel drivers", () =>
        {
            var rows = Wmi.Query("SELECT Name, DisplayName, State, StartMode, PathName FROM Win32_SystemDriver")
                .OrderBy(r => r.Str("Name"), StringComparer.OrdinalIgnoreCase)
                .ToList();
            summary.Add("Kernel drivers", Fmt.Number(rows.Count))
                .Add("Running", Fmt.Number(rows.Count(r => r.Str("State") == "Running")));

            var t = b.Table("Kernel drivers", "Name", "Display name", "State", "Start mode", "Path");
            foreach (var r in rows)
            {
                t.Row(r.Str("Name"), r.Str("DisplayName"), r.Str("State"), ServicesProvider.StartMode(r.Str("StartMode")),
                    SoftwareHelpers.NormalizeImagePath(r.Str("PathName")));
            }
        });

        return b.Build();
    }

    // WMI reports bare upper-case class names (DISPLAY). The class key has the Device Manager name as
    // "@inf,%key%;English text"; the English text after ';' keeps the report language-neutral.
    private static string ClassName(Dictionary<string, string> cache, string? guid, string? fallback)
    {
        if (guid == null) return fallback ?? "";
        if (!cache.TryGetValue(guid, out var name))
        {
            var key = @"SYSTEM\CurrentControlSet\Control\Class\" + guid;
            var desc = Reg.Str(Registry.LocalMachine, key, "ClassDesc");
            name = desc is { } d && d.LastIndexOf(';') is var i and >= 0 ? d[(i + 1)..].Trim()
                : desc is { } plain && !plain.StartsWith('@') ? plain
                : Reg.Str(Registry.LocalMachine, key, "Class") ?? fallback ?? "";
            cache[guid] = name;
        }
        return name;
    }
}
