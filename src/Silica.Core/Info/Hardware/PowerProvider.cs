using System.Runtime.InteropServices;
using System.Text;
using Silica.Core.Model;
using Silica.Core.Util;

namespace Silica.Core.Info.Hardware;

public sealed class PowerProvider : IInfoProvider
{
    public string Id => "power";
    public string Title => "Power";
    public InfoCategory Category => InfoCategory.Hardware;
    public int Order => 100;
    public string Icon => "";

    public InfoPage Collect(CancellationToken ct)
    {
        var b = new PageBuilder(Id, Title);
        var hasBattery = false;

        b.Try("Power status", () =>
        {
            if (!Native.GetSystemPowerStatus(out var s)) return;
            hasBattery = s.BatteryFlag != 128 && s.BatteryFlag != 255;
            var g = b.Group("Power status");
            g.Word("Power source", s.AcLineStatus switch { 1 => "AC power", 0 => "Battery", _ => null });
            g.Add("Power plan", ActivePlan());
            if (!hasBattery)
            {
                g.Word("Battery", "No battery");
                return;
            }
            if (s.BatteryLifePercent <= 100) g.Add("Battery level", Fmt.Percent(s.BatteryLifePercent));
            g.Word("Battery state", (s.BatteryFlag & 8) != 0 ? "Charging"
                : (s.BatteryFlag & 4) != 0 ? "Critical"
                : (s.BatteryFlag & 2) != 0 ? "Low"
                : s.AcLineStatus == 1 ? "Not charging" : "Discharging");
            if (s.BatteryLifeTime > 0) g.Add("Remaining time", Fmt.Duration(TimeSpan.FromSeconds(s.BatteryLifeTime)));
            if (s.BatteryFullLifeTime > 0) g.Add("Full charge runtime", Fmt.Duration(TimeSpan.FromSeconds(s.BatteryFullLifeTime)));
            g.Word("Battery saver", s.SystemStatusFlag == 1 ? "Enabled" : "Disabled");
        });

        if (hasBattery) b.Try("Batteries", () => AddBatteries(b));
        return b.Build();
    }

    private static void AddBatteries(PageBuilder b)
    {
        var win32 = Wmi.Query("SELECT Name, DeviceID, EstimatedChargeRemaining, Chemistry, DesignVoltage FROM Win32_Battery");
        var statics = Wmi.Query("SELECT InstanceName, DesignedCapacity, ManufactureName, SerialNumber, DeviceName, Chemistry FROM BatteryStaticData", Hw.WmiNs);
        var full = ByInstance("SELECT InstanceName, FullChargedCapacity FROM BatteryFullChargedCapacity");
        var cycles = ByInstance("SELECT InstanceName, CycleCount FROM BatteryCycleCount");
        var status = ByInstance("SELECT InstanceName, Voltage, ChargeRate, DischargeRate, RemainingCapacity FROM BatteryStatus");

        if (statics.Count == 0)
        {
            foreach (var w in win32)
            {
                var g = b.Group(w.Str("Name") ?? "Battery", localizeTitle: false);
                g.Add("Charge", w.Int("EstimatedChargeRemaining") is { } pct ? Fmt.Percent(pct) : null);
                g.Add("Chemistry", Win32Chemistry(w.Int("Chemistry")));
                g.Add("Design voltage", w.Long("DesignVoltage") is > 0 and var mv ? $"{mv / 1000.0:0.00} V" : null);
            }
            if (win32.Count > 0) b.Warn("Run as administrator for battery capacity and wear data");
            return;
        }

        foreach (var s in statics)
        {
            var inst = s.Str("InstanceName") ?? "";
            var g = b.Group(s.Str("DeviceName") ?? "Battery", localizeTitle: false);
            g.Add("Manufacturer", s.Str("ManufactureName"));
            g.Add("Serial number", s.Str("SerialNumber"));
            g.Add("Chemistry", Chemistry(s.Long("Chemistry")));
            var design = s.Long("DesignedCapacity");
            var fullCap = full.GetValueOrDefault(inst)?.Long("FullChargedCapacity");
            g.Add("Design capacity", design is > 0 ? $"{Fmt.Number(design.Value)} {Fmt.Unit("mWh")}" : null);
            g.Add("Full charge capacity", fullCap is > 0 ? $"{Fmt.Number(fullCap.Value)} {Fmt.Unit("mWh")}" : null);
            if (design is > 0 && fullCap is > 0) g.Add("Health", Fmt.Percent(100.0 * fullCap.Value / design.Value, 1));
            if (cycles.GetValueOrDefault(inst)?.Long("CycleCount") is > 0 and var c) g.Add("Cycle count", c);

            if (status.GetValueOrDefault(inst) is { } st)
            {
                if (st.Long("RemainingCapacity") is > 0 and var rem) g.Add("Remaining capacity", $"{Fmt.Number(rem)} {Fmt.Unit("mWh")}");
                if (st.Long("Voltage") is > 0 and var v) g.Add("Voltage", $"{v / 1000.0:0.000} V");
                if (st.Long("ChargeRate") is > 0 and var cr) g.Add("Charge rate", $"{cr / 1000.0:0.0} W");
                if (st.Long("DischargeRate") is > 0 and var dr) g.Add("Discharge rate", $"{dr / 1000.0:0.0} W");
            }
        }
    }

    private static Dictionary<string, WmiRow> ByInstance(string wql)
    {
        var d = new Dictionary<string, WmiRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in Wmi.Query(wql, Hw.WmiNs))
            if (r.Str("InstanceName") is { } i) d.TryAdd(i, r);
        return d;
    }

    // BatteryStaticData.Chemistry is a four-character code such as "LION" packed into a ULONG.
    private static string? Chemistry(long? code)
    {
        if (code is not > 0) return null;
        var text = Encoding.ASCII.GetString(BitConverter.GetBytes((uint)code.Value)).TrimEnd('\0', ' ').ToUpperInvariant();
        return text switch
        {
            "LION" or "LI-I" => "Lithium-ion",
            "LIP" or "LIPO" => "Lithium polymer",
            "NIMH" => "Nickel-metal hydride",
            "NICD" => "Nickel-cadmium",
            "PBAC" => "Lead acid",
            "" => null,
            _ => text
        };
    }

    private static string? Win32Chemistry(int? c) => c switch
    {
        3 => "Lead acid",
        4 => "Nickel-cadmium",
        5 => "Nickel-metal hydride",
        6 => "Lithium-ion",
        7 => "Zinc air",
        8 => "Lithium polymer",
        _ => null
    };

    internal static string? ActivePlan()
    {
        try
        {
            if (Native.PowerGetActiveScheme(IntPtr.Zero, out var guidPtr) != 0) return null;
            try
            {
                var guid = Marshal.PtrToStructure<Guid>(guidPtr);
                uint size = 0;
                Native.PowerReadFriendlyName(IntPtr.Zero, ref guid, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref size);
                if (size == 0) return null;
                var buffer = Marshal.AllocHGlobal((int)size);
                try
                {
                    return Native.PowerReadFriendlyName(IntPtr.Zero, ref guid, IntPtr.Zero, IntPtr.Zero, buffer, ref size) == 0
                        ? Marshal.PtrToStringUni(buffer)
                        : null;
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            finally { Native.LocalFree(guidPtr); }
        }
        catch (DllNotFoundException) { return null; }
        catch (EntryPointNotFoundException) { return null; }
    }
}
