using Silica.Core.Model;
using Silica.Core.Util;

namespace Silica.Core.Info.Hardware;

public sealed class UsbProvider : IInfoProvider
{
    public string Id => "usb";
    public string Title => "USB";
    public InfoCategory Category => InfoCategory.Hardware;
    public int Order => 95;
    public string Icon => "";

    public InfoPage Collect(CancellationToken ct)
    {
        var b = new PageBuilder(Id, Title);
        var controllers = Wmi.Query("SELECT Name, Manufacturer, Status, PNPDeviceID FROM Win32_USBController")
            .OrderBy(r => r.Str("Name"), StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var devices = Wmi.Query(@"SELECT Name, Manufacturer, PNPClass, Status, PNPDeviceID, ConfigManagerErrorCode " +
                                @"FROM Win32_PnPEntity WHERE PNPDeviceID LIKE 'USB\\%'")
            .Select(r => (Row: r, VidPid: Hw.UsbVidPid(r.Str("PNPDeviceID"))))
            .OrderBy(d => d.VidPid == null)
            .ThenBy(d => d.VidPid, StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => d.Row.Str("Name"), StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var g = b.Group("Summary");
        g.Add("USB controllers", controllers.Count);
        g.Add("Root hubs", devices.Count(d => d.Row.Str("PNPDeviceID")?.StartsWith(@"USB\ROOT_HUB", StringComparison.OrdinalIgnoreCase) == true));
        g.Add("USB devices", devices.Count(d => d.VidPid != null));

        b.Try("USB controllers", () =>
        {
            var t = b.Table("USB controllers", "Name", "Manufacturer", "Vendor ID", "Device ID", "Status");
            foreach (var r in controllers)
            {
                var (ven, dev, _, _) = Hw.PciIds(r.Str("PNPDeviceID"));
                t.Row(r.Str("Name"), r.Str("Manufacturer"), ven, dev, r.Str("Status"));
            }
        });

        b.Try("USB devices", () =>
        {
            var t = b.Table("USB devices", "Name", "Manufacturer", "Class", "VID:PID", "Status", "Device ID");
            foreach (var (r, vidPid) in devices)
            {
                var status = r.Int("ConfigManagerErrorCode") is > 0 and var code ? DevicesProvider.Problem(code) : r.Str("Status");
                t.Row(r.Str("Name"), r.Str("Manufacturer"), r.Str("PNPClass"), vidPid, status, r.Str("PNPDeviceID"));
            }
        });

        return b.Build();
    }
}
