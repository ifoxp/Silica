using System.Diagnostics;

namespace Silica.Core.Sensors;

public enum SensorHubState { Closed, Opening, Ready, Failed }

/// <summary>
/// Owns the sensor session. Hardware is opened on the first subscriber and polled on a dedicated
/// low-priority thread only while someone subscribes; after a quiet period the session is released.
/// </summary>
public sealed class SensorHub : IDisposable
{
    public static SensorHub Shared { get; } = new();

    private readonly object _gate = new();
    private readonly AutoResetEvent _wake = new(false);
    // Readings outlive a released session, so history and min/max survive a reopen.
    private readonly Dictionary<string, SensorReading> _known = [];
    private ISensorEngine? _engine;
    private SensorReading[] _readings = [];
    private Thread? _thread;
    private int _subscribers;
    private volatile bool _disposed;
    private volatile bool _reopen;
    private SensorGroups _groups = SensorGroups.All;

    public SensorHubState State { get; private set; }
    public string? Error { get; private set; }
    public IReadOnlyList<DeviceNode> Devices { get; private set; } = [];
    public KeySensors Key { get; private set; } = KeySensors.Empty;
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(1);
    public int HistorySize { get; set; } = 600;
    public bool IncludeVirtualNetwork { get; set; }
    public long PollCount { get; private set; }

    /// <summary>How long the hardware session stays open with nobody watching.</summary>
    public TimeSpan IdleRelease { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Starts the sensor host process for a pipe name. When set, hardware is read in that process so its
    /// memory is returned to Windows on release; when null or failing, it is read in this process.
    /// </summary>
    public Func<string, Process?>? HostLauncher { get; set; }

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
                if (_engine == null) _wake.WaitOne();
                else if (!_wake.WaitOne(IdleRelease))
                {
                    lock (_gate) subscribers = _subscribers;
                    if (subscribers == 0) Close();
                }
                continue;
            }

            var sw = Stopwatch.StartNew();
            try
            {
                if (_engine == null || _reopen) Open();
                if (_engine != null) Poll();
            }
            catch (Exception ex)
            {
                // A crashed host is restarted on the next round.
                Error = ex.Message;
                Close();
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
            _engine = CreateEngine();
            Error = null;
            Apply(_engine.Tree);
            State = SensorHubState.Ready;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            State = SensorHubState.Failed;
            _engine = null;
            // Without this the loop would retry opening every second.
            _wake.WaitOne(TimeSpan.FromSeconds(30));
        }
    }

    private ISensorEngine CreateEngine()
    {
        if (HostLauncher is { } launch)
        {
            try { return new RemoteEngine(launch, _groups, IncludeVirtualNetwork); }
            catch (Exception ex) { Error = ex.Message; }
        }
        return new LocalEngine(_groups, IncludeVirtualNetwork);
    }

    private void Poll()
    {
        var values = _engine!.Poll(out var tree);
        if (tree != null) Apply(tree);
        var readings = _readings;
        for (var i = 0; i < readings.Length && i < values.Length; i++)
            readings[i].Record(float.IsNaN(values[i]) ? null : values[i]);
        PollCount++;
        Updated?.Invoke();
    }

    private void Apply(SensorTree tree)
    {
        var nodes = new DeviceNode[tree.Devices.Count];
        var roots = new List<DeviceNode>();
        for (var i = 0; i < nodes.Length; i++)
        {
            var d = tree.Devices[i];
            var parent = d.Parent >= 0 ? nodes[d.Parent] : null;
            nodes[i] = new DeviceNode(d.Id, d.Name, d.Kind, parent);
            if (parent == null) roots.Add(nodes[i]);
            else parent.Children.Add(nodes[i]);
        }

        var readings = new SensorReading[tree.Sensors.Count];
        for (var i = 0; i < readings.Length; i++)
        {
            var s = tree.Sensors[i];
            var device = nodes[s.Device];
            var reading = _known.TryGetValue(s.Id, out var old) && old.Device.Id == device.Id
                ? old
                : new SensorReading(s.Id, s.Name, s.Kind, device, HistorySize);
            _known[s.Id] = reading;
            device.Sensors.Add(reading);
            readings[i] = reading;
        }

        foreach (var node in nodes) node.Children.RemoveAll(c => c.Sensors.Count == 0 && c.Children.Count == 0);
        _readings = readings;
        Devices = roots;
        Key = KeySensors.Resolve(roots);
        DevicesChanged?.Invoke();
    }

    private void Close()
    {
        var engine = _engine;
        _engine = null;
        if (engine == null) return;
        try { engine.Dispose(); } catch { }
        State = SensorHubState.Closed;
    }

    public void Dispose()
    {
        _disposed = true;
        _wake.Set();
        _thread?.Join(TimeSpan.FromSeconds(3));
        if (_thread == null) Close();
    }

    private sealed class Subscription(SensorHub hub) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) hub.Unsubscribe();
        }
    }
}
