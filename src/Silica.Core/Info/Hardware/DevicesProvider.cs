using Silica.Core.Model;
using Silica.Core.Util;

namespace Silica.Core.Info.Hardware;

public sealed class DevicesProvider : IInfoProvider
{
    public string Id => "devices";
    public string Title => "Devices";
    public InfoCategory Category => InfoCategory.Hardware;
    public int Order => 90;
    public string Icon => "";

    public InfoPage Collect(CancellationToken ct)
    {
        var b = new PageBuilder(Id, Title);
        var devices = Wmi.Query("SELECT PNPClass, Name, Manufacturer, Status, Service, PNPDeviceID, ConfigManagerErrorCode FROM Win32_PnPEntity")
            .OrderBy(r => r.Str("PNPClass") ?? "￿", StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(r => r.Str("Name"), StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (devices.Count == 0)
        {
            b.Warn("No devices were reported by WMI");
            return b.Build();
        }
        var problems = devices.Where(r => r.Int("ConfigManagerErrorCode") is > 0).ToList();

        b.Try("Summary", () =>
        {
            var g = b.Group("Summary");
            g.Add("Devices", devices.Count);
            g.Add("Device classes", devices.Select(r => r.Str("PNPClass")).Where(c => c != null).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            g.Add("Problem devices", problems.Count);
        });

        b.Try("Problem devices", () =>
        {
            var t = b.Table("Problem devices", "Class", "Name", "Problem", "Error code", "Device ID");
            foreach (var r in problems)
            {
                var code = r.Int("ConfigManagerErrorCode")!.Value;
                t.Row(r.Str("PNPClass"), r.Str("Name"), Problem(code), code, r.Str("PNPDeviceID"));
            }
        });

        b.Try("All devices", () =>
        {
            var t = b.Table("All devices", "Class", "Name", "Manufacturer", "Status", "Driver", "Device ID");
            foreach (var r in devices)
                t.Row(r.Str("PNPClass"), r.Str("Name"), r.Str("Manufacturer"), r.Str("Status"), r.Str("Service"), r.Str("PNPDeviceID"));
        });

        return b.Build();
    }

    internal static string Problem(int code) => code switch
    {
        1 => "Not configured correctly",
        3 => "Driver may be corrupted",
        10 => "Cannot start",
        12 => "Not enough free resources",
        14 => "Restart required",
        18 => "Drivers need to be reinstalled",
        19 => "Registry configuration is incomplete or damaged",
        21 => "Device is being removed",
        22 => "Disabled",
        24 => "Not present or not working",
        28 => "Drivers not installed",
        29 => "Disabled by firmware",
        31 => "Not working properly",
        32 => "Driver service disabled",
        37 => "Driver failed to initialize",
        38 => "Previous driver instance still loaded",
        39 => "Driver is corrupted or missing",
        40 => "Driver service key is invalid",
        41 => "Driver loaded but device not found",
        43 => "Device reported problems",
        44 => "Stopped by an application or service",
        45 => "Not connected",
        47 => "Prepared for safe removal",
        48 => "Driver blocked from starting",
        49 => "Registry size limit exceeded",
        52 => "Driver signature cannot be verified",
        _ => Fmt.T("Error {0}", code)
    };
}
