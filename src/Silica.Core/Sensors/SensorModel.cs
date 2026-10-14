namespace Silica.Core.Sensors;

public enum SensorKind
{
    Temperature, Load, Clock, Voltage, Current, Power, Fan, Flow, Control, Level,
    Data, SmallData, Throughput, Factor, Energy, Noise, TimeSpan, Frequency, Humidity, Conductivity, Other
}

public enum DeviceKind { Motherboard, SuperIO, Cpu, Memory, Gpu, Storage, Network, Cooler, EmbeddedController, Psu, Battery, Other }

[Flags]
public enum SensorGroups
{
    None = 0,
    Cpu = 1,
    Gpu = 2,
    Memory = 4,
    Motherboard = 8,
    Storage = 16,
    Network = 32,
    Controller = 64,
    Battery = 128,
    Psu = 256,
    All = Cpu | Gpu | Memory | Motherboard | Storage | Network | Controller | Battery | Psu
}

/// <summary>Fixed-size ring of samples. Written by the poll thread, read by the UI.</summary>
public sealed class SampleHistory(int capacity)
{
    private readonly float[] _buffer = new float[capacity];
    private int _start, _count;

    public int Capacity => capacity;

    public void Push(float value)
    {
        lock (_buffer)
        {
            if (_count < capacity) _buffer[(_start + _count++) % capacity] = value;
            else { _buffer[_start] = value; _start = (_start + 1) % capacity; }
        }
    }

    /// <summary>Copies the newest samples (oldest first) into <paramref name="target"/>; returns how many were written.</summary>
    public int CopyLatest(Span<float> target)
    {
        lock (_buffer)
        {
            var n = Math.Min(target.Length, _count);
            for (var i = 0; i < n; i++)
                target[i] = _buffer[(_start + _count - n + i) % capacity];
            return n;
        }
    }

    public void Clear()
    {
        lock (_buffer) _start = _count = 0;
    }
}

public sealed class SensorReading
{
    internal SensorReading(string id, string name, SensorKind kind, DeviceNode device, int historySize)
    {
        Id = id;
        Name = name;
        Kind = kind;
        Device = device;
        History = new SampleHistory(historySize);
    }

    public string Id { get; }
    public string Name { get; }
    public SensorKind Kind { get; }
    public DeviceNode Device { get; }
    public SampleHistory History { get; }

    public float? Value { get; internal set; }
    public float? Min { get; private set; }
    public float? Max { get; private set; }
    public double? Average => _samples == 0 ? null : _sum / _samples;

    private double _sum;
    private long _samples;

    internal void Record(float? value)
    {
        if (value is { } v && (float.IsNaN(v) || float.IsInfinity(v))) value = null;
        Value = value;
        if (value is not { } x) return;
        Min = Min is { } mn ? Math.Min(mn, x) : x;
        Max = Max is { } mx ? Math.Max(mx, x) : x;
        _sum += x;
        _samples++;
        History.Push(x);
    }

    public void ResetStatistics()
    {
        Min = Max = Value;
        _sum = 0;
        _samples = 0;
    }

    public override string ToString() => $"{Device.Name} / {Name}";
}

public sealed class DeviceNode
{
    internal DeviceNode(string id, string name, DeviceKind kind, DeviceNode? parent)
    {
        Id = id;
        Name = name;
        Kind = kind;
        Parent = parent;
    }

    public string Id { get; }
    public string Name { get; }
    public DeviceKind Kind { get; }
    public DeviceNode? Parent { get; }
    public List<SensorReading> Sensors { get; } = [];
    public List<DeviceNode> Children { get; } = [];

    public IEnumerable<SensorReading> AllSensors() => Sensors.Concat(Children.SelectMany(c => c.AllSensors()));
}
