using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using LibreHardwareMonitor.Hardware;

namespace Silica.Core.Sensors;

public sealed record DeviceSpec(string Id, string Name, DeviceKind Kind, int Parent);

public sealed record SensorSpec(string Id, string Name, SensorKind Kind, int Device);

/// <summary>Flat description of the hardware: devices in display order and sensors in value order.</summary>
public sealed record SensorTree(IReadOnlyList<DeviceSpec> Devices, IReadOnlyList<SensorSpec> Sensors);

/// <summary>One LibreHardwareMonitor session. Runs either inside Silica or in the separate sensor host process.</summary>
public sealed partial class LhmSession : IDisposable
{
    private readonly Computer _computer;
    private readonly bool _includeVirtualNetwork;
    private List<ISensor> _sensors = [];
    private volatile bool _changed;

    public LhmSession(SensorGroups groups, bool includeVirtualNetwork)
    {
        _includeVirtualNetwork = includeVirtualNetwork;
        _computer = new Computer
        {
            IsCpuEnabled = groups.HasFlag(SensorGroups.Cpu),
            IsGpuEnabled = groups.HasFlag(SensorGroups.Gpu),
            IsMemoryEnabled = groups.HasFlag(SensorGroups.Memory),
            IsMotherboardEnabled = groups.HasFlag(SensorGroups.Motherboard),
            IsStorageEnabled = groups.HasFlag(SensorGroups.Storage),
            IsNetworkEnabled = groups.HasFlag(SensorGroups.Network),
            IsControllerEnabled = groups.HasFlag(SensorGroups.Controller),
            IsBatteryEnabled = groups.HasFlag(SensorGroups.Battery),
            IsPsuEnabled = groups.HasFlag(SensorGroups.Psu)
        };
        _computer.HardwareAdded += _ => _changed = true;
        _computer.HardwareRemoved += _ => _changed = true;
        _computer.Open();
        Tree = Build();
    }

    public SensorTree Tree { get; private set; }

    /// <summary>Reads every sensor. Returns true when devices appeared or disappeared and <see cref="Tree"/> was rebuilt.</summary>
    public bool Poll(out float[] values)
    {
        var rebuilt = false;
        if (_changed)
        {
            _changed = false;
            Tree = Build();
            rebuilt = true;
        }
        else
        {
            foreach (var hw in _computer.Hardware) UpdateRecursive(hw);
        }

        values = new float[_sensors.Count];
        for (var i = 0; i < values.Length; i++)
            values[i] = _sensors[i].Value is { } v && !float.IsInfinity(v) ? v : float.NaN;
        return rebuilt;
    }

    private SensorTree Build()
    {
        foreach (var hw in _computer.Hardware) UpdateRecursive(hw);
        var visibleNics = _includeVirtualNetwork ? null : PhysicalAdapterNames();
        var devices = new List<DeviceSpec>();
        var specs = new List<SensorSpec>();
        var sensors = new List<ISensor>();

        void Add(IHardware hw, int parent)
        {
            var index = devices.Count;
            devices.Add(new DeviceSpec(hw.Identifier.ToString(), hw.Name.Trim(), MapKind(hw.HardwareType), parent));
            foreach (var s in hw.Sensors)
            {
                specs.Add(new SensorSpec(s.Identifier.ToString(), s.Name.Trim(), MapKind(s.SensorType), index));
                sensors.Add(s);
            }
            foreach (var sub in hw.SubHardware) Add(sub, index);
        }

        var roots = _computer.Hardware
            .Where(hw => hw.HardwareType != HardwareType.Network || visibleNics == null || visibleNics.Contains(hw.Name))
            .OrderBy(hw => Rank(MapKind(hw.HardwareType)));
        foreach (var hw in roots) Add(hw, -1);

        _sensors = sensors;
        return new SensorTree(devices, specs);
    }

    private static void UpdateRecursive(IHardware hw)
    {
        try { hw.Update(); } catch { /* one flaky device must not stop the others */ }
        foreach (var sub in hw.SubHardware) UpdateRecursive(sub);
    }

    public void Dispose()
    {
        try { _computer.Close(); } catch { }
    }

    private static HashSet<string> PhysicalAdapterNames()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                // Filter drivers show up as separate interfaces named like "Ethernet-QoS Packet Scheduler-0000".
                if (FilterSuffix().IsMatch(nic.Name)) continue;
                if (nic.NetworkInterfaceType is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.Ppp)) continue;
                var d = nic.Description;
                if (d.Contains("Virtual", StringComparison.OrdinalIgnoreCase) || d.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase)
                    || d.Contains("VMware", StringComparison.OrdinalIgnoreCase) || d.Contains("TAP-", StringComparison.OrdinalIgnoreCase)
                    || d.Contains("Debug", StringComparison.OrdinalIgnoreCase))
                    continue;
                set.Add(nic.Name);
            }
        }
        catch { }
        return set;
    }

    private static int Rank(DeviceKind k) => k switch
    {
        DeviceKind.Cpu => 0,
        DeviceKind.Gpu => 1,
        DeviceKind.Memory => 2,
        DeviceKind.Motherboard => 3,
        DeviceKind.SuperIO => 4,
        DeviceKind.EmbeddedController => 5,
        DeviceKind.Storage => 6,
        DeviceKind.Network => 7,
        DeviceKind.Battery => 8,
        DeviceKind.Cooler => 9,
        DeviceKind.Psu => 10,
        _ => 11
    };

    private static DeviceKind MapKind(HardwareType t) => t switch
    {
        HardwareType.Motherboard => DeviceKind.Motherboard,
        HardwareType.SuperIO => DeviceKind.SuperIO,
        HardwareType.Cpu => DeviceKind.Cpu,
        HardwareType.Memory => DeviceKind.Memory,
        HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel => DeviceKind.Gpu,
        HardwareType.Storage => DeviceKind.Storage,
        HardwareType.Network => DeviceKind.Network,
        HardwareType.Cooler => DeviceKind.Cooler,
        HardwareType.EmbeddedController => DeviceKind.EmbeddedController,
        HardwareType.Psu => DeviceKind.Psu,
        HardwareType.Battery => DeviceKind.Battery,
        _ => DeviceKind.Other
    };

    private static SensorKind MapKind(SensorType t) => t switch
    {
        SensorType.Temperature => SensorKind.Temperature,
        SensorType.Load => SensorKind.Load,
        SensorType.Clock => SensorKind.Clock,
        SensorType.Voltage => SensorKind.Voltage,
        SensorType.Current => SensorKind.Current,
        SensorType.Power => SensorKind.Power,
        SensorType.Fan => SensorKind.Fan,
        SensorType.Flow => SensorKind.Flow,
        SensorType.Control => SensorKind.Control,
        SensorType.Level => SensorKind.Level,
        SensorType.Data => SensorKind.Data,
        SensorType.SmallData => SensorKind.SmallData,
        SensorType.Throughput => SensorKind.Throughput,
        SensorType.Factor => SensorKind.Factor,
        SensorType.Energy => SensorKind.Energy,
        SensorType.Noise => SensorKind.Noise,
        SensorType.TimeSpan => SensorKind.TimeSpan,
        SensorType.Frequency => SensorKind.Frequency,
        SensorType.Humidity => SensorKind.Humidity,
        SensorType.Conductivity => SensorKind.Conductivity,
        _ => SensorKind.Other
    };

    [GeneratedRegex(@"-\d{4}$")]
    private static partial Regex FilterSuffix();
}
