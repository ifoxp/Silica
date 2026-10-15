using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Silica.Core.Model;
using Silica.Core.Util;

namespace Silica.Core.Info.Hardware;

/// <summary>
/// Everything a buyer asks about on one page: what is inside, how worn the drives and the battery are,
/// how many memory slots are free. Live readings such as temperatures are left out on purpose.
/// </summary>
public sealed class SpecSheetProvider : IInfoProvider
{
    public string Id => "specs";
    public string Title => "Spec sheet";
    public InfoCategory Category => InfoCategory.Hardware;
    public int Order => -10;
    public string Icon => "";

    public InfoPage Collect(CancellationToken ct)
    {
        var b = new PageBuilder(Id, Title);
        var hasBattery = Wmi.Query("SELECT DeviceID FROM Win32_Battery").Count > 0;

        b.Try("Computer", () =>
        {
            var g = b.Group("Computer");
            var cs = Wmi.First("SELECT Manufacturer, Model, PCSystemType FROM Win32_ComputerSystem");
            var chassis = Wmi.First("SELECT ChassisTypes FROM Win32_SystemEnclosure")?.Array<ushort>("ChassisTypes")?.FirstOrDefault();
            g.Word("Type", hasBattery || cs?.Int("PCSystemType") == 2 || chassis is 8 or 9 or 10 or 14 or 31 or 32 ? "Laptop" : "Desktop");
            // A self-built PC reports its motherboard (or a placeholder) as the model; that is listed under Motherboard.
            var model = Hw.Join([cs?.Str("Manufacturer"), cs?.Str("Model")], " ");
            var board = Wmi.First("SELECT Manufacturer, Product FROM Win32_BaseBoard");
            if (!model.Contains("O.E.M.", StringComparison.OrdinalIgnoreCase) && !model.Contains("System Product Name", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(model, Hw.Join([board?.Str("Manufacturer"), board?.Str("Product")], " "), StringComparison.OrdinalIgnoreCase))
                g.Add("Model", model);
            g.Add("Operating system", Hw.OsName());
            g.Word("Windows activation", Activation());
        });

        b.Try("Processor", () =>
        {
            foreach (var cpu in Wmi.Query("SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed FROM Win32_Processor"))
            {
                var g = b.Group("Processor");
                g.Add("Model", cpu.Str("Name")?.Trim());
                g.Add("Cores / threads", $"{cpu.Int("NumberOfCores")} / {cpu.Int("NumberOfLogicalProcessors")}");
                if (cpu.Long("MaxClockSpeed") is > 0 and var mhz) g.Add("Base clock", $"{mhz / 1000.0:0.00} {Fmt.Unit("GHz")}");
            }
        });

        b.Try("Graphics", () =>
        {
            var g = b.Group("Graphics");
            foreach (var gpu in Wmi.Query(GpuProvider.Query).Where(r => !Hw.IsVirtualGpu(r.Str("PNPDeviceID"))))
            {
                var vram = Hw.GpuMemory(gpu.Str("PNPDeviceID"), gpu.Long("AdapterRAM"));
                g.Named(gpu.Str("Name") ?? "GPU", vram is > 0 ? Fmt.T("{0} video memory", Fmt.Bytes((double)vram.Value)) : null);
            }
        });

        b.Try("Memory", () =>
        {
            var g = b.Group("Memory");
            var modules = MemoryProvider.Modules();
            var ms = new Native.MemoryStatusEx { Length = (uint)Marshal.SizeOf<Native.MemoryStatusEx>() };
            var installed = modules.Sum(m => (double)m.Capacity);
            g.Add("Installed", installed > 0 ? Fmt.Bytes(installed) : Native.GlobalMemoryStatusEx(ref ms) ? Fmt.Bytes((double)ms.TotalPhys) : null);
            g.Add("Configuration", MemoryProvider.ModuleSummary(modules));
            var arrays = Wmi.Query("SELECT MemoryDevices, MaxCapacityEx, MaxCapacity FROM Win32_PhysicalMemoryArray");
            var slots = arrays.Sum(a => a.Int("MemoryDevices") ?? 0);
            if (slots > 0) g.Add("Slots used", Fmt.T("{0} of {1}", modules.Count, slots));
            var max = arrays.Sum(a => a.ULong("MaxCapacityEx") is > 0 and var ex ? (double)ex * 1024 : (double)(a.ULong("MaxCapacity") ?? 0) * 1024);
            if (max > installed) g.Add("Maximum supported", Fmt.Bytes(max));
            foreach (var m in modules)
                g.Named(m.Slot ?? "DIMM", Hw.Join([Fmt.Bytes((double)m.Capacity), m.Manufacturer, m.PartNumber?.Trim()], " · "));
        });

        b.Try("Storage", () =>
        {
            foreach (var d in StorageProvider.Disks())
            {
                ct.ThrowIfCancellationRequested();
                // USB sticks and card readers are not part of the computer being described.
                if (d.Bus is 7 or 12 or 13) continue;
                var g = b.Group(d.Name, localizeTitle: false);
                g.Add("Capacity", Hw.Join([d.Size > 0 ? Fmt.DecimalBytes(d.Size) : null, Hw.DiskBus(d.Bus), Hw.DiskMedia(d.Media)], " "));
                var health = int.TryParse(d.DeviceId, out var n) ? DriveSmart.Read(n) : null;
                if (health == null)
                {
                    g.Word("Health", d.Row.Int("HealthStatus") switch { 0 => "Healthy", 1 => "Warning", 2 => "Unhealthy", _ => null });
                    continue;
                }
                if (health.LifeRemaining is { } life) g.Add("Life remaining", Fmt.T("{0} ({1})", Fmt.Percent(life), Verdict(life)));
                if (health.PowerOnHours is > 0 and var hours) g.Add("Power-on hours", Fmt.T("{0} h ({1} days)", Fmt.Number(hours), Fmt.Number(hours / 24)));
                if (health.BytesWritten is > 0 and var written) g.Add("Data written", Fmt.DecimalBytes(written));
                if (health.MediaErrors is > 0 and var errors) g.Add("Media errors", Fmt.Number(errors));
                if (health.CriticalWarning != 0) g.Word("Health", "Warning");
            }
        });

        if (hasBattery) b.Try("Battery", () => AddBattery(b));

        b.Try("Displays", () =>
        {
            var g = b.Group("Displays");
            var sizes = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in Wmi.Query("SELECT InstanceName, MaxHorizontalImageSize, MaxVerticalImageSize FROM WmiMonitorBasicDisplayParams", Hw.WmiNs))
            {
                var (h, v) = (r.Long("MaxHorizontalImageSize") ?? 0, r.Long("MaxVerticalImageSize") ?? 0);
                if (h > 0 && v > 0) sizes.TryAdd(DisplaysProvider.Key(r.Str("InstanceName")), Math.Sqrt(h * h + v * v) / 2.54);
            }
            var names = DisplaysProvider.Monitors().ToDictionary(m => m.Instance, m => m.Name ?? m.ProductCode, StringComparer.OrdinalIgnoreCase);
            foreach (var o in DisplaysProvider.Outputs())
            {
                var best = DisplaysProvider.Modes(o.Device).FirstOrDefault();
                var name = o.MonitorInstance != null && names.TryGetValue(o.MonitorInstance, out var n) && n != null ? n : o.MonitorName ?? "Monitor";
                var size = o.MonitorInstance != null && sizes.TryGetValue(o.MonitorInstance, out var inches) ? $"{inches:0.0}\"" : null;
                // A monitor turned to portrait still has a 1920 × 1080 panel, which is how it is sold.
                var mode = best.W > 0 ? $"{Hw.Resolution(Math.Max(best.W, best.H), Math.Min(best.W, best.H))}, {best.Rates.Max()} {Fmt.Unit("Hz")}" : null;
                g.Named(name, Hw.Join([size, mode], ", "));
            }
        });

        b.Try("Motherboard", () =>
        {
            var g = b.Group("Motherboard");
            var board = Wmi.First("SELECT Manufacturer, Product FROM Win32_BaseBoard");
            g.Add("Model", Hw.Join([board?.Str("Manufacturer"), board?.Str("Product")], " "));
            var bios = Wmi.First("SELECT SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS");
            g.Add("BIOS", Hw.Join([bios?.Str("SMBIOSBIOSVersion"), bios?.Date("ReleaseDate") is { } date ? $"({Fmt.Date(date)})" : null], " "));
        });

        b.Try("Connectivity", () =>
        {
            var g = b.Group("Connectivity");
            var physical = Hw.NetAdapters();
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces()
                         .Where(n => n.NetworkInterfaceType is NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet)
                         .Where(n => physical == null || physical.GetValueOrDefault(n.Id, false))
                         // Wi-Fi Direct adds copies named "... #2", "... #3" for the same card.
                         .GroupBy(n => System.Text.RegularExpressions.Regex.Replace(n.Description, @"\s#\d+$", "")).Select(x => x.OrderBy(n => n.Description.Length).First()))
                g.Add(nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "Wi-Fi" : "Ethernet", nic.Description);
            if (Wmi.Query("SELECT Name FROM Win32_PnPEntity WHERE PNPClass = 'Bluetooth' AND Status = 'OK'").Count > 0)
                g.Flag("Bluetooth", true);
        });

