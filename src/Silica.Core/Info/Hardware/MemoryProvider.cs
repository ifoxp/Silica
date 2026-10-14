using System.Runtime.InteropServices;
using Silica.Core.Model;
using Silica.Core.Util;

namespace Silica.Core.Info.Hardware;

public sealed class MemoryProvider : IInfoProvider
{
    public string Id => "memory";
    public string Title => "Memory";
    public InfoCategory Category => InfoCategory.Hardware;
    public int Order => 30;
    public string Icon => "";

    public InfoPage Collect(CancellationToken ct)
    {
        var b = new PageBuilder(Id, Title);
        var modules = Modules();

        b.Try("Physical memory", () =>
        {
            var ms = new Native.MemoryStatusEx { Length = (uint)Marshal.SizeOf<Native.MemoryStatusEx>() };
            if (!Native.GlobalMemoryStatusEx(ref ms)) return;
            var g = b.Group("Physical memory");
            if (Native.GetPhysicallyInstalledSystemMemory(out var installedKb))
            {
                g.Add("Installed", Fmt.Bytes(installedKb * 1024.0));
                var reserved = (double)installedKb * 1024 - ms.TotalPhys;
                if (reserved > 0) g.Add("Hardware reserved", Fmt.Bytes(reserved));
            }
            g.Add("Total", Fmt.Bytes((double)ms.TotalPhys));
            g.Add("Available", Fmt.Bytes((double)ms.AvailPhys));
            g.Add("In use", Fmt.Bytes((double)(ms.TotalPhys - ms.AvailPhys)));
            g.Add("Memory load", Fmt.Percent(ms.MemoryLoad));
            g.Add("Configuration", ModuleSummary(modules));
        });

        b.Try("Commit", () =>
        {
            if (!Native.GetPerformanceInfo(out var pi, (uint)Marshal.SizeOf<Native.PerformanceInformation>())) return;
            double page = pi.PageSize;
            var g = b.Group("Virtual memory");
            g.Add("Committed", Fmt.Bytes(pi.CommitTotal * page));
            g.Add("Commit limit", Fmt.Bytes(pi.CommitLimit * page));
            g.Add("Commit peak", Fmt.Bytes(pi.CommitPeak * page));
            g.Add("Cached", Fmt.Bytes(pi.SystemCache * page));
            g.Add("Kernel paged pool", Fmt.Bytes(pi.KernelPaged * page));
            g.Add("Kernel nonpaged pool", Fmt.Bytes(pi.KernelNonpaged * page));
            g.Add("Page size", Fmt.Bytes(page));
            g.Flag("Page file managed by system",
                Wmi.First("SELECT AutomaticManagedPagefile FROM Win32_ComputerSystem")?.Bool("AutomaticManagedPagefile"));
        });

        b.Try("Page files", () =>
        {
            var t = b.Table("Page files", "File", "Allocated", "Current usage", "Peak usage");
            foreach (var r in Wmi.Query("SELECT Name, AllocatedBaseSize, CurrentUsage, PeakUsage FROM Win32_PageFileUsage"))
                t.Row(r.Str("Name"), Mb(r.Long("AllocatedBaseSize")), Mb(r.Long("CurrentUsage")), Mb(r.Long("PeakUsage")));
        });

        b.Try("Slots", () =>
        {
            var arrays = Wmi.Query("SELECT MaxCapacityEx, MaxCapacity, MemoryDevices, MemoryErrorCorrection, Use FROM Win32_PhysicalMemoryArray")
                .Where(a => a.Int("Use") is null or 3)
                .ToList();
            if (arrays.Count == 0 && modules.Count == 0) return;
            var g = b.Group("Memory slots");
            var slots = arrays.Sum(a => a.Int("MemoryDevices") ?? 0);
            if (slots > 0) g.Add("Total slots", slots);
            g.Add("Used slots", modules.Count);
            var maxKb = arrays.Sum(a => (double)(a.Long("MaxCapacityEx") is > 0 ? a.Long("MaxCapacityEx")!.Value : a.Long("MaxCapacity") ?? 0));
            if (maxKb > 0) g.Add("Maximum capacity", Fmt.Bytes(maxKb * 1024));
            g.Word("Error correction", arrays.Select(a => a.Int("MemoryErrorCorrection")).FirstOrDefault() switch
            {
                3 => "None",
                4 => "Parity",
                5 => "Single-bit ECC",
                6 => "Multi-bit ECC",
                7 => "CRC",
                _ => null
            });
        });

        b.Try("Modules", () =>
        {
            var t = b.Table("Memory modules", "Slot", "Capacity", "Type", "Rated speed", "Configured speed", "Manufacturer",
                "Part number", "Serial number", "Form factor", "Ranks", "Voltage");
            foreach (var m in modules)
            {
                t.Row(m.Slot, Fmt.Bytes((double)m.Capacity), m.Type, Rate(m.Speed), Rate(m.ConfiguredSpeed), m.Manufacturer,
                    m.PartNumber, m.Serial, m.FormFactor, m.Ranks is > 0 ? m.Ranks : null,
                    m.VoltageMv is > 0 ? (m.VoltageMv.Value / 1000.0).ToString("0.00#") + " V" : null);
            }
        });

        return b.Build();
    }

    private static string? Mb(long? mb) => mb is { } v ? Fmt.Bytes(v * 1024.0 * 1024) : null;

    private static string? Rate(long? mts) => mts is > 0 ? $"{mts} MT/s" : null;

    internal sealed record Module(string? Slot, ulong Capacity, string? Type, long? Speed, long? ConfiguredSpeed,
        string? Manufacturer, string? PartNumber, string? Serial, string? FormFactor, int? Ranks, long? VoltageMv);

    internal static List<Module> Modules() =>
        Wmi.Query("SELECT BankLabel, DeviceLocator, Capacity, SMBIOSMemoryType, Speed, ConfiguredClockSpeed, Manufacturer, " +
                  "PartNumber, SerialNumber, FormFactor, Attributes, ConfiguredVoltage FROM Win32_PhysicalMemory")
            .Select(r => new Module(
                Hw.Join([r.Str("BankLabel"), r.Str("DeviceLocator")], " / "),
                r.ULong("Capacity") ?? 0,
                Hw.MemoryType(r.Int("SMBIOSMemoryType")),
                r.Long("Speed"),
                r.Long("ConfiguredClockSpeed"),
                Hw.MemoryVendor(r.Str("Manufacturer")),
                r.Str("PartNumber"),
                r.Str("SerialNumber") is { } s && s.Trim('0') != "" ? s : null,
                Hw.MemoryFormFactor(r.Int("FormFactor")),
                r.Int("Attributes"),
                r.Long("ConfiguredVoltage")))
            .ToList();

    // "2 × 16 GB DDR5-6000"; mixed kits are listed group by group.
    internal static string? ModuleSummary(List<Module> modules)
    {
        if (modules.Count == 0) return null;
        return string.Join(" + ", modules
            .GroupBy(m => (m.Capacity, m.Type, Speed: m.ConfiguredSpeed is > 0 ? m.ConfiguredSpeed : m.Speed))
            .Select(g =>
            {
                var kind = g.Key.Type == null ? "" : " " + g.Key.Type + (g.Key.Speed is > 0 ? "-" + g.Key.Speed : "");
                return $"{g.Count()} × {Fmt.Bytes((double)g.Key.Capacity)}{kind}";
            }));
    }
}
