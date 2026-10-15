using System.Globalization;
using System.Runtime.InteropServices;
using Silica.Core.Model;
using Silica.Core.Util;

namespace Silica.Core.Info.Hardware;

public sealed class DisplaysProvider : IInfoProvider
{
    public string Id => "displays";
    public string Title => "Monitors";
    public InfoCategory Category => InfoCategory.Hardware;
    public int Order => 45;
    public string Icon => "\uE7F4";

    internal sealed record Output(string Device, string Adapter, string? MonitorName, string? MonitorInstance, bool Primary,
        Native.DevMode Mode, uint? Dpi);

    internal sealed record WmiMonitor(string Instance, string? Name, string? Vendor, string? ProductCode, string? Serial,
        int? Week, int? Year, bool Active);

    public InfoPage Collect(CancellationToken ct)
    {
        var b = new PageBuilder(Id, Title);
        var outputs = new List<Output>();
        b.Try("Display outputs", () => outputs = Outputs());
        var monitors = Monitors();
        var size = ByInstance("SELECT InstanceName, MaxHorizontalImageSize, MaxVerticalImageSize, VideoInputType FROM WmiMonitorBasicDisplayParams");
        var conn = ByInstance("SELECT InstanceName, VideoOutputTechnology FROM WmiMonitorConnectionParams");

        var used = new HashSet<Output>();
        Output? OutputOf(WmiMonitor m) =>
            outputs.FirstOrDefault(x => string.Equals(x.MonitorInstance, m.Instance, StringComparison.OrdinalIgnoreCase));

        foreach (var m in monitors.OrderByDescending(m => OutputOf(m)?.Primary == true).ThenByDescending(m => OutputOf(m) != null))
        {
            var o = OutputOf(m);
            if (o != null) used.Add(o);
            else if (!m.Active) continue;
            b.Try(m.Name ?? m.Instance, () =>
            {
                var g = b.Group(m.Name ?? o?.MonitorName ?? "Monitor", localizeTitle: false);
                g.Add("Manufacturer", Hw.PnpVendor(m.Vendor));
                g.Add("Product code", m.ProductCode);
                g.Add("Serial number", m.Serial);
                if (m.Year is > 1990)
                    g.Add("Manufactured", m.Week is > 0 and < 54 ? Fmt.T("{0}, week {1}", m.Year, m.Week) : m.Year.ToString());
                if (size.TryGetValue(m.Instance, out var s))
                {
                    var hcm = s.Long("MaxHorizontalImageSize") ?? 0;
                    var vcm = s.Long("MaxVerticalImageSize") ?? 0;
                    if (hcm > 0 && vcm > 0)
                    {
                        var inches = Math.Sqrt(hcm * hcm + vcm * vcm) / 2.54;
                        g.Add("Screen size", $"{hcm} × {vcm} cm ({inches.ToString("0.0", CultureInfo.CurrentCulture)}\")");
                    }
                    g.Word("Input type", s.Long("VideoInputType") switch { 1 => "Digital", 0 => "Analog", _ => null });
                }
                if (conn.TryGetValue(m.Instance, out var c)) g.Add("Connection", Hw.VideoOutput(c.Long("VideoOutputTechnology")));
                if (o != null) AddOutput(g, o);
                else g.Word("Status", "Disabled");
            });
        }

        foreach (var o in outputs.Where(o => !used.Contains(o)))
            b.Try(o.Device, () => AddOutput(b.Group(o.MonitorName ?? o.Device, localizeTitle: false), o));

        b.Try("Supported modes", () =>
        {
            var primary = outputs.FirstOrDefault(o => o.Primary) ?? outputs.FirstOrDefault();
            if (primary == null) return;
            var t = b.Table("Supported modes", "Resolution", "Max refresh", "Refresh rates");
            foreach (var (w, h, rates) in Modes(primary.Device))
                t.Row(Hw.Resolution(w, h), $"{rates.Max()} {Fmt.Unit("Hz")}", string.Join(", ", rates) + " " + Fmt.Unit("Hz"));
        });

        if (outputs.Count == 0 && monitors.Count == 0) b.Warn("No monitors were detected");
        return b.Build();
    }