        return b.Build();
    }

    private static void AddBattery(PageBuilder b)
    {
        var statics = Wmi.Query("SELECT InstanceName, DesignedCapacity, DeviceName, Chemistry FROM BatteryStaticData", Hw.WmiNs);
        if (statics.Count == 0)
        {
            b.Warn("Run as administrator for battery capacity and wear data");
            return;
        }
        var full = Wmi.Query("SELECT InstanceName, FullChargedCapacity FROM BatteryFullChargedCapacity", Hw.WmiNs);
        var cycles = Wmi.Query("SELECT InstanceName, CycleCount FROM BatteryCycleCount", Hw.WmiNs);
        foreach (var s in statics)
        {
            var inst = s.Str("InstanceName");
            var g = b.Group("Battery");
            g.Add("Model", s.Str("DeviceName"));
            g.Add("Chemistry", PowerProvider.Chemistry(s.Long("Chemistry")));
            var design = s.Long("DesignedCapacity");
            var now = full.FirstOrDefault(f => f.Str("InstanceName") == inst)?.Long("FullChargedCapacity");
            g.Add("Design capacity", design is > 0 ? $"{Fmt.Number(design.Value)} {Fmt.Unit("mWh")}" : null);
            g.Add("Full charge capacity", now is > 0 ? $"{Fmt.Number(now.Value)} {Fmt.Unit("mWh")}" : null);
            if (design is > 0 && now is > 0)
            {
                var left = Math.Min(100, 100.0 * now.Value / design.Value);
                g.Add("Capacity left", Fmt.T("{0} ({1})", Fmt.Percent(left, 0), Verdict(left)));
            }
            if (cycles.FirstOrDefault(c => c.Str("InstanceName") == inst)?.Long("CycleCount") is > 0 and var count) g.Add("Cycle count", count);
        }
    }

    // A plain word next to the percentage, so a buyer does not have to know what a normal value is.
    private static string Verdict(double percent) => percent switch
    {
        >= 90 => Fmt.T("excellent"),
        >= 75 => Fmt.T("good"),
        >= 50 => Fmt.T("fair"),
        _ => Fmt.T("worn")
    };

    // LicenseStatus 1 is licensed; the query lists the Windows edition's own product only.
    private static string? Activation()
    {
        var row = Wmi.First("SELECT LicenseStatus FROM SoftwareLicensingProduct WHERE ApplicationID = '55c92734-d682-4d71-983e-d6ec3f16059f' AND PartialProductKey IS NOT NULL");
        return row?.Int("LicenseStatus") switch
        {
            1 => "Activated",
            null => null,
            _ => "Not activated"
        };
    }
}
