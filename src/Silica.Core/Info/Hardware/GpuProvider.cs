using Microsoft.Win32;
using Silica.Core.Model;
using Silica.Core.Util;

namespace Silica.Core.Info.Hardware;

public sealed class GpuProvider : IInfoProvider
{
    public string Id => "gpu";
    public string Title => "Graphics";
    public InfoCategory Category => InfoCategory.Hardware;
    public int Order => 40;
    public string Icon => "";

    internal const string Query = "SELECT Name, VideoProcessor, AdapterCompatibility, AdapterRAM, DriverVersion, DriverDate, " +
                                  "InfFilename, InfSection, CurrentHorizontalResolution, CurrentVerticalResolution, " +
                                  "CurrentRefreshRate, CurrentBitsPerPixel, PNPDeviceID, Status FROM Win32_VideoController";

    public InfoPage Collect(CancellationToken ct)
    {
        var b = new PageBuilder(Id, Title);
        var adapters = Wmi.Query(Query).OrderBy(r => Hw.IsVirtualGpu(r.Str("PNPDeviceID"))).ToList();
        if (adapters.Count == 0) b.Warn("No display adapters were reported by WMI");

        foreach (var r in adapters)
        {
            ct.ThrowIfCancellationRequested();
            var name = r.Str("Name") ?? "Display adapter";
            b.Try(name, () => AddAdapter(b, r, name));
        }
        return b.Build();
    }

    private static void AddAdapter(PageBuilder b, WmiRow r, string name)
    {
        var pnp = r.Str("PNPDeviceID");
        var key = Hw.DriverKey(pnp);
        string? Drv(string value) => key == null ? null : Hw.RegText(Reg.Get(Registry.LocalMachine, key, value));

        var g = b.Group(name, localizeTitle: false);
        g.Add("Chip", Drv("HardwareInformation.ChipType") ?? r.Str("VideoProcessor"));
        g.Add("Vendor", r.Str("AdapterCompatibility") ?? Drv("ProviderName"));
        g.Add("Video memory", Fmt.Bytes(Hw.GpuMemory(pnp, r.Long("AdapterRAM"))));
        g.Add("DAC type", Drv("HardwareInformation.DacType"));
        var vbios = Drv("HardwareInformation.BiosString");
        if (vbios != null && vbios.StartsWith("Version", StringComparison.OrdinalIgnoreCase) && vbios.Length > 7 && char.IsDigit(vbios[7]))
            vbios = vbios[7..];
        g.Add("Video BIOS", vbios);
        g.Word("Adapter type", Hw.IsVirtualGpu(pnp) ? "Virtual" : null);

        var w = r.Long("CurrentHorizontalResolution");
        var h = r.Long("CurrentVerticalResolution");
        if (w is > 0 && h is > 0)
        {
            var mode = Hw.Resolution(w.Value, h.Value);
            if (r.Long("CurrentRefreshRate") is > 1 and var hz) mode += $" @ {hz} Hz";
            if (r.Long("CurrentBitsPerPixel") is > 0 and var bits) mode += $", {bits}-bit";
            g.Add("Current mode", mode);
        }

        g.Add("Driver version", r.Str("DriverVersion"));
        g.Add("Driver date", Fmt.Date(r.Date("DriverDate")));
        g.Add("Driver provider", Drv("ProviderName"));
        g.Add("Driver INF", Hw.Join([r.Str("InfFilename"), r.Str("InfSection")], " / "));

        var (ven, dev, subsys, rev) = Hw.PciIds(pnp);
        g.Add("Vendor ID", ven);
        g.Add("Device ID", dev);
        g.Add("Subsystem ID", subsys);
        g.Add("Revision", rev);
        g.Add("Bus location", Hw.DeviceLocation(pnp));
        g.Word("Status", r.Str("Status"));
    }
}
