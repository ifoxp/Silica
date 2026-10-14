using System.Diagnostics;
using System.Net.NetworkInformation;
using LibreHardwareMonitor.Hardware;

namespace Silica.Core.Sensors;

public enum SensorHubState { Closed, Opening, Ready, Failed }

/// <summary>
/// Owns the LibreHardwareMonitor session. Hardware is opened lazily on the first subscriber and polled
/// on a dedicated low-priority thread only while at least one subscriber exists.
/// </summary>
public sealed partial class SensorHub : IDisposable
{
    public static SensorHub Shared { get; } = new();

    private readonly object _gate = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Dictionary<ISensor, SensorReading> _map = [];
    private Computer? _computer;
    private Thread? _thread;
    private int _subscribers;
    private volatile bool _disposed;
    private volatile bool _reopen;
    private volatile bool _rebuild;
    private SensorGroups _groups = SensorGroups.All;

    public SensorHubState State { get; private set; }
    public string? Error { get; private set; }
    public IReadOnlyList<DeviceNode> Devices { get; private set; } = [];
    public KeySensors Key { get; private set; } = KeySensors.Empty;
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(1);
    public int HistorySize { get; set; } = 600;
    public bool IncludeVirtualNetwork { get; set; }
    public long PollCount { get; private set; }

    /// <summary>Raised on the poll thread after the device tree was (re)built.</summary>
    public event Action? DevicesChanged;

    /// <summary>Raised on the poll thread after every poll.</summary>
    public event Action? Updated;

    public static bool DriverInstalled
    {
        get
        {
            try { return LibreHardwareMonitor.PawnIo.PawnIo.IsInstalled; }
            catch { return false; }
        }
    }

    public SensorGroups Groups
    {
        get => _groups;
        set
        {
            if (_groups == value) return;
            _groups = value;
            _reopen = true;
            _wake.Set();
        }
    }

    public IEnumerable<SensorReading> AllSensors => Devices.SelectMany(d => d.AllSensors());

    public SensorReading? Find(string id) => AllSensors.FirstOrDefault(s => s.Id == id);

    /// <summary>Keeps polling alive until the returned token is disposed.</summary>
    public IDisposable Subscribe()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _subscribers++;
            if (_thread == null)
            {
                _thread = new Thread(Loop) { IsBackground = true, Name = "Sensor poll", Priority = ThreadPriority.BelowNormal };
                _thread.Start();
            }
        }
        _wake.Set();
        return new Subscription(this);
    }

    public void ResetStatistics()
    {
        foreach (var s in AllSensors) s.ResetStatistics();
    }

    private void Unsubscribe()
    {
        lock (_gate) _subscribers = Math.Max(0, _subscribers - 1);
    }

    private void Loop()
    {
        while (!_disposed)
        {
            int subscribers;
            lock (_gate) subscribers = _subscribers;
            if (subscribers == 0)
            {
                _wake.WaitOne();
                continue;
            }

            var sw = Stopwatch.StartNew();
            try
            {
                if (_computer == null || _reopen) Open();
                else if (_rebuild) Rebuild();
                Poll();
            }
            catch (Exception ex)
            {
                Error = ex.Message;
            }

            var wait = Interval - sw.Elapsed;
            _wake.WaitOne(wait > TimeSpan.FromMilliseconds(50) ? wait : TimeSpan.FromMilliseconds(50));
        }
        Close();
    }

    private void Open()
    {
        Close();
        _reopen = false;
        State = SensorHubState.Opening;
        try
        {
            var g = _groups;
            var computer = new Computer
            {
                IsCpuEnabled = g.HasFlag(SensorGroups.Cpu),
                IsGpuEnabled = g.HasFlag(SensorGroups.Gpu),
                IsMemoryEnabled = g.HasFlag(SensorGroups.Memory),
                IsMotherboardEnabled = g.HasFlag(SensorGroups.Motherboard),
                IsStorageEnabled = g.HasFlag(SensorGroups.Storage),
                IsNetworkEnabled = g.HasFlag(SensorGroups.Network),
                IsControllerEnabled = g.HasFlag(SensorGroups.Controller),
                IsBatteryEnabled = g.HasFlag(SensorGroups.Battery),
                IsPsuEnabled = g.HasFlag(SensorGroups.Psu)
            };
            computer.HardwareAdded += _ => _rebuild = true;
            computer.HardwareRemoved += _ => _rebuild = true;
            computer.Open();
            _computer = computer;
            Error = null;
            Rebuild();
            State = SensorHubState.Ready;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            State = SensorHubState.Failed;
            _computer = null;
            // Without this the loop would retry opening every second.
            _wake.WaitOne(TimeSpan.FromSeconds(30));
        }
    }

    private void Rebuild()
    {
        _rebuild = false;
        if (_computer == null) return;

        foreach (var hw in _computer.Hardware) UpdateRecursive(hw);

        var visibleNics = IncludeVirtualNetwork ? null : PhysicalAdapterNames();
        var previous = _map.ToDictionary(p => p.Value.Id, p => p.Value);
        _map.Clear();
        var roots = new List<DeviceNode>();
        foreach (var hw in _computer.Hardware)
        {
            if (hw.HardwareType == HardwareType.Network && visibleNics != null && !visibleNics.Contains(hw.Name)) continue;
            roots.Add(BuildNode(hw, null, previous));
        }

        roots.Sort((a, b) => Rank(a.Kind).CompareTo(Rank(b.Kind)));
        Devices = roots;
        Key = KeySensors.Resolve(roots);
        DevicesChanged?.Invoke();
    }

    private DeviceNode BuildNode(IHardware hw, DeviceNode? parent, Dictionary<string, SensorReading> previous)
    {
        var node = new DeviceNode(hw.Identifier.ToString(), hw.Name.Trim(), MapKind(hw.HardwareType), parent);
        foreach (var s in hw.Sensors)
        {
            var id = s.Identifier.ToString();
            // Keep the existing object across rebuilds so history and min/max survive.
            var reading = previous.TryGetValue(id, out var old) && old.Device.Id == node.Id
                ? old
                : new SensorReading(id, s.Name.Trim(), MapKind(s.SensorType), node, HistorySize);
            node.Sensors.Add(reading);
            _map[s] = reading;
        }
        foreach (var sub in hw.SubHardware)
        {
            var child = BuildNode(sub, node, previous);
            if (child.Sensors.Count > 0 || child.Children.Count > 0) node.Children.Add(child);
        }
        return node;
    }

    private void Poll()
    {
        if (_computer == null) return;
        foreach (var hw in _computer.Hardware) UpdateRecursive(hw);
        foreach (var (sensor, reading) in _map) reading.Record(sensor.Value);
        PollCount++;
        Updated?.Invoke();
    }

    private static void UpdateRecursive(IHardware hw)
    {
        try { hw.Update(); } catch { /* one flaky device must not stop the others */ }
        foreach (var sub in hw.SubHardware) UpdateRecursive(sub);
    }

    private void Close()
    {
        var c = _computer;
        _computer = null;
        _map.Clear();
        if (c == null) return;
        try { c.Close(); } catch { }
        State = SensorHubState.Closed;
    }

    private static HashSet<string> PhysicalAdapterNames()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
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

    public void Dispose()
    {
        _disposed = true;
        _wake.Set();
        _thread?.Join(TimeSpan.FromSeconds(3));
        if (_thread == null) Close();
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"-\d{4}$")]
    private static partial System.Text.RegularExpressions.Regex FilterSuffix();

    private sealed class Subscription(SensorHub hub) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) hub.Unsubscribe();
        }
    }
}