    private static void AddOutput(PropertyBuilder g, Output o)
    {
        var m = o.Mode;
        g.Add("Display", o.Device.Replace(@"\\.\", "", StringComparison.Ordinal));
        g.Add("Adapter", o.Adapter);
        if (m.PelsWidth > 0)
        {
            g.Add("Resolution", Hw.Resolution(m.PelsWidth, m.PelsHeight));
            g.Add("Refresh rate", m.DisplayFrequency > 1 ? $"{m.DisplayFrequency} {Fmt.Unit("Hz")}" : null);
            g.Add("Color depth", m.BitsPerPel > 0 ? Fmt.T("{0}-bit", m.BitsPerPel) : null);
            g.Add("Position", $"{m.PositionX}, {m.PositionY}");
            g.Word("Orientation", m.DisplayOrientation switch { 0 => "Landscape", 1 => "Portrait", 2 => "Landscape (flipped)", 3 => "Portrait (flipped)", _ => null });
        }
        if (o.Dpi is > 0 and var dpi) g.Add("Scaling", $"{Math.Round(dpi * 100.0 / 96)} % ({dpi} DPI)");
        g.Flag("Primary", o.Primary);
    }

    private static Dictionary<string, WmiRow> ByInstance(string wql)
    {
        var d = new Dictionary<string, WmiRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in Wmi.Query(wql, Hw.WmiNs)) d.TryAdd(Key(r.Str("InstanceName")), r);
        return d;
    }

    internal static string Key(string? wmiInstance)
    {
        // WMI instance names carry a "_0" suffix that the PnP instance id does not.
        var s = wmiInstance ?? "";
        var i = s.LastIndexOf('_');
        return i > 0 && s[(i + 1)..].All(char.IsDigit) ? s[..i] : s;
    }

    internal static List<WmiMonitor> Monitors() =>
        Wmi.Query("SELECT InstanceName, Active, ManufacturerName, ProductCodeID, SerialNumberID, UserFriendlyName, " +
                  "WeekOfManufacture, YearOfManufacture FROM WmiMonitorID", Hw.WmiNs)
            .Select(r => new WmiMonitor(Key(r.Str("InstanceName")), Hw.WmiChars(r["UserFriendlyName"]),
                Hw.WmiChars(r["ManufacturerName"]), Hw.WmiChars(r["ProductCodeID"]),
                Hw.WmiChars(r["SerialNumberID"]) is { } sn && sn.Trim('0') != "" ? sn : null,
                r.Int("WeekOfManufacture"), r.Int("YearOfManufacture"), r.Bool("Active") ?? true))
            .ToList();

    internal static List<Output> Outputs()
    {
        var dpis = MonitorDpis();
        var list = new List<Output>();
        var adapter = Native.DisplayDevice.Create();
        for (uint i = 0; Native.EnumDisplayDevices(null, i, ref adapter, 0); i++, adapter = Native.DisplayDevice.Create())
        {
            if ((adapter.StateFlags & Native.DisplayDeviceAttachedToDesktop) == 0 ||
                (adapter.StateFlags & Native.DisplayDeviceMirroring) != 0) continue;

            var mode = Native.DevMode.Create();
            if (!Native.EnumDisplaySettingsEx(adapter.DeviceName, Native.EnumCurrentSettings, ref mode, 0)) mode = default;

            var primary = (adapter.StateFlags & Native.DisplayDevicePrimary) != 0;
            uint? dpi = dpis.TryGetValue(adapter.DeviceName, out var d) ? d : null;
            var count = list.Count;
            // In duplicate mode several monitors share one source, so each active monitor gets its own entry.
            var mon = Native.DisplayDevice.Create();
            for (uint j = 0; Native.EnumDisplayDevices(adapter.DeviceName, j, ref mon, Native.EddGetDeviceInterfaceName); j++, mon = Native.DisplayDevice.Create())
            {
                if ((mon.StateFlags & Native.MonitorActive) != 0)
                    list.Add(new Output(adapter.DeviceName, adapter.DeviceString, mon.DeviceString, InstanceFromInterface(mon.DeviceId), primary, mode, dpi));
            }
            if (list.Count == count)
                list.Add(new Output(adapter.DeviceName, adapter.DeviceString, null, null, primary, mode, dpi));
        }
        return list;
    }

    // \\?\DISPLAY#ACR0B61#5&2e6bed42&0&UID4352#{e6f07b5f-...} -> DISPLAY\ACR0B61\5&2e6bed42&0&UID4352
    private static string? InstanceFromInterface(string? path)
    {
        if (string.IsNullOrEmpty(path) || !path.StartsWith(@"\\?\", StringComparison.Ordinal)) return null;
        var end = path.LastIndexOf("#{", StringComparison.Ordinal);
        return (end > 4 ? path[4..end] : path[4..]).Replace('#', '\\');
    }

    private static Dictionary<string, uint> MonitorDpis()
    {
        var result = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        var previous = IntPtr.Zero;
        try
        {
            // Without per-monitor awareness GetDpiForMonitor reports the system DPI for every monitor.
            try { previous = Native.SetThreadDpiAwarenessContext(new IntPtr(-4)); }
            catch (EntryPointNotFoundException) { }

            Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (h, _, _, _) =>
            {
                var info = new Native.MonitorInfoEx { Size = Marshal.SizeOf<Native.MonitorInfoEx>() };
                if (Native.GetMonitorInfo(h, ref info) && Native.GetDpiForMonitor(h, 0, out var x, out _) == 0)
                    result[info.Device] = x;
                return true;
            }, IntPtr.Zero);
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
        finally
        {
            if (previous != IntPtr.Zero) Native.SetThreadDpiAwarenessContext(previous);
        }
        return result;
    }

    internal static IEnumerable<(uint W, uint H, List<uint> Rates)> Modes(string device)
    {
        var modes = new Dictionary<(uint, uint), SortedSet<uint>>();
        var dm = Native.DevMode.Create();
        for (var i = 0; Native.EnumDisplaySettingsEx(device, i, ref dm, 0); i++, dm = Native.DevMode.Create())
        {
            if (dm.PelsWidth == 0 || dm.DisplayFrequency <= 1) continue;
            if (!modes.TryGetValue((dm.PelsWidth, dm.PelsHeight), out var set))
                modes[(dm.PelsWidth, dm.PelsHeight)] = set = [];
            set.Add(dm.DisplayFrequency);
        }
        return modes
            .OrderByDescending(kv => (ulong)kv.Key.Item1 * kv.Key.Item2).ThenByDescending(kv => kv.Key.Item1)
            .Select(kv => (kv.Key.Item1, kv.Key.Item2, kv.Value.Reverse().ToList()));
    }
}
