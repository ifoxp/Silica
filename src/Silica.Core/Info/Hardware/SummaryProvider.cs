using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Silica.Core.Model;
using Silica.Core.Util;

namespace Silica.Core.Info.Hardware;

public sealed class SummaryProvider : IInfoProvider
{
    public string Id => "summary";
    public string Title => "Summary";
    public InfoCategory Category => InfoCategory.Hardware;
    public int Order => 0;
    public string Icon => "";

    public InfoPage Collect(CancellationToken ct)
    {
        var b = new PageBuilder(Id, Title);

        b.Try("System", () =>
        {
            var cs = Wmi.First("SELECT Manufacturer, Model FROM Win32_ComputerSystem");
            var g = b.Group("System");
            g.Add("Computer name", Environment.MachineName);
            g.Add("User", Environment.UserName);
            g.Add("Operating system", Hw.OsName());
            g.Add("Uptime", Fmt.Duration(TimeSpan.FromMilliseconds(Environment.TickCount64)));
            // Self-built PCs repeat the motherboard name (or an OEM placeholder) as the computer model.
            var model = Hw.Join([cs?.Str("Manufacturer"), cs?.Str("Model")], " ");
            var board = Wmi.First("SELECT Manufacturer, Product FROM Win32_BaseBoard");
            if (!model.Contains("O.E.M.", StringComparison.OrdinalIgnoreCase) &&
                !model.Contains("System Product Name", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(model, Hw.Join([board?.Str("Manufacturer"), board?.Str("Product")], " "), StringComparison.OrdinalIgnoreCase))
                g.Add("Computer model", model);
            g.Word("BIOS mode", MotherboardProvider.FirmwareType());
            g.Word("Secure Boot", MotherboardProvider.SecureBoot());
        });

        b.Try("Hardware", () =>
        {
            var g = b.Group("Hardware");
            var cpu = Wmi.First("SELECT Name, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor");
            if (cpu?.Str("Name") is { } cpuName)
                g.Add("Processor", $"{cpuName} ({Fmt.Plural(cpu.Int("NumberOfCores") ?? 0, "{0} cores")}, {Fmt.Plural(cpu.Int("NumberOfLogicalProcessors") ?? 0, "{0} threads")})");

            var board = Wmi.First("SELECT Manufacturer, Product FROM Win32_BaseBoard");
            g.Add("Motherboard", Hw.Join([board?.Str("Manufacturer"), board?.Str("Product")], " "));
            var bios = Wmi.First("SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS");
            if (bios != null)
            {
                var date = bios.Date("ReleaseDate");
                g.Add("BIOS", Hw.Join([bios.Str("Manufacturer"), bios.Str("SMBIOSBIOSVersion"), date == null ? null : $"({Fmt.Date(date)})"], " "));
            }

            var ms = new Native.MemoryStatusEx { Length = (uint)Marshal.SizeOf<Native.MemoryStatusEx>() };
            var total = Native.GlobalMemoryStatusEx(ref ms) ? Fmt.Bytes((double)ms.TotalPhys) : null;
            var config = MemoryProvider.ModuleSummary(MemoryProvider.Modules());
            g.Add("Memory", config == null ? total : $"{total} ({config})");

            foreach (var gpu in Wmi.Query(GpuProvider.Query).Where(r => !Hw.IsVirtualGpu(r.Str("PNPDeviceID"))))
            {
                var vram = Fmt.Bytes(Hw.GpuMemory(gpu.Str("PNPDeviceID"), gpu.Long("AdapterRAM")));
                g.Add("Graphics", vram == null ? gpu.Str("Name") : $"{gpu.Str("Name")} ({vram})");
            }

            var sizes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in Wmi.Query("SELECT InstanceName, MaxHorizontalImageSize, MaxVerticalImageSize FROM WmiMonitorBasicDisplayParams", Hw.WmiNs))
            {
                var (h, v) = (r.Long("MaxHorizontalImageSize") ?? 0, r.Long("MaxVerticalImageSize") ?? 0);
                if (h > 0 && v > 0)
                    sizes.TryAdd(DisplaysProvider.Key(r.Str("InstanceName")), $"{Math.Sqrt(h * h + v * v) / 2.54:0.0}\"");
            }
            foreach (var m in DisplaysProvider.Monitors().Where(m => m.Active))
            {
                var size = sizes.GetValueOrDefault(m.Instance);
                var name = m.Name ?? m.ProductCode ?? "Monitor";
                if (m.Vendor != null && Hw.PnpVendor(m.Vendor) is { } vendor && !name.Contains(vendor.Split(' ')[0], StringComparison.OrdinalIgnoreCase))
                    name = vendor.Split(' ')[0] + " " + name;
                g.Add("Monitor", size == null ? name : $"{name} ({size})");
            }
        });

        b.Try("Storage", () =>
        {
            var g = b.Group("Storage");
            foreach (var d in StorageProvider.Disks())
            {
                var kind = Hw.Join([Hw.DiskBus(d.Bus), Hw.DiskMedia(d.Media)], " ");
                g.Add("Disk", Hw.Join([d.Name, d.Size > 0 ? $"({Fmt.DecimalBytes(d.Size)}{(kind.Length > 0 ? ", " + kind : "")})" : null], " "));
            }
            foreach (var v in DriveInfo.GetDrives().Where(v => v.DriveType == DriveType.Fixed && v.IsReady))
                g.Add("Volume", $"{v.Name.TrimEnd('\\')} {v.VolumeLabel} ({Fmt.T("{0} free of {1}", Fmt.Bytes(v.TotalFreeSpace), Fmt.Bytes(v.TotalSize))})".Replace("  ", " "));
        });

        b.Try("Network", () =>
        {
            var adapters = Hw.NetAdapters();
            var g = b.Group("Network");
            foreach (var n in NetworkProvider.Interfaces(adapters)
                         .Where(n => n.OperationalStatus == OperationalStatus.Up && adapters?.GetValueOrDefault(n.Id) == true))
            {
                var ip = n.GetIPProperties().UnicastAddresses
                    .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString();
                g.Add("Network adapter", Hw.Join([n.Description, n.Speed > 0 ? Fmt.BitRate(n.Speed) : null, ip]));
            }
        });

        b.Try("Audio", () =>
        {
            var g = b.Group("Audio");
            foreach (var r in Wmi.Query("SELECT Name FROM Win32_SoundDevice"))
                g.Add("Sound device", r.Str("Name"));
            var endpoints = AudioProvider.Endpoints();
            if (endpoints.FirstOrDefault(e => e is { Playback: true, IsDefault: true }) is { } p) g.Add("Default playback", AudioProvider.Describe(p));
            if (endpoints.FirstOrDefault(e => e is { Playback: false, IsDefault: true }) is { } r2) g.Add("Default recording", AudioProvider.Describe(r2));
        });

        return b.Build();
    }
}
